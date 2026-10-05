using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using WinnersPortal.Api.Activity;
using WinnersPortal.Services.Activity;
using WinnersPortal.Services.Preview;
using WinnersPortal.Services.Settings;
using Xunit;

namespace WinnersPortal.Api.Tests;

/// <summary>
/// The wire to the build host: a setup's fields to a connection, each call
/// as the agent expects it, each answer read back — and, because the
/// activity log records every call, the two tokens never reaching a row.
/// </summary>
public class PreviewHostTests
{
    private static SetupValues Values(string? url, string? token, string? timeout = null) =>
        new(Setups.MainId, Setups.MainName, true, new Dictionary<string, string?>
        {
            [PreviewHostKeys.HostUrl] = url,
            [PreviewHostKeys.Token] = token,
            [PreviewHostKeys.BuildTimeoutMinutes] = timeout,
        });

    private static readonly PreviewHostConfig Host =
        new(new Uri("https://build.example.com/"), "agent-token-123456", TimeSpan.FromMinutes(15));

    // ----------------------------------------------- whether builds are on

    private static SetupTestDto Test(bool ok, bool changed = false) =>
        new(ok, ok ? "Docker 27 · Compose 2.29" : "The agent did not answer.", DateTimeOffset.UtcNow, "admin@example.com", changed);

    [Fact]
    public void Builds_are_on_only_for_an_active_complete_host_whose_last_test_still_stands()
    {
        var host = Values("https://build.example.com", "agent-token-123456");

        var on = BuildHostState.Judge(host, Test(ok: true));
        Assert.True(on.Ready);
        Assert.Equal("build.example.com", on.Config!.Host.Host);

        Assert.Equal(BuildHostState.NoneActive, BuildHostState.Judge(null, null).Problem);
        Assert.Equal(BuildHostState.Incomplete, BuildHostState.Judge(Values("https://build.example.com", null), Test(ok: true)).Problem);
        Assert.Equal(BuildHostState.NotTested, BuildHostState.Judge(host, null).Problem);
        Assert.Equal(BuildHostState.TestFailed, BuildHostState.Judge(host, Test(ok: false)).Problem);
        Assert.Equal(BuildHostState.ChangedSinceTest, BuildHostState.Judge(host, Test(ok: true, changed: true)).Problem);
    }

    [Fact]
    public void A_host_not_ready_for_new_work_still_carries_what_is_already_on_it()
    {
        // The worker follows running builds and stops previews through the
        // active setup's connection whether or not its test still stands.
        var untested = BuildHostState.Judge(Values("https://build.example.com", "agent-token-123456"), null);
        Assert.False(untested.Ready);
        Assert.NotNull(untested.Config);
        Assert.Null(BuildHostState.Judge(null, null).Config);
    }

    // ---------------------------------------------------------- settings

    [Fact]
    public void A_setup_is_a_connection_once_it_has_an_address_and_a_token()
    {
        var c = PreviewHost.Config(Values("https://build.example.com", " agent-token-123456 ", "20"));
        Assert.NotNull(c);
        Assert.Equal("https://build.example.com/", c!.Host.ToString()); // a slash, so calls append to it
        Assert.Equal("agent-token-123456", c.Token);
        Assert.Equal(TimeSpan.FromMinutes(20), c.BuildTimeout);

        Assert.Null(PreviewHost.Config(Values(null, "agent-token-123456")));
        Assert.Null(PreviewHost.Config(Values("https://build.example.com", "  ")));
        Assert.Equal(TimeSpan.FromMinutes(PreviewHost.DefaultTimeoutMinutes),
            PreviewHost.Config(Values("https://build.example.com", "t", "not a number"))!.BuildTimeout);
    }

    [Theory]
    [InlineData("https://build.example.com", "https://build.example.com/")]
    [InlineData("https://build.example.com/agent", "https://build.example.com/agent/")]
    [InlineData("http://preview-agent:7070", "http://preview-agent:7070/")]
    [InlineData("build.example.com", null)]
    [InlineData("ftp://build.example.com", null)]
    [InlineData("https://build.example.com/?x=1", null)]
    public void The_address_is_an_http_origin_or_path_and_nothing_after(string given, string? parsed) =>
        Assert.Equal(parsed, PreviewHost.ParseHost(given)?.ToString());

