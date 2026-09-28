using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using WinnersPortal.Api.Activity;
using WinnersPortal.Api.Auth;
using WinnersPortal.Api.Common;
using WinnersPortal.Api.Live;
using WinnersPortal.Api.Middleware;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Services.Activity;
using WinnersPortal.Services.Admin;
using WinnersPortal.Services.Ai;
using WinnersPortal.Services.Auth;
using WinnersPortal.Services.Common;
using WinnersPortal.Services.Opportunities;
using WinnersPortal.Services.Dashboard;
using WinnersPortal.Services.Database;
using WinnersPortal.Services.Email;
using WinnersPortal.Services.GitHub;
using WinnersPortal.Services.Leaderboard;
using WinnersPortal.Services.Live;
using WinnersPortal.Services.Notifications;
using WinnersPortal.Services.Profiles;
using WinnersPortal.Services.Reports;
using WinnersPortal.Services.Settings;
using WinnersPortal.Services.Setup;
using WinnersPortal.Services.Storage;

var builder = WebApplication.CreateBuilder(args);

// --- data protection (secret settings are encrypted at rest) ------------
// The keys directory comes first: the database choice below may be a file
// beside the keys, protected under the same ring.
var keysDir = builder.Configuration["DP_KEYS_DIR"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "keys");
Directory.CreateDirectory(keysDir);
builder.Services.AddDataProtection()
    .SetApplicationName("WinnersPortal")
    .PersistKeysToFileSystem(new DirectoryInfo(keysDir));

// --- database -----------------------------------------------------------
// Which database, and where — decided before the host exists, because the
// settings table is behind the very connection being chosen. An in-app
// move writes its choice beside the keys and that file wins; otherwise
// DATABASE__PROVIDER (sqlserver unless it says postgres) and the
// connection string from the environment — a blank provider beside a
// string only PostgreSQL reads is an install from before SQL Server was
// the default, and stays on PostgreSQL with a warning. Development falls
// back to the stock compose database so a bare `dotnet run` works;
// anywhere else an unset string stops the process here naming the
// setting — the fallback would connect to whatever answers on localhost
// (on a box with a native server, the wrong one), and fail as a rejected
// password once per restart the service manager makes. SQL Server 2022
// or later is insisted on before the first migration.
var database = DatabaseSelection.Resolve(
    builder.Configuration, keysDir, builder.Environment.IsDevelopment(),
    // The same ring the host registers above, read a moment early; only
    // built when there is a file to read, so a fresh install creates no
    // key before the host has its own.
    () => DataProtectionProvider.Create(new DirectoryInfo(keysDir), b => b.SetApplicationName("WinnersPortal"))
        .CreateProtector(DatabaseOverrideFile.Purpose),
    warn: line => Console.Error.WriteLine("warn: " + line));
builder.Services.AddSingleton(database);

// --- redis ----------------------------------------------------------------
// Whether this process uses Redis, and where, is the redis settings group,
// off by default — read here, before the host, because the connection is
// opened and its subscriptions made as the process starts, and SignalR
// picks its backplane while the container is composed. A saved change is in
// force at the next restart, and the group's panel says when one is owed.
// REDIS_URL, the older spelling compose still uses, is read as both pins.
if (RedisSettings.LegacyPins(
        Environment.GetEnvironmentVariable(RedisSettings.LegacyEnv),
        Environment.GetEnvironmentVariable(SettingsRegistry.EnvVarName(RedisSettings.EnabledKey)),
        Environment.GetEnvironmentVariable(SettingsRegistry.EnvVarName(RedisSettings.UrlKey))) is (var onPin, var urlPin))
{
    Environment.SetEnvironmentVariable(SettingsRegistry.EnvVarName(RedisSettings.EnabledKey), onPin);
    Environment.SetEnvironmentVariable(SettingsRegistry.EnvVarName(RedisSettings.UrlKey), urlPin);
}
var redis = await RedisSettings.ReadAsync(database.Provider, database.ConnectionString,
    warn: line => Console.Error.WriteLine("warn: " + line), CancellationToken.None);

