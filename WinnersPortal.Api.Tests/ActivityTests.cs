using Microsoft.AspNetCore.Mvc.Controllers;
using WinnersPortal.Api.Middleware;
using WinnersPortal.Api.Activity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WinnersPortal.Domain;
using WinnersPortal.Services.Activity;
using WinnersPortal.Services.Ai;
using WinnersPortal.Services.Auth;
using WinnersPortal.Services.Opportunities;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Services.Help;
using WinnersPortal.Services.Settings;
using Xunit;

namespace WinnersPortal.Api.Tests;

/// <summary>
/// The activity log's pure half: which requests earn a row, what the row
/// says, what may never land in one, and how long rows are kept. The one
/// test that matters most maps every endpoint the API has and holds each
/// write to having a name — an unnamed action is a row an administrator
/// has to decode.
/// </summary>
public class ActivityTests
{
    [Theory]
    [InlineData("/api/admin/users/{id:guid}/lock", "/api/admin/users/{id}/lock")]
    [InlineData("/api/opportunities/{slug}/applications", "/api/opportunities/{slug}/applications")]
    [InlineData("/api/opportunities/", "/api/opportunities")]
    [InlineData("api/profile", "/api/profile")]
    [InlineData("/", "/")]
    public void Patterns_are_normalized_to_the_spelling_the_names_use(string raw, string expected) =>
        Assert.Equal(expected, ActivityNames.Normalize(raw));

    [Theory]
    [InlineData("POST", "/api/opportunities/{slug}/applications", true)]
    [InlineData("DELETE", "/api/admin/users/{id}", true)]
    [InlineData("POST", "/api/nothing/like/this", true)] // unknown writes still count
    [InlineData("GET", "/api/attachments/{id}/download", true)]
    [InlineData("GET", "/api/github/callback", true)]
    [InlineData("GET", "/api/dashboard", false)]
    [InlineData("GET", "/api/opportunities/{slug}", false)]
    [InlineData("POST", "/api/webhooks/github", false)]
    [InlineData("POST", "/api/activity/visit", false)]
    [InlineData("GET", "/api/health", false)]
    public void Writes_and_downloads_earn_a_row_and_reads_and_machines_do_not(string method, string pattern, bool worth) =>
        Assert.Equal(worth, ActivityNames.Worth(method, pattern));

    [Fact]
    public void A_named_request_reads_as_words_and_an_unnamed_one_as_itself()
    {
        Assert.Equal("Applied to an opportunity", ActivityNames.Describe("post", "/api/opportunities/{slug}/applications"));
        Assert.Equal("POST /api/nothing", ActivityNames.Describe("POST", "/api/nothing"));
    }

    /// <summary>
    /// Every controller action the API declares, read off the endpoint data
    /// sources the way the middleware reads them. The hub is not an action;
    /// the webhook is one, and ActivityNames.Worth says it is never a row.
    /// </summary>
    private static IEnumerable<(string Method, string Pattern)> EveryRequest()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddControllers().AddApplicationPart(typeof(ActivityController).Assembly);
        var app = builder.Build();
        app.MapControllers();

