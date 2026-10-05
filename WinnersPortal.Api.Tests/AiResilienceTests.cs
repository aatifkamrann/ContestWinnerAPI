using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using WinnersPortal.Api.Activity;
using WinnersPortal.Services.Activity;
using WinnersPortal.Services.Ai;
using WinnersPortal.Services.Settings;
using Xunit;

namespace WinnersPortal.Api.Tests;

/// <summary>
/// What stands between the provider client and the wire: the retries, the
/// attempt timeout and the pause, each driven by a setting read on every
/// call, and the recorder inside them seeing every attempt. The provider
/// is a stub handler; nothing here reaches a network.
/// </summary>
public class AiResilienceTests
{
    private sealed class Provider(Func<int, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        public int Attempts { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            await answer(++Attempts, request, ct);
    }

    private static HttpResponseMessage Status(HttpStatusCode code, string body = "{}", int? retryAfterSeconds = null)
    {
        var response = new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (retryAfterSeconds is { } s) response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(s));
        return response;
    }

    private const string Answer = """{"candidates":[{"content":{"parts":[{"text":"{\"ok\":true}"}]},"finishReason":"STOP"}]}""";

    /// <summary>The pipeline over the stub, the recorder inside it, as Program.cs wires the named client.</summary>
    private static (AiProviderClient Client, Provider Provider, ActivityLog Log) Wired(AiLimitValues limits, Func<int, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer)
    {
        var provider = new Provider(answer);
        var log = new ActivityLog(NullLogger<ActivityLog>.Instance);
        var recorder = new ExternalCallRecorder(ExternalServices.Ai, log, new HttpContextAccessor()) { InnerHandler = provider };
        var builder = new ResiliencePipelineBuilder<HttpResponseMessage>();
        AiResilience.Configure(builder, _ => Task.FromResult(limits));
        // The handler type is marked experimental by the package; Program.cs gets it through AddResilienceHandler.
#pragma warning disable EXTEXP0001
        var handler = new ResilienceHandler(builder.Build()) { InnerHandler = recorder };
#pragma warning restore EXTEXP0001
        var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        return (new AiProviderClient(new Factory(http)), provider, log);
    }

    private sealed class Factory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private static AiLimitValues Limits(int timeoutSeconds = 5, int retries = 2, int pauseSeconds = 60) =>
        AiLimitValues.Defaults with { CallTimeoutSeconds = timeoutSeconds, Retries = retries, PauseSeconds = pauseSeconds };

    private static async Task<string> CallAsync(AiProviderClient client) =>
        (await client.CompleteAsync("gemini", "gemini-3.6-flash", "key", "sys", "user", AiOutputs.PingSchema, CancellationToken.None)).Text;

    private static int Rows(ActivityLog log)
    {
        var n = 0;
        while (log.Reader.TryRead(out _)) n++;
        return n;
    }

    [Fact]
    public async Task A_bad_minute_is_retried_and_every_attempt_is_its_own_row()
    {
        var (client, provider, log) = Wired(Limits(), (attempt, _, _) => Task.FromResult(
            attempt < 3 ? Status(HttpStatusCode.ServiceUnavailable, """{"error":{"message":"high demand"}}""", retryAfterSeconds: 0) : Status(HttpStatusCode.OK, Answer)));

        Assert.Equal("{\"ok\":true}", await CallAsync(client));
        Assert.Equal(3, provider.Attempts);
        Assert.Equal(3, Rows(log));
    }

    [Fact]
    public async Task The_saved_number_of_retries_is_the_number_tried()
    {
        var (none, provider, _) = Wired(Limits(retries: 0), (_, _, _) => Task.FromResult(Status(HttpStatusCode.TooManyRequests, retryAfterSeconds: 0)));
        var e = await Assert.ThrowsAsync<AiProviderException>(() => CallAsync(none));
        Assert.Equal(429, e.StatusCode);
        Assert.Equal(1, provider.Attempts);

        var (one, provider1, _) = Wired(Limits(retries: 1), (_, _, _) => Task.FromResult(Status(HttpStatusCode.BadGateway, retryAfterSeconds: 0)));
        await Assert.ThrowsAsync<AiProviderException>(() => CallAsync(one));
        Assert.Equal(2, provider1.Attempts);
    }