// --- the pages' origin, when it is not this one ---------------------------
// Same-origin unless the Branding settings name an API URL other than the
// Web URL (the two-site IIS layout: api.crm.com serving the API,
// web.crm.com the pages). Then that one origin may call the API from a
// page, with its cookie; the session cookie is scoped to the parent both
// hosts share so the web server sees it too; and a redirect to a page is
// sent to that host. Read per request, so a change is in force at once.
builder.Services.AddCors();
builder.Services.AddSingleton<ICorsPolicyProvider, SettingsCorsPolicyProvider>();

// Every command at or over the slow-query threshold is logged as a warning
// with its SQL and the request it served — the measurement any hand-tuning
// of a query has to start from — and tallied by query for Admin →
// Operations. The threshold is a setting (Limits & maintenance, 500 ms
// until one is read, 0 for off), carried in by a watcher so a change needs
// no restart.
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<SlowQueryStats>(_ => new SlowQueryStats());
builder.Services.AddSingleton(sp =>
{
    var http = sp.GetRequiredService<IHttpContextAccessor>();
    return new SlowQueryInterceptor(sp.GetRequiredService<SlowQueryStats>(), sp.GetRequiredService<ILogger<SlowQueryInterceptor>>(),
        () => http.HttpContext is { } h ? $"{h.Request.Method} {h.Request.Path}" : null);
});
builder.Services.AddHostedService<SlowQueryThresholdWatcher>();
// On SQL Server the day-to-day reads are T-SQL through Dapper on the
// context's own connection (AppDbContext.Sql); Database:Dapper=false is a
// diagnostic that serves the same database through the LINQ instead, so
// the two can be compared on one set of rows.
var dapper = builder.Configuration.GetValue("Database:Dapper", true);
builder.Services.AddDbContext<AppDbContext>((sp, o) =>
{
    AppDbContextOptions.Configure(o, database.Provider, database.ConnectionString, dapper);
    o.AddInterceptors(sp.GetRequiredService<SlowQueryInterceptor>());
});

// --- token signing (bound from the JWT settings once migrations have run) --
// Issuer, audience, key and lifetimes are settings, so they need the
// database; they are read once, below, and fixed for the life of the
// process. Nothing signs or checks a token before app.Run, so nothing asks
// for the service before it is there.
TokenService? tokens = null;
builder.Services.AddSingleton(_ => tokens
    ?? throw new InvalidOperationException("The token service is bound after migrations; nothing may use it before."));

// --- services -----------------------------------------------------------
builder.Services.AddSingleton(sp => new RedisConnection(redis, sp.GetRequiredService<ILogger<RedisConnection>>()));
builder.Services.AddSingleton<SettingsService>();
// The public lists, held for a few seconds so a crowd shares one read;
// a clear crosses to the other API processes over Redis where there is one.
builder.Services.AddSingleton<WinnersPortal.Services.Common.PublicReads>();
// The database move: a process-wide pause the workers and the gate honour,
// the one move's state, and the mover that runs it in the background.
builder.Services.AddSingleton<AppPause>();
builder.Services.AddSingleton<DatabaseMoveState>();
builder.Services.AddSingleton<DatabaseMover>();
builder.Services.AddSingleton<AiOptions>();
builder.Services.AddSingleton<SetupToken>();
builder.Services.AddScoped<SetupService>();
builder.Services.AddScoped<SetupTestLog>();
builder.Services.AddSingleton<WinnersPortal.Services.Profiles.PaymentSecrets>();
builder.Services.AddHostedService<FirstRunHostedService>();

// --- activity log (every request that did something, every page opened, --
// and every call to a third-party API: each named client below records its
// own through RecordExternalCalls) ------------------------------------------
builder.Services.AddSingleton<ActivityLog>();
builder.Services.AddHostedService<ActivityWriter>();

// --- github --------------------------------------------------------------
builder.Services.AddHttpClient(GitHubService.HttpClientName, c =>
{
    c.DefaultRequestHeaders.UserAgent.ParseAdd("WinnersPortal");
    c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    c.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    c.Timeout = TimeSpan.FromSeconds(30);
}).RecordExternalCalls(ExternalServices.GitHub);
builder.Services.AddHttpClient(GitHubService.ZipballHttpClientName, c =>
{
    c.DefaultRequestHeaders.UserAgent.ParseAdd("WinnersPortal");
    c.Timeout = TimeSpan.FromMinutes(5); // archives, not API calls
}).RecordExternalCalls(ExternalServices.GitHub);
builder.Services.AddSingleton<GitHubService>();
builder.Services.AddSingleton<GitHubWorkSignal>();

