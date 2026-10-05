using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Services.Common;
using WinnersPortal.Services.Database;
using WinnersPortal.Services.Settings;
using WinnersPortal.Services.Help;
using WinnersPortal.Services.Setup;

namespace WinnersPortal.Api.Setup;

/// <summary>
/// The API before it has a database: nothing in the environment names one
/// and no connection was saved beside the keys. Rather than guess — a
/// guessed localhost reaches whatever server answers there — the process
/// serves only what the setup page needs to connect one, behind the setup
/// token: its status, the form's defaults, a test, and the connect. The
/// connect proves the choice before keeping it: the tables are created and
/// the stored procedures installed first, and only then is the connection
/// written beside the keys, so a database the portal cannot use never
/// becomes the one it restarts onto. Then this small host stops, and
/// Program carries on building the real one on that database, in the same
/// process and with the same setup token, so the page goes on to the
/// administrator step.
/// </summary>
public static class DatabaseBootstrap
{
    /// <summary>True once a connection is saved; false when the process was asked to stop first.</summary>
    public static async Task<bool> RunAsync(string[] args, string keysDir, SetupToken token)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));
        var app = builder.Build();
        var log = app.Logger;
        var protection = DataProtectionProvider.Create(new DirectoryInfo(keysDir), b => b.SetApplicationName("WinnersPortal"));
        var provider = ProviderAsked(builder.Configuration, log);
        var state = new BootstrapState();
        var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        app.MapGet("/api/health", () => Results.Ok(new HealthResponse(Ok: true, TimeUtc: DateTimeOffset.UtcNow)));

        // The help the connect form's question marks open; static, so it needs no database.
        app.MapGet("/api/help", () => Results.Ok(new WinnersPortal.Api.Help.HelpResponse { Topics = HelpRegistry.All }));

        app.MapGet("/api/setup/status", () => Results.Ok(new SetupStatusResponse
        {
            Completed = false,
            NeedsDatabase = true,
            Connecting = state.Snapshot(),
        }));

        // The token check the page opens with, and the form's starting point.
        app.MapGet("/api/setup/defaults", (HttpRequest request) =>
            !token.Matches(request.Headers["X-Setup-Token"])
                ? Results.Unauthorized()
                : Results.Ok(new SetupDefaultsResponse
                {
                    Values = [],
                    FromEnvironment = [],
                    Database = null,
                    NeedsDatabase = true,
                    Connection = DatabaseConnections.Blank(provider),
                }));

        app.MapPost("/api/setup/connection/test", async (HttpRequest request, DatabaseConnectionFields fields, CancellationToken ct) =>
        {
            if (!token.Matches(request.Headers["X-Setup-Token"])) return Results.Unauthorized();
            if (DatabaseConnections.TryBuild(fields, null, out var chosen, out var connectionString) is { } problem)
                return Results.BadRequest(new ErrorResponse(problem));
            return Results.Ok(await DatabaseProbe.TestAsync(chosen, connectionString, null,
                protection.CreateProtector(SettingsService.ProtectorPurpose), ProbePurpose.Connect, ct));
        });

        app.MapPost("/api/setup/connection", (HttpRequest request, DatabaseConnectionFields fields) =>
        {
            if (!token.Matches(request.Headers["X-Setup-Token"])) return Results.Unauthorized();
            if (DatabaseConnections.TryBuild(fields, null, out var chosen, out var connectionString) is { } problem)
                return Results.BadRequest(new ErrorResponse(problem));
            var (server, database) = DatabaseOverrideFile.Describe(chosen, connectionString);
            if (!state.TryBegin(server, database))
                return Results.Conflict(new ErrorResponse("A database is being connected already; this page follows it."));
            _ = Task.Run(() => ConnectAsync(chosen, connectionString, server, database));
            return Results.Accepted("/api/setup/status", state.Snapshot());
        });

        // Everything else waits for a database, and says so rather than 404.
        app.Map("/api/{**rest}", () => Results.Json(
            new ErrorResponse("No database is connected yet — open /setup to connect one."), statusCode: StatusCodes.Status503ServiceUnavailable));

        await app.StartAsync();
        log.LogWarning(
            "No database is connected: open /setup?token={Token} to connect one. (Pin the token with SETUP_TOKEN.)",
            token.Value);

        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (app.Lifetime.ApplicationStopping.Register(() => stopping.TrySetResult()))
            await Task.WhenAny(saved.Task, stopping.Task);

        await app.StopAsync();
        await app.DisposeAsync();
        return saved.Task.IsCompleted;

        async Task ConnectAsync(DatabaseProvider chosen, string connectionString, string server, string database)
        {
            try
            {
                state.SetPhase("checking");
                var probe = await DatabaseProbe.TestAsync(chosen, connectionString, null,
                    protection.CreateProtector(SettingsService.ProtectorPurpose), ProbePurpose.Connect, CancellationToken.None);
                if (probe.Action != "connect")
                    throw new InvalidOperationException(probe.Problem ?? "That database cannot be connected.");

                // What the real host does at every start, done here first: a
                // database it cannot migrate is refused now, with the reason,
                // instead of after it is kept.
                state.SetPhase("preparing");
                await using (var db = new AppDbContext(AppDbContextOptions.Build(chosen, connectionString)))
                {
                    if (chosen == DatabaseProvider.SqlServer) await SqlServerRequirements.CheckAsync(db, CancellationToken.None);
                    await db.Database.MigrateAsync();
                    await StoredProcedures.ApplyAsync(db, CancellationToken.None);
                }

                state.SetPhase("recording");
                DatabaseOverrideFile.Write(DatabaseOverrideFile.PathIn(keysDir),
                    new DatabaseOverride(chosen, server, database, connectionString, DateTimeOffset.UtcNow, "the setup page"),
                    protection.CreateProtector(DatabaseOverrideFile.Purpose));
                log.LogWarning("Connected {Provider} at {Server}/{Database}, recorded in {File}; starting on it.",
                    DatabaseProviders.Label(chosen), server, database, DatabaseOverrideFile.PathIn(keysDir));
                state.SetPhase("starting");
                saved.TrySetResult();
            }
            catch (Exception e)
            {
                log.LogError("Connecting {Server}/{Database} failed: {Message}", server, database,
                    DatabaseConnections.Scrub(e.Message, connectionString));
                state.Fail(DatabaseConnections.Scrub(e.Message, connectionString));
            }
        }
    }

    /// <summary>The kind of database the form starts on: DATABASE__PROVIDER when it names one, SQL Server otherwise.</summary>
    private static DatabaseProvider ProviderAsked(IConfiguration config, ILogger log)
    {
        try
        {
            return DatabaseProviders.Parse(config[DatabaseProviders.ConfigKey]);
        }
        catch (InvalidOperationException e)
        {
            log.LogWarning("{Message} The setup page starts on SQL Server.", e.Message);
            return DatabaseProviders.Default;
        }
    }

    /// <summary>The one connect this host runs, as the page polls it.</summary>
    private sealed class BootstrapState
    {
        private readonly object _gate = new();
        private SetupConnectingResponse? _now;

        public bool TryBegin(string server, string database)
        {
            lock (_gate)
            {
                if (_now is { Phase: not "failed" }) return false;
                _now = new SetupConnectingResponse { Phase = "checking", Server = server, Database = database, Error = null };
                return true;
            }
        }

        public void SetPhase(string phase)
        {
            lock (_gate) _now = _now! with { Phase = phase };
        }

        public void Fail(string error)
        {
            lock (_gate) _now = _now! with { Phase = "failed", Error = error };
        }

        public SetupConnectingResponse? Snapshot()
        {
            lock (_gate) return _now;
        }
    }
}