        return ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(d => d.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<ControllerActionDescriptor>() is not null)
            .SelectMany(e => (e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["GET"])
                .Select(m => (Method: m, Pattern: ActivityNames.Normalize(ActivityLogMiddleware.PatternText(e.RoutePattern)))))
            .Distinct()
            .ToList();
    }

    [Fact]
    public void Every_endpoint_that_does_something_has_a_name()
    {
        var unnamed = EveryRequest()
            .Where(x => ActivityNames.Worth(x.Method, x.Pattern) && !ActivityNames.Described(x.Method, x.Pattern))
            .Select(x => $"{x.Method} {x.Pattern}")
            .ToList();

        Assert.True(unnamed.Count == 0,
            "These requests would be logged by method and path; give each a name in ActivityNames:\n  "
            + string.Join("\n  ", unnamed));
    }

    [Fact]
    public void Every_name_points_at_an_endpoint_that_exists()
    {
        var real = EveryRequest().Select(x => $"{x.Method} {x.Pattern}").ToHashSet(StringComparer.Ordinal);
        var orphans = ActivityNames.Named.Where(n => !real.Contains(n)).ToList();
        Assert.True(orphans.Count == 0,
            "These names describe requests the API no longer has:\n  " + string.Join("\n  ", orphans));
    }

    [Theory]
    [InlineData("/reset-password?token=abc123", "/reset-password")] // a token must never be logged
    [InlineData("/opportunities/foo#board", "/opportunities/foo")]
    [InlineData("/opportunities/foo/", "/opportunities/foo")]
    [InlineData("/", "/")]
    [InlineData("  /dashboard  ", "/dashboard")]
    public void A_page_is_kept_as_a_path_and_nothing_else(string given, string expected) =>
        Assert.Equal(expected, ActivityNames.CleanPage(given));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("dashboard")]
    [InlineData("//evil.example/phish")]
    [InlineData("https://evil.example/")]
    public void What_is_not_a_path_on_this_portal_is_not_a_page(string? given) =>
        Assert.Null(ActivityNames.CleanPage(given));

    [Fact]
    public void A_page_longer_than_the_column_is_cut()
    {
        var page = "/" + new string('a', 500);
        Assert.Equal(ActivityNames.MaxPath, ActivityNames.CleanPage(page)!.Length);
    }

    [Fact]
    public void The_referer_becomes_its_path()
    {
        Assert.Equal("/opportunities/foo", ActivityNames.PageOf("http://localhost/opportunities/foo?x=1"));
        Assert.Equal("/dashboard", ActivityNames.PageOf("/dashboard"));
        Assert.Null(ActivityNames.PageOf(null));
        Assert.Null(ActivityNames.PageOf("not a url"));
    }

    [Theory]
    [InlineData("/", "Opened the landing page")]
    [InlineData("/opportunities", "Opened the opportunities feed")]
    [InlineData("/opportunities/some-slug", "Opened an opportunity")]
    [InlineData("/opportunities/some-slug/apply", "Opened an opportunity's application form")]
    [InlineData("/client/opportunities/new", "Opened the new opportunity form")]
    [InlineData("/dashboard", "Opened the dashboard")]
    [InlineData("/profile/0f3c", "Opened somebody's profile")]
    [InlineData("/admin/activity", "Opened Admin → Activity")]
    [InlineData("/reports/opportunities", "Opened the opportunities report")]
    [InlineData("/reports/applications", "Opened the applications report")]
    [InlineData("/reports/entries", "Opened the entries report")]
    [InlineData("/somewhere/else", "Opened /somewhere/else")]
    public void A_visit_is_named_by_the_page(string page, string expected) =>
        Assert.Equal(expected, ActivityNames.PageName(page));

    [Fact]
    public void The_subject_is_the_route_and_only_the_route()
    {
        var values = new Dictionary<string, object?> { ["slug"] = "inventory-dashboard", ["feature"] = "coach" };
        Assert.Equal("inventory-dashboard · coach", ActivityNames.Subject(values));
        Assert.Null(ActivityNames.Subject(new Dictionary<string, object?>()));
    }

    [Theory]
    [InlineData(null, 90)]
    [InlineData("", 90)]
    [InlineData("abc", 90)]
    [InlineData("-5", 90)]
    [InlineData("0", 0)]
    [InlineData("365", 365)]
    public void Retention_reads_a_day_count_and_falls_back_to_ninety(string? configured, int days) =>
        Assert.Equal(days, ActivityRetention.Days(configured));

    [Fact]
    public void Zero_retention_keeps_everything()
    {
        var now = new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
        Assert.Null(ActivityRetention.CutOff("0", now));
        Assert.Equal(now.AddDays(-30), ActivityRetention.CutOff("30", now));
    }

    [Fact]
    public void The_administrators_read_translates_to_sql_with_every_filter_on()
    {
        // No database: EF renders the SQL from the model alone, which is
        // enough to catch the two ways this query breaks — a filter that
        // cannot translate, and a projection off the nullable account.
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=nowhere;Database=none;Username=x;Password=x")
            .Options;
        using var db = new AppDbContext(options);
        var filter = new ActivityService.ActivityFilter(
            Guid.NewGuid(), "0123456789abcdef", "action", "members", "nadia%",
            DateTimeOffset.UtcNow.AddDays(-7), DateTimeOffset.UtcNow);

        var sql = ActivityService.PageQuery(db, filter, pageIndex: 2, pageSize: 50).ToQueryString();

        Assert.Contains("LEFT JOIN \"Users\"", sql);
        Assert.Contains("ILIKE", sql);
        Assert.Contains("\"Detail\"", sql);
        Assert.Contains("OFFSET", sql);
        Assert.Contains("ORDER BY", sql);
        var count = ActivityService.Filter(db, filter with { Who = "visitors", Kind = "visit" }).ToQueryString();
        Assert.Contains("IS NULL", count);
        var calls = ActivityService.Filter(db, filter with { Kind = "external", Service = "identity" }).ToQueryString();
        Assert.Contains("\"Service\"", calls);
        Assert.Contains("'external'", calls);
        // The AI filters read the row's Subject, "openai/gpt-5.5".
        var ai = ActivityService.Filter(db, filter with { AiProvider = "openai", AiModel = "openai/gpt-5.5" }).ToQueryString();
        Assert.Contains("\"Subject\"", ai);
        Assert.Contains("LIKE", ai);
        Assert.Contains("'ai'", ai);
        var models = ActivityService.AiModelsQuery(db).ToQueryString();
        Assert.Contains("GROUP BY", models);
        Assert.Contains("max(", models, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("openai", "openai")]
    [InlineData("Gemini", "gemini")]
    [InlineData("stripe", null)]
    [InlineData(null, null)]
    public void The_provider_filter_takes_only_a_provider_the_portal_calls(string? asked, string? applied)
    {
        Assert.Equal(applied, ActivityService.AiProviderFilter(asked));
    }

    [Theory]
    [InlineData("openai/gpt-5.5", "openai/gpt-5.5")]
    [InlineData(" gemini/gemini-3.6-flash ", "gemini/gemini-3.6-flash")]
    [InlineData("gpt-5.5", null)]
    [InlineData("openai/", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void The_model_filter_takes_a_whole_provider_and_model(string? asked, string? applied)
    {
        Assert.Equal(applied, ActivityService.AiModelFilter(asked));
    }

    [Fact]
    public void An_AI_call_is_named_by_its_settings_key_and_model()
    {
        Assert.Equal("anthropic/claude-sonnet-5", AiProviders.CallName("anthropic", "claude-sonnet-5"));
        Assert.Equal(("anthropic", "claude-sonnet-5"), AiProviders.ParseCallName("anthropic/claude-sonnet-5"));
        Assert.Null(AiProviders.ParseCallName("Called OpenAI"));
    }

    [Fact]
    public void The_subject_a_caller_sets_rides_the_request()
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
            .WithSubject("openai/gpt-5.5");
        Assert.True(message.Options.TryGetValue(ExternalExchange.SubjectKey, out var subject));
        Assert.Equal("openai/gpt-5.5", subject);
    }

    [Fact]
    public void The_detail_a_caller_sets_rides_the_request_beside_the_subject()
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
            .WithSubject("openai/gpt-5.5")
            .WithDetail(AiPrompts.CallDetail(AiFeature.EntryDigest));
        Assert.True(message.Options.TryGetValue(ExternalExchange.DetailKey, out var detail));
        Assert.Equal("entryDigest · prompt v2", detail);
        Assert.True(message.Options.TryGetValue(ExternalExchange.SubjectKey, out _));
    }

    [Fact]
    public void The_visit_endpoint_passes_the_gates_an_unconfirmed_account_owes()
    {
        // A page opened by somebody still at the door is a page opened; the
        // beacon must not be the thing that answers 403.
        Assert.True(Confirmation.GateAllows(new PathString("/api/activity/visit")));
        Assert.True(Terms.GateAllows(new PathString("/api/activity/visit"), HttpMethods.Post));
        Assert.False(Terms.GateAllows(new PathString("/api/admin/activity"), HttpMethods.Post));
    }

    [Theory]
    [InlineData(true, false, "Selected an applicant")]
    [InlineData(false, false, "Passed on an applicant")]
    [InlineData(false, true, "Took a selection back")]
    public void A_decision_is_named_by_its_direction(bool selected, bool takenBack, string expected) =>
        Assert.Equal(expected, ActivityNames.Decision(selected, takenBack));

    [Theory]
    [InlineData(true, false, "Saved a new opportunity draft")]
    [InlineData(true, true, "Saved a new opportunity to publish it")]
    [InlineData(false, false, "Edited an opportunity draft")]
    [InlineData(false, true, "Saved an opportunity draft to publish it")]
    public void An_opportunity_save_says_whether_it_was_on_the_way_to_publishing(bool isNew, bool publishing, string expected) =>
        Assert.Equal(expected, ActivityNames.OpportunitySaved(isNew, publishing));

    [Fact]
    public void A_handler_that_names_its_subject_is_believed_over_the_route_and_cut_short()
    {
        var log = new ActivityLog(Microsoft.Extensions.Logging.Abstractions.NullLogger<ActivityLog>.Instance);
        var services = new ServiceCollection().AddSingleton(log).AddScoped<ActivityNote>().BuildServiceProvider();

        static HttpContext Save(IServiceProvider services, string? about)
        {
            var ctx = new DefaultHttpContext { RequestServices = services.CreateScope().ServiceProvider };
            ctx.Request.Method = "POST";
            ctx.Request.Path = "/api/opportunities";
            ctx.Response.StatusCode = 200;
            ctx.SetEndpoint(new RouteEndpoint(
                _ => Task.CompletedTask,
                Microsoft.AspNetCore.Routing.Patterns.RoutePatternFactory.Parse("/api/opportunities"),
                0, null, null));
            if (about is not null) ctx.RequestServices.GetRequiredService<ActivityNote>().Subject = about;
            return ctx;
        }

        // A new opportunity has no id in its path: without the handler's word there is no subject at all.
        ActivityLogMiddleware.Capture(Save(services, null));
        Assert.True(log.Reader.TryRead(out var bare));
        Assert.Null(bare!.Subject);
        Assert.Equal("Saved a new opportunity draft", bare.Action);

        ActivityLogMiddleware.Capture(Save(services, "inventory-dashboard"));
        Assert.True(log.Reader.TryRead(out var named));
        Assert.Equal("inventory-dashboard", named!.Subject);

        ActivityLogMiddleware.Capture(Save(services, new string('s', 1000)));
        Assert.True(log.Reader.TryRead(out var cut));
        Assert.Equal(ActivityNames.MaxSubject, cut!.Subject!.Length);
    }

    [Fact]
    public void A_handler_that_names_its_own_row_is_believed_and_the_route_name_is_the_fallback()
    {
        var log = new ActivityLog(Microsoft.Extensions.Logging.Abstractions.NullLogger<ActivityLog>.Instance);
        var services = new ServiceCollection().AddSingleton(log).AddScoped<ActivityNote>().BuildServiceProvider();

        static HttpContext Decide(IServiceProvider services, string? said)
        {
            var ctx = new DefaultHttpContext { RequestServices = services.CreateScope().ServiceProvider };
            ctx.Request.Method = "POST";
            ctx.Request.Path = "/api/applications/6f9619ff-8b86-d011-b42d-00c04fc964ff/decide";
            ctx.Request.RouteValues["id"] = "6f9619ff-8b86-d011-b42d-00c04fc964ff";
            ctx.Response.StatusCode = 200;
            var endpoint = new RouteEndpoint(
                _ => Task.CompletedTask,
                Microsoft.AspNetCore.Routing.Patterns.RoutePatternFactory.Parse("/api/applications/{id:guid}/decide"),
                0, null, null);
            ctx.SetEndpoint(endpoint);
            if (said is not null) ctx.RequestServices.GetRequiredService<ActivityNote>().Action = said;
            return ctx;
        }

        ActivityLogMiddleware.Capture(Decide(services, ActivityNames.Decision(true, false)));
        Assert.True(log.Reader.TryRead(out var named));
        Assert.Equal("Selected an applicant", named!.Action);
        Assert.Equal("6f9619ff-8b86-d011-b42d-00c04fc964ff", named.Subject);

        ActivityLogMiddleware.Capture(Decide(services, null));
        Assert.True(log.Reader.TryRead(out var fallback));
        Assert.Equal("Decided an application", fallback!.Action);

        // Whatever a handler says is a name, not a body: it is cut like a subject.
        ActivityLogMiddleware.Capture(Decide(services, new string('x', 1000)));
        Assert.True(log.Reader.TryRead(out var cut));
        Assert.Equal(ActivityNames.MaxSubject, cut!.Action.Length);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    [InlineData("  The brief changed under me.  ", "The brief changed under me.")]
    [InlineData("Took a full-time role.\nSorry.", "Took a full-time role.\nSorry.")]
    public void A_reason_is_trimmed_and_a_blank_one_is_none(string? given, string? kept) =>
        Assert.Equal(kept, ActivityNames.Detail(given));

    [Fact]
    public void A_reason_a_handler_passes_is_kept_and_cut_at_a_removal_reasons_length()
    {
        Assert.Equal(Removal.MaxReason, ActivityNames.MaxDetail);
        var log = new ActivityLog(Microsoft.Extensions.Logging.Abstractions.NullLogger<ActivityLog>.Instance);
        var services = new ServiceCollection().AddSingleton(log).AddScoped<ActivityNote>().BuildServiceProvider();

        static HttpContext Withdraw(IServiceProvider services, string? said)
        {
            var ctx = new DefaultHttpContext { RequestServices = services.CreateScope().ServiceProvider };
            ctx.Request.Method = "POST";
            ctx.Request.Path = "/api/entries/6f9619ff-8b86-d011-b42d-00c04fc964ff/withdraw";
            ctx.Request.RouteValues["id"] = "6f9619ff-8b86-d011-b42d-00c04fc964ff";
            ctx.Response.StatusCode = 204;
            ctx.SetEndpoint(new RouteEndpoint(
                _ => Task.CompletedTask,
                Microsoft.AspNetCore.Routing.Patterns.RoutePatternFactory.Parse("/api/entries/{id:guid}/withdraw"),
                0, null, null));
            if (said is not null) ctx.RequestServices.GetRequiredService<ActivityNote>().Detail = said;
            return ctx;
        }

        ActivityLogMiddleware.Capture(Withdraw(services, "Took a full-time role."));
        Assert.True(log.Reader.TryRead(out var given));
        Assert.Equal("Withdrew from an opportunity", given!.Action);
        Assert.Equal("Took a full-time role.", given.Detail);

        // No reason, no detail — and a request body is never where one comes from.
        ActivityLogMiddleware.Capture(Withdraw(services, null));
        Assert.True(log.Reader.TryRead(out var silent));
        Assert.Null(silent!.Detail);

        ActivityLogMiddleware.Capture(Withdraw(services, "  " + new string('r', 1000)));
        Assert.True(log.Reader.TryRead(out var cut));
        Assert.Equal(ActivityNames.MaxDetail, cut!.Detail!.Length);
    }

    [Fact]
    public void The_activity_screen_and_its_setting_explain_themselves()
    {
        Assert.Contains("admin.activity", HelpRegistry.RequiredAdminTopicIds);
        Assert.NotNull(HelpRegistry.Find(HelpRegistry.TopicIdForSetting(ActivityRetention.Key)));
        Assert.NotNull(SettingsRegistry.Find(ActivityRetention.Key));
        Assert.Equal("90", SettingsRegistry.Find(ActivityRetention.Key)!.Default);
    }
}