// --- storage (signed URLs to read; uploads written from here) ------------
builder.Services.AddHttpClient(WinnersPortal.Services.Storage.StorageService.HttpClientName,
    c => c.Timeout = TimeSpan.FromMinutes(5)).RecordExternalCalls(ExternalServices.Storage);
builder.Services.AddSingleton<WinnersPortal.Services.Storage.StorageService>();

// --- email (an SMTP server, or Mailgun's or Brevo's HTTPS API) -----------
builder.Services.AddHttpClient(EmailSender.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(30))
    .RecordExternalCalls(ExternalServices.Email);
builder.Services.AddSingleton<EmailWorkSignal>();
builder.Services.AddSingleton<EmailSender>();

// --- web push (the email outbox's twin; the VAPID pair generates itself) --
builder.Services.AddHttpClient(PushWorker.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(20))
    .RecordExternalCalls(ExternalServices.Push);
builder.Services.AddSingleton<PushKeys>();
builder.Services.AddSingleton<UnsubscribeTokens>();
builder.Services.AddSingleton<PushWorkSignal>();

// --- ai (drafts in the background; nothing in the request path) ----------
builder.Services.AddHttpClient(WinnersPortal.Services.Ai.AiProviderClient.HttpClientName,
    c => c.Timeout = TimeSpan.FromSeconds(100)) // generation, not a ping
    .RecordExternalCalls(ExternalServices.Ai);
builder.Services.AddSingleton<WinnersPortal.Services.Ai.AiProviderClient>();
builder.Services.AddSingleton<WinnersPortal.Services.Ai.AiQuota>();
builder.Services.AddSingleton<WinnersPortal.Services.Ai.AiWorkSignal>();

// --- identity verification (a session opened, a verdict read, and its proof — the decision and --
// --- copies of the document images — kept for administrators by the proof worker) --------------
builder.Services.AddHttpClient(WinnersPortal.Services.Identity.IdentityProviderClient.HttpClientName,
    c => c.Timeout = TimeSpan.FromSeconds(15)).RecordExternalCalls(ExternalServices.Identity);
builder.Services.AddSingleton<WinnersPortal.Services.Identity.IdentityOptions>();
builder.Services.AddSingleton<WinnersPortal.Services.Identity.IdentityProviderClient>();
builder.Services.AddSingleton<WinnersPortal.Services.Identity.IdentityProofWorkSignal>();

// --- build host (a Linux server of its own that builds each claimed milestone of an opportunity --
// --- requiring Docker Compose; the preview worker drives it, the settings test proves it) ----
builder.Services.AddHttpClient(WinnersPortal.Services.Preview.PreviewHostClient.HttpClientName,
    c => c.Timeout = TimeSpan.FromSeconds(30)).RecordExternalCalls(ExternalServices.Preview);
builder.Services.AddSingleton<WinnersPortal.Services.Preview.PreviewHostClient>();
builder.Services.AddSingleton<WinnersPortal.Services.Preview.PreviewWorkSignal>();

// --- the background workers (one process's work; WORKERS=false elsewhere) --
// GitHub provisioning and the deadline sweep, the two outboxes, AI drafting,
// identity proof and the daily merit snapshot act on what the database holds pending, and
// do not coordinate: two processes running them would each send every
// email. With a second API process, exactly one runs them and the others
// start with WORKERS=false; what those queue wakes the one that does, over
// Redis. The activity log's writer and the slow-query watcher above serve
// their own process, and every process keeps them.
var workers = BackgroundWork.RunsHere(builder.Configuration);
builder.Services.AddBackgroundWork(workers);

// --- live board (SignalR; the hub carries nudges, never data) -------------
// Where Redis is on, SignalR keeps its groups in Redis (the backplane),
// so a nudge sent by one API process reaches the pages connected to another.
// It rides the portal's one Redis connection; its channels are named after
// the hub (WinnersPortal.Api.Live.OpportunityHub:...), which keeps them apart
// from the settings and public-list channels. A prefix would need a second
// connection: SignalR applies one only to a connection it makes itself.
// Unset, the groups live in this process, which is all one process needs.
// --- controllers (every /api route is an action; IResult answers, as before) --
builder.Services.AddControllers();