    [Fact]
    public async Task A_4xx_was_ours_and_is_answered_not_retried()
    {
        var (client, provider, _) = Wired(Limits(), (_, _, _) => Task.FromResult(Status(HttpStatusCode.Unauthorized, """{"error":{"message":"bad key"}}""")));
        var e = await Assert.ThrowsAsync<AiProviderException>(() => CallAsync(client));
        Assert.Equal(401, e.StatusCode);
        Assert.Equal(AiFailure.Provider, e.Failure);
        Assert.Equal(1, provider.Attempts);
    }

    [Fact]
    public async Task An_attempt_past_the_timeout_is_abandoned_retried_and_named_when_none_answers()
    {
        // A second per attempt (the screen's floor is five; the pipeline takes what it is given), one retry.
        var (client, provider, log) = Wired(Limits(timeoutSeconds: 1, retries: 1), async (_, _, ct) =>
        {
            // A real handler honours the token; the abandoned attempt is cancelled, not left to finish.
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return Status(HttpStatusCode.OK, Answer);
        });
        var e = await Assert.ThrowsAsync<AiProviderException>(() => CallAsync(client));
        Assert.Equal(AiFailure.Timeout, e.Failure);
        Assert.Contains("did not answer in time", e.Friendly);
        Assert.Equal(2, provider.Attempts);
        Assert.Equal(2, Rows(log)); // both abandoned attempts are on record
    }

    [Fact]
    public async Task Half_a_minutes_failures_pause_the_calls_and_the_pause_is_named()
    {
        // Retries off, so each call is one attempt; four failures trip the breaker.
        var (client, provider, _) = Wired(Limits(retries: 0, pauseSeconds: 60), (_, _, _) => Task.FromResult(Status(HttpStatusCode.InternalServerError)));
        for (var i = 0; i < AiResilience.MinimumThroughput; i++)
            await Assert.ThrowsAsync<AiProviderException>(() => CallAsync(client));
        Assert.Equal(AiResilience.MinimumThroughput, provider.Attempts);

        var paused = await Assert.ThrowsAsync<AiProviderException>(() => CallAsync(client));
        Assert.Equal(AiFailure.Paused, paused.Failure);
        Assert.True(AiRules.NothingRan(paused));
        Assert.Equal(AiResilience.MinimumThroughput, provider.Attempts); // nothing was sent
    }

    [Fact]
    public async Task A_pause_of_zero_never_pauses()
    {
        var (client, provider, _) = Wired(Limits(retries: 0, pauseSeconds: 0), (_, _, _) => Task.FromResult(Status(HttpStatusCode.InternalServerError)));
        for (var i = 0; i < AiResilience.MinimumThroughput + 2; i++)
        {
            var e = await Assert.ThrowsAsync<AiProviderException>(() => CallAsync(client));
            Assert.Equal(500, e.StatusCode);
        }
        Assert.Equal(AiResilience.MinimumThroughput + 2, provider.Attempts);
    }

    [Theory]
    [InlineData(429, true)]
    [InlineData(408, true)]
    [InlineData(500, true)]
    [InlineData(503, true)]
    [InlineData(400, false)]
    [InlineData(401, false)]
    [InlineData(404, false)]
    [InlineData(200, false)]
    public void A_transient_failure_is_a_429_a_408_or_a_5xx(int status, bool transient) =>
        Assert.Equal(transient, AiResilience.Transient(Outcome.FromResult(new HttpResponseMessage((HttpStatusCode)status))));