    [Fact]
    public void The_settings_screen_refuses_what_would_never_connect()
    {
        Assert.Null(PreviewHost.Problem(PreviewHostKeys.HostUrl, "https://build.example.com"));
        Assert.Null(PreviewHost.Problem(PreviewHostKeys.HostUrl, ""));
        Assert.Contains("https://", PreviewHost.Problem(PreviewHostKeys.HostUrl, "build.example.com"));
        Assert.Contains("https://", PreviewHost.Problem("preview.s1a2b3c4d.hostUrl", "nope")); // a second setup's key too
        Assert.Null(PreviewHost.Problem(PreviewHostKeys.BuildTimeoutMinutes, "45"));
        Assert.Contains("minutes", PreviewHost.Problem(PreviewHostKeys.BuildTimeoutMinutes, "0"));
        Assert.Contains("minutes", PreviewHost.Problem(PreviewHostKeys.BuildTimeoutMinutes, "121"));
        Assert.Null(PreviewHost.Problem("preview.token", "anything goes"));
    }

    // ---------------------------------------------------------- previews

    [Fact]
    public void A_setup_runs_previews_once_it_names_where_they_live()
    {
        var values = new Dictionary<string, string?>
        {
            [PreviewHostKeys.HostUrl] = "https://build.example.com",
            [PreviewHostKeys.Token] = "agent-token-123456",
            [PreviewHostKeys.RunUrl] = "https://previews.example.net",
            [PreviewHostKeys.IdleMinutes] = "45",
            [PreviewHostKeys.MaxRunning] = "5",
        };
        var c = PreviewHost.Config(new SetupValues(Setups.MainId, Setups.MainName, true, values))!;
        Assert.Equal("https://previews.example.net/", c.RunUrl!.ToString());
        Assert.Equal(45, c.IdleMinutes);
        Assert.Equal(5, c.MaxRunning);

        // Builds only, and the limits at their defaults, until they are set.
        var plain = PreviewHost.Config(Values("https://build.example.com", "agent-token-123456"))!;
        Assert.Null(plain.RunUrl);
        Assert.Equal(PreviewHost.DefaultIdleMinutes, plain.IdleMinutes);
        Assert.Equal(PreviewHost.DefaultMaxRunning, plain.MaxRunning);
    }

    [Theory]
    [InlineData("https://previews.example.net", "https://previews.example.net/")]
    [InlineData("https://previews.example.net/", "https://previews.example.net/")]
    [InlineData("http://preview.localhost:8088", "http://preview.localhost:8088/")]
    [InlineData("https://previews.example.net/app", null)] // a path has nowhere to go
    [InlineData("https://10.0.0.5", null)] // a label cannot go in front of an address
    [InlineData("previews.example.net", null)]
    [InlineData("https://previews.example.net/?x=1", null)]
    public void The_preview_address_is_an_origin_that_names_a_host(string given, string? parsed) =>
        Assert.Equal(parsed, PreviewHost.ParseRunUrl(given)?.ToString());

    [Fact]
    public void Each_preview_has_an_address_of_its_own_named_after_its_id()
    {
        var id = Guid.Parse("1a2b3c4d-0000-0000-0000-000000000000");
        Assert.Equal("p-1a2b3c4d", PreviewHost.Label(id));
        Assert.Equal("https://p-1a2b3c4d.previews.example.net/",
            PreviewHost.Address(new Uri("https://previews.example.net/"), id).ToString());
        Assert.Equal("http://p-1a2b3c4d.preview.localhost:8088/",
            PreviewHost.Address(new Uri("http://preview.localhost:8088/"), id).ToString());
    }