// --- feature services (what the controllers do, one per area) -----------
// Scoped, like the context they read and write through. A controller binds
// the request, calls one of these, and turns its Outcome into the answer.
builder.Services.AddScoped<ActivityNote>();
builder.Services.AddScoped<AccountGates>();
builder.Services.AddScoped<LiveOpportunities>();
builder.Services.AddScoped<ActivityService>();
builder.Services.AddScoped<AdminService>();
builder.Services.AddScoped<DatabaseAdminService>();
builder.Services.AddScoped<UserAdminService>();
builder.Services.AddScoped<AiService>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<WinnersPortal.Services.Identity.IdentityService>();
builder.Services.AddScoped<WinnersPortal.Services.Identity.IdentityProofService>();
builder.Services.AddScoped<ApplicationService>();
builder.Services.AddScoped<AwardService>();
builder.Services.AddScoped<CancelService>();
builder.Services.AddScoped<OpportunityService>();
builder.Services.AddScoped<EntryService>();
builder.Services.AddScoped<RatingService>();
builder.Services.AddScoped<DashboardService>();
builder.Services.AddScoped<GitHubAuthService>();
builder.Services.AddScoped<GitHubWebhookService>();
builder.Services.AddScoped<LeaderboardService>();
builder.Services.AddScoped<TalentService>();
builder.Services.AddScoped<NotificationService>();
builder.Services.AddScoped<ProfileService>();
builder.Services.AddScoped<ReportService>();
builder.Services.AddScoped<SettingsAdminService>();
builder.Services.AddScoped<AttachmentService>();
builder.Services.AddScoped<EntryZipService>();
builder.Services.AddScoped<SubmissionService>();
builder.Services.AddScoped<WinnersPortal.Services.Preview.CheckpointBuildService>();
builder.Services.AddScoped<WinnersPortal.Services.Preview.PreviewService>();

var signalR = builder.Services.AddSignalR();
if (redis.Active is not null)
{
    signalR.AddStackExchangeRedis();
    builder.Services.AddOptions<Microsoft.AspNetCore.SignalR.StackExchangeRedis.RedisOptions>()
        .Configure<RedisConnection>((o, redis) => o.ConnectionFactory = _ => Task.FromResult(
            redis.Muxer ?? throw new InvalidOperationException(
                "Redis is on but the portal could not connect to it; the live board needs it. See the startup warning.")));
}
builder.Services.AddSingleton<ILiveBoard, LiveBoard>();

// --- captcha (Cloudflare Turnstile server-side verification) -------------
builder.Services.AddHttpClient(Captcha.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(10))
    .RecordExternalCalls(ExternalServices.Captcha);

// --- phone (the SMS gateway confirmation codes are texted through) -------
builder.Services.AddHttpClient(WinnersPortal.Services.Phone.PhoneSender.HttpClientName,
    c => c.Timeout = TimeSpan.FromSeconds(10)).RecordExternalCalls(ExternalServices.Sms);
builder.Services.AddSingleton<WinnersPortal.Services.Phone.PhoneSender>();

// --- auth ---------------------------------------------------------------
// One scheme, JWT bearer, two transports: the browser's HttpOnly cookie
// (wp.auth, a week, sliding) and the Authorization header a client that
// holds no cookie sends, with a short access token (an hour unless the JWT
// settings say otherwise) it renews through the refresh endpoint. The
// handler answers 401/403 with status codes, never a redirect — the callers
// are API consumers.
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        // The token's claim names are kept as written (sub, email, role),
        // not mapped to the .NET URIs; Principal reads the short names.
        o.MapInboundClaims = false;
        o.SaveToken = false;
        o.Events = new JwtBearerEvents
        {
            OnMessageReceived = SessionValidation.OnMessageReceived,
            // A lock, an erasure, or a new session stamp ends the session on
            // the next request, not at the token's natural end a week away.
            OnTokenValidated = SessionValidation.OnTokenValidated,
            OnAuthenticationFailed = SessionValidation.OnAuthenticationFailed,
        };
    });