    [Fact]
    public void A_lost_connection_or_a_timed_out_attempt_is_transient_too()
    {
        Assert.True(AiResilience.Transient(Outcome.FromException<HttpResponseMessage>(new HttpRequestException("no route"))));
        Assert.True(AiResilience.Transient(Outcome.FromException<HttpResponseMessage>(new Polly.Timeout.TimeoutRejectedException())));
        Assert.False(AiResilience.Transient(Outcome.FromException<HttpResponseMessage>(new InvalidOperationException())));
    }

    // ------------------------------------------------------- the settings

    [Fact]
    public void The_limits_read_from_the_settings_fall_back_to_their_defaults_one_by_one()
    {
        Assert.Equal(AiLimitValues.Defaults, AiLimits.Read(null, null, null, null, null));
        Assert.Equal(AiLimitValues.Defaults with { CallTimeoutSeconds = 120, Retries = 0 }, AiLimits.Read("120", "0", "abc", "", null));
        Assert.Equal(TimeSpan.FromSeconds(60 * 3) + AiResilience.BackoffAllowance, AiLimitValues.Defaults.TotalTimeout);
    }

    [Theory]
    [InlineData(AiLimits.CallTimeoutKey, "60", null)]
    [InlineData(AiLimits.CallTimeoutKey, "4", "Call timeout")]
    [InlineData(AiLimits.CallTimeoutKey, "601", "Call timeout")]
    [InlineData(AiLimits.RetriesKey, "5", null)]
    [InlineData(AiLimits.RetriesKey, "6", "Retries")]
    [InlineData(AiLimits.PauseKey, "0", null)]
    [InlineData(AiLimits.PauseKey, "-1", "pause")]
    [InlineData(AiLimits.MemberDailyKey, "0", null)]
    [InlineData(AiLimits.MemberDailyKey, "ten", "per day")]
    [InlineData(AiLimits.MemberPerMinuteKey, "6", null)]
    [InlineData(AiLimits.MemberPerMinuteKey, "1.5", "per minute")]
    [InlineData(EvalSettings.MaxCallsKey, "0", "eval run")]
    [InlineData(EvalSettings.MaxCallsKey, "100", null)]
    [InlineData(EvalSettings.TimeoutKey, "x", "eval call timeout")]
    [InlineData("ai.dailyCallLimit", "x", null)]
    public void A_limit_that_is_not_a_number_in_range_is_refused_on_the_screen(string key, string value, string? problem)
    {
        var def = SettingsRegistry.Find(key)!;
        var line = SettingsService.ValueProblem(def, value);
        if (problem is null) Assert.Null(line);
        else Assert.Contains(problem, line);
        Assert.Null(SettingsService.ValueProblem(def, "")); // blank hands back to the variable, then the default
    }

    [Fact]
    public void The_seven_limits_sit_on_the_two_ai_tabs_with_their_defaults()
    {
        string?[] ai = [AiLimits.CallTimeoutKey, AiLimits.RetriesKey, AiLimits.PauseKey, AiLimits.MemberDailyKey, AiLimits.MemberPerMinuteKey];
        foreach (var key in ai)
        {
            var def = SettingsRegistry.Find(key!)!;
            Assert.Equal("ai", def.Group);
            Assert.True(def.HelpRequired);
            Assert.NotNull(def.Default);
        }
        Assert.Equal(("eval", $"{EvalSettings.DefaultMaxCalls}"), (SettingsRegistry.Find(EvalSettings.MaxCallsKey)!.Group, SettingsRegistry.Find(EvalSettings.MaxCallsKey)!.Default));
        Assert.Equal(("eval", $"{EvalSettings.DefaultTimeoutSeconds}"), (SettingsRegistry.Find(EvalSettings.TimeoutKey)!.Group, SettingsRegistry.Find(EvalSettings.TimeoutKey)!.Default));
        // The two counter rows the ceiling used to live in are gone: the count is a table now.
        Assert.Null(SettingsRegistry.Find("system.aiCallsCount"));
    }
}