    [Theory]
    [InlineData("https://previews.example.net", "https://web.crm.com", false)]
    [InlineData("https://p.crm-previews.com", "https://web.crm.com", false)]
    [InlineData("https://build.crm.com", "https://web.crm.com", true)] // the cookie's own domain
    [InlineData("https://previews.web.crm.com", "https://web.crm.com", true)]
    [InlineData("https://crm.com", "https://web.crm.com", true)]
    [InlineData("https://previews.crm.com", "https://crm.com", true)] // pages at the apex
    [InlineData("http://preview.localhost:8088", "http://localhost", true)] // under the portal's own host
    [InlineData("http://previews.test:8088", "http://localhost", false)]
    public void Previews_live_outside_the_portals_own_domain(string run, string web, bool refused)
    {
        var problem = PreviewHost.RunDomainProblem(new Uri(run), new Uri(web));
        Assert.Equal(refused, problem is not null);
        if (refused) Assert.Contains("outside the portal", problem);
        Assert.Null(PreviewHost.RunDomainProblem(new Uri(run), null)); // no Web URL set: nothing to compare
    }

    [Fact]
    public void The_screen_refuses_a_preview_address_or_limit_that_could_not_work()
    {
        Assert.Null(PreviewHost.Problem(PreviewHostKeys.RunUrl, "https://previews.example.net"));
        Assert.Contains("host name", PreviewHost.Problem(PreviewHostKeys.RunUrl, "https://previews.example.net/app"));
        Assert.Null(PreviewHost.Problem(PreviewHostKeys.IdleMinutes, "30"));
        Assert.Contains("Idle stop", PreviewHost.Problem(PreviewHostKeys.IdleMinutes, "4"));
        Assert.Null(PreviewHost.Problem(PreviewHostKeys.MaxRunning, "3"));
        Assert.Contains("at once", PreviewHost.Problem(PreviewHostKeys.MaxRunning, "0"));
        Assert.Contains("at once", PreviewHost.Problem(PreviewHostKeys.MaxRunning, "21"));
    }

    [Fact]
    public void The_test_says_whether_the_host_serves_previews_under_the_same_name()
    {
        var health = new PreviewHost.Health(true, "29.0", "5.1", 20_480, 0, null, 2, "previews.example.net");
        var runs = Host with { RunUrl = new Uri("https://previews.example.net/") };

        Assert.Null(PreviewHost.RunLine(health, Host)); // builds only: no second line
        var ok = PreviewHost.RunLine(health, runs)!.Value;
        Assert.True(ok.Ok);
        Assert.Contains("https://p-….previews.example.net", ok.Line);

        var other = PreviewHost.RunLine(health with { RunDomain = "previews.other.net" }, runs)!.Value;
        Assert.False(other.Ok);
        Assert.Contains("previews.other.net", other.Line);

        var none = PreviewHost.RunLine(health with { RunDomain = null }, runs)!.Value;
        Assert.False(none.Ok);
        Assert.Contains("PREVIEW_RUN_DOMAIN=previews.example.net", none.Line);

        Assert.Contains("2 previews running", PreviewHost.HealthLine(health, "Main"));
    }

    [Fact]
    public void The_preview_fields_came_later_so_passed_tests_stay_passed()
    {
        foreach (var key in new[] { PreviewHostKeys.RunUrl, PreviewHostKeys.IdleMinutes, PreviewHostKeys.MaxRunning })
        {
            Assert.Contains(key, Setups.Preview.Fields);
            Assert.Contains(key, Setups.Preview.Later!);
            Assert.NotNull(SettingsRegistry.Find(key));
        }
    }

    [Fact]
    public void The_build_host_is_a_setup_kind_with_a_test_and_a_group_of_its_own()
    {
        Assert.Contains(Setups.Preview, Setups.Kinds);
        Assert.Equal("preview", Setups.Preview.Group);
        Assert.Equal(Setups.Preview, Setups.KindOfField(PreviewHostKeys.Token));
        Assert.True(SettingsRegistry.Find(PreviewHostKeys.Token)!.IsSecret);
        Assert.Equal("Build host", ExternalServices.Labels[ExternalServices.Preview]);
    }

    // ---------------------------------------------------------- requests