// Validation reads the bound service, which the options first ask for on
// the first request — after the binding below.
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<TokenService>((o, bound) => o.TokenValidationParameters = bound.ValidationParameters);
builder.Services.AddAuthorization(o =>
{
    o.AddPolicy("admin", p => p.RequireRole("admin"));
    o.AddPolicy("client", p => p.RequireRole("client"));
    o.AddPolicy("freelancer", p => p.RequireRole("freelancer"));
});

var app = builder.Build();

// Caddy is the only ingress in compose; trust its X-Forwarded-* headers.
var forwarded = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
};
forwarded.KnownNetworks.Clear();
forwarded.KnownProxies.Clear();
app.UseForwardedHeaders(forwarded);

// Where the pages are, resolved once per request from the settings; then,
// ahead of authentication and the gates, the CORS policy built from it —
// a preflight carries no cookie and must be answered, not refused, for
// the request behind it to follow. Same-origin, there is no policy and
// nothing here touches the request.
app.UseMiddleware<WebOriginMiddleware>();
app.UseCors();

app.UseAuthentication();
app.UseAuthorization();

// A delivery from GitHub or the identity provider is a third-party row,
// with what arrived and what the portal answered — ahead of the gates, so
// one the maintenance gate refused is recorded with that answer.
app.UseMiddleware<WebhookLogMiddleware>();

// After authentication, so a row knows who; ahead of the gates, so a
// request one of them refused is recorded with the answer it got.
app.UseMiddleware<ActivityLogMiddleware>();

// After authentication, so the admin exemption can see the role.
app.UseMiddleware<MaintenanceGateMiddleware>();

// An account that has not yet proved an email or a phone can do nothing
// but prove one: every call under /api but the session's own answers 403.
app.UseMiddleware<ConfirmationGateMiddleware>();

// Also after authentication: an owed acceptance belongs to a signed-in
// account. Reads and the session's own endpoints pass; other writes wait.
app.UseMiddleware<TermsGateMiddleware>();

app.MapGet("/api/health", () => Results.Ok(new HealthResponse(Ok: true, TimeUtc: DateTimeOffset.UtcNow)));
app.MapControllers();
app.MapHub<WinnersPortal.Api.Live.OpportunityHub>("/api/live/opportunities");

// Apply migrations before anything runs; FirstRunHostedService needs the schema.
// On SQL Server, first the version and the full-text feature the model
// needs: a server that will not do stops the process here, naming what
// to install, rather than failing a migration halfway through — and after
// the migrations, the stored procedures this build calls, installed whole.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var (server, name) = database.Describe();
    if (database.Provider == DatabaseProvider.SqlServer)
    {
        var report = await SqlServerRequirements.CheckAsync(db, CancellationToken.None);
        app.Logger.LogInformation("Database: SQL Server {Version} ({Edition}) at {Server}/{Database}, full-text installed; chosen by {Source}.",
            report.ProductVersion, report.Edition, server, name, database.Source);
    }
    else
    {
        app.Logger.LogInformation("Database: PostgreSQL at {Server}/{Database}; chosen by {Source}.", server, name, database.Source);
    }
    await db.Database.MigrateAsync();
    await StoredProcedures.ApplyAsync(db, CancellationToken.None);
}
if (redis.Active is not null)
    app.Logger.LogInformation("Redis: on, at {Endpoints}; chosen by {Source}.", redis.Endpoints, redis.Source);
else
    app.Logger.LogInformation("Redis: off ({Source}); settings invalidation, the public lists' clears, the workers' wakes and the live board stay in this process.", redis.Source);
BackgroundWork.Announce(app.Logger, workers, app.Services.GetRequiredService<RedisConnection>().Muxer is not null);

// Bind token signing from the JWT settings: a change saved there takes
// effect here, at the next start.
tokens = new TokenService(await JwtSettings.LoadAsync(
    app.Services.GetRequiredService<SettingsService>(),
    keysDir,
    app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("WinnersPortal.Auth.Jwt"),
    CancellationToken.None));

app.Run();

// A database move ends the process on purpose once its choice is written,
// and relies on the service manager to bring it back — which WinSW does
// only for a non-zero exit, and compose does under restart: unless-stopped.
return app.Services.GetRequiredService<DatabaseMoveState>().RestartRequested ? 3 : 0;

/// <summary>The health check's answer: the API is up, and its clock.</summary>
public sealed record HealthResponse(bool Ok, DateTimeOffset TimeUtc);