    [Fact]
    public async Task A_build_is_handed_over_with_the_bearer_the_commit_and_the_clone_token()
    {
        var id = Guid.NewGuid();
        using var m = PreviewHost.StartBuildRequest(Host, id, "astrik-opportunities/erp-nadia", "abc1234def", "ghs_installation_token_9");
        Assert.Equal(HttpMethod.Post, m.Method);
        Assert.Equal("https://build.example.com/builds", m.RequestUri!.ToString());
        Assert.Equal("Bearer agent-token-123456", m.Headers.Authorization!.ToString());
        var body = await m.Content!.ReadAsStringAsync();
        Assert.Contains($"\"id\":\"{id}\"", body);
        Assert.Contains("\"repo\":\"astrik-opportunities/erp-nadia\"", body);
        Assert.Contains("\"sha\":\"abc1234def\"", body);
        Assert.Contains("\"token\":\"ghs_installation_token_9\"", body);
        Assert.Contains("\"timeoutSeconds\":900", body);
        // Both tokens are marked for the activity log to scrub.
        Assert.True(m.Options.TryGetValue(ExternalExchange.SecretsKey, out var secrets));
        Assert.Equal(["agent-token-123456", "ghs_installation_token_9"], secrets!.Order());
    }

    [Fact]
    public void The_other_calls_name_the_build_by_its_id()
    {
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");
        Assert.Equal("https://build.example.com/health", PreviewHost.HealthRequest(Host).RequestUri!.ToString());
        Assert.Equal($"https://build.example.com/builds/{id}", PreviewHost.StatusRequest(Host, id).RequestUri!.ToString());
        Assert.Equal($"https://build.example.com/builds/{id}/log", PreviewHost.LogRequest(Host, id).RequestUri!.ToString());
        Assert.Equal(HttpMethod.Delete, PreviewHost.DeleteRequest(Host, id).Method);
        // A host given with a path keeps it.
        var under = Host with { Host = new Uri("https://example.com/agent/") };
        Assert.Equal("https://example.com/agent/health", PreviewHost.HealthRequest(under).RequestUri!.ToString());
    }

    [Fact]
    public async Task A_preview_is_handed_over_with_its_ref_the_idle_stop_and_the_clone_token()
    {
        var id = Guid.NewGuid();
        var c = Host with { RunUrl = new Uri("https://previews.example.net/"), IdleMinutes = 45 };
        using var m = PreviewHost.StartRunRequest(c, id, "astrik-opportunities/erp-nadia", "final", "ghs_installation_token_9");
        Assert.Equal(HttpMethod.Post, m.Method);
        Assert.Equal("https://build.example.com/runs", m.RequestUri!.ToString());
        Assert.Equal("Bearer agent-token-123456", m.Headers.Authorization!.ToString());
        var body = await m.Content!.ReadAsStringAsync();
        Assert.Contains($"\"id\":\"{id}\"", body);
        Assert.Contains("\"ref\":\"final\"", body);
        Assert.Contains("\"idleSeconds\":2700", body);
        Assert.Contains("\"timeoutSeconds\":900", body);
        Assert.True(m.Options.TryGetValue(ExternalExchange.SecretsKey, out var secrets));
        Assert.Equal(["agent-token-123456", "ghs_installation_token_9"], secrets!.Order());

        Assert.Equal($"https://build.example.com/runs/{id}", PreviewHost.RunStatusRequest(Host, id).RequestUri!.ToString());
        using var stop = PreviewHost.StopRunRequest(Host, id);
        Assert.Equal(HttpMethod.Delete, stop.Method);
        Assert.Equal($"https://build.example.com/runs/{id}", stop.RequestUri!.ToString());
    }

    [Fact]
    public void A_preview_is_read_back_field_by_field()
    {
        var up = PreviewHost.ParseRun("""{"id":"x","status":"running","sha":"abc1234def","startedAt":"2026-09-24T10:00:00Z","lastSeenAt":"2026-09-24T10:05:00Z","stoppedAt":null,"stopReason":null,"error":null,"logTail":"web-1 started","notes":"Log in as demo@example.com / demo"}""");
        Assert.Equal(PreviewHost.Running, up.Status);
        Assert.Equal("abc1234def", up.Sha);
        Assert.Equal(DateTimeOffset.Parse("2026-09-24T10:05:00Z"), up.LastSeenAt);
        Assert.Null(up.StoppedAt);
        Assert.Equal("Log in as demo@example.com / demo", up.Notes);

        var idle = PreviewHost.ParseRun("""{"status":"stopped","stoppedAt":"2026-09-24T11:00:00Z","stopReason":"idle"}""");
        Assert.Equal("idle", idle.StopReason);
        Assert.Throws<PreviewHostException>(() => PreviewHost.ParseRun("""{"id":"x"}"""));
    }

    // ----------------------------------------------------------- answers

    [Fact]
    public void Health_and_a_build_are_read_back_field_by_field()
    {
        var h = PreviewHost.ParseHealth("""{"ok":true,"docker":"27.3.1","compose":"2.29.7","diskFreeMb":24576,"queued":2,"building":null}""");
        Assert.True(h.Ok);
        Assert.Equal("27.3.1", h.Docker);
        Assert.Equal(24576, h.DiskFreeMb);
        Assert.Equal(2, h.Queued);
        Assert.Null(h.Building);
        Assert.Equal("The build host answered (“Main”): Docker 27.3.1, Compose 2.29.7, 24 GB free, 2 queued.", PreviewHost.HealthLine(h, "Main"));

        var b = PreviewHost.ParseBuild("""{"status":"failed","startedAt":"2026-09-24T10:00:00Z","finishedAt":"2026-09-24T10:02:30Z","exitCode":1,"error":"docker compose build exited 1","logTail":"ERROR: npm ci failed"}""");
        Assert.Equal(PreviewHost.Failed, b.Status);
        Assert.Equal(DateTimeOffset.Parse("2026-09-24T10:02:30Z"), b.FinishedAt);
        Assert.Equal(1, b.ExitCode);
        Assert.Equal("ERROR: npm ci failed", b.LogTail);

        // The agent writes null, not nothing, for what a build has not got yet.
        var running = PreviewHost.ParseBuild("""{"status":"building","startedAt":"2026-09-24T10:00:00Z","finishedAt":null,"exitCode":null,"error":null,"logTail":""}""");
        Assert.Equal(PreviewHost.Building, running.Status);
        Assert.Null(running.ExitCode);
        Assert.Null(running.FinishedAt);
        Assert.Null(PreviewHost.ParseHealth("""{"ok":true,"diskFreeMb":null,"queued":null,"building":null}""").DiskFreeMb);

        var queued = PreviewHost.ParseBuild("""{"status":"queued"}""");
        Assert.Equal(PreviewHost.Queued, queued.Status);
        Assert.Null(queued.StartedAt);
        Assert.Throws<PreviewHostException>(() => PreviewHost.ParseBuild("""{"id":"x"}"""));
    }

    [Fact]
    public void A_refusal_carries_the_agents_reason_when_it_gave_one()
    {
        Assert.Equal("The build host answered 400: sha is not a commit", PreviewHost.ErrorDetail(400, """{"error":"sha is not a commit"}"""));
        Assert.Equal("The build host answered 502.", PreviewHost.ErrorDetail(502, "<html>bad gateway</html>"));
    }

    // ------------------------------------------------- the activity log

    private sealed class Answer(Func<HttpRequestMessage, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => answer(request);
    }

    [Fact]
    public async Task A_build_host_row_keeps_the_call_and_neither_token()
    {
        var log = new ActivityLog(NullLogger<ActivityLog>.Instance);
        var recorder = new ExternalCallRecorder(ExternalServices.Preview, log, new HttpContextAccessor())
        {
            InnerHandler = new Answer(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                // A log line that echoes the clone URL, as git does on a failed fetch.
                Content = new StringContent(
                    """{"id":"x","status":"queued","logTail":"fatal: https://x-access-token:ghs_installation_token_9@github.com/a/b"}""",
                    Encoding.UTF8, "application/json"),
            })),
        };
        using var client = new HttpClient(recorder);
        using var message = PreviewHost.StartBuildRequest(Host, Guid.NewGuid(), "a/b", "abc1234", "ghs_installation_token_9");

        using var response = await client.SendAsync(message);

        Assert.True(log.Reader.TryRead(out var row));
        Assert.Equal(ExternalServices.Preview, row!.Service);
        Assert.Equal("build.example.com/builds", row.Path);
        Assert.Contains("\"repo\": \"a/b\"", row.Request);
        Assert.DoesNotContain("agent-token-123456", row.Request);
        Assert.DoesNotContain("ghs_installation_token_9", row.Request);
        Assert.DoesNotContain("ghs_installation_token_9", row.Response);
        Assert.Contains(ExternalExchange.Masked, row.Response);
    }
}
