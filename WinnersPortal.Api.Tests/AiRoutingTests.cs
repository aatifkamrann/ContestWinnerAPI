using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging.Abstractions;
using Polly;
using WinnersPortal.Api.Activity;
using WinnersPortal.Domain;
using WinnersPortal.Services.Activity;
using WinnersPortal.Services.Ai;
using WinnersPortal.Services.Settings;
using Xunit;

namespace WinnersPortal.Api.Tests;

/// <summary>
/// Where a call goes: the standby setup asked when the active provider
/// cannot be reached and left alone when it answered, each provider's
/// host on a circuit of its own, the model a feature asks, and the
/// embedding call's wire shapes. The providers are stub handlers
/// answering by host; nothing here reaches a network.
/// </summary>
public class AiRoutingTests
{
    private const string GeminiHost = "generativelanguage.googleapis.com";
    private const string OpenAiHost = "api.openai.com";

    private const string GeminiAnswer = """{"candidates":[{"content":{"parts":[{"text":"{\"category\":\"web\",\"subcategory\":null,\"because\":\"a shop\",\"confident\":true}"}]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":10,"candidatesTokenCount":4}}""";
    private const string OpenAiAnswer = """{"choices":[{"message":{"content":"{\"category\":\"web\",\"subcategory\":null,\"because\":\"a shop\",\"confident\":true}"},"finish_reason":"stop"}],"usage":{"prompt_tokens":12,"completion_tokens":5}}""";
    private const string OpenAiCut = """{"choices":[{"message":{"content":"{\"category\":\"we"},"finish_reason":"length"}],"usage":{"prompt_tokens":12,"completion_tokens":8192}}""";

    private sealed class Provider(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<string> Hosts { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Hosts.Add(request.RequestUri!.Host);
            return Task.FromResult(answer(request));
        }
    }

    private static HttpResponseMessage Status(HttpStatusCode code, string body = "{}") =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>The pipeline over the stub, the recorder inside it, as the resilience tests wire it: one pipeline, so the host test uses the container instead.</summary>
    private static (AiCaller Caller, AiProviderClient Client, Provider Provider, ActivityLog Log) Wired(Func<HttpRequestMessage, HttpResponseMessage> answer)
    {
        var provider = new Provider(answer);
        var log = new ActivityLog(NullLogger<ActivityLog>.Instance);
        var recorder = new ExternalCallRecorder(ExternalServices.Ai, log, new HttpContextAccessor()) { InnerHandler = provider };
        var builder = new ResiliencePipelineBuilder<HttpResponseMessage>();
        AiResilience.Configure(builder, _ => Task.FromResult(AiLimitValues.Defaults with { Retries = 0, CallTimeoutSeconds = 5 }));
#pragma warning disable EXTEXP0001
        var handler = new ResilienceHandler(builder.Build()) { InnerHandler = recorder };
#pragma warning restore EXTEXP0001
        var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var client = new AiProviderClient(new Factory(http));
        return (new AiCaller(null!, client, NullLogger<AiCaller>.Instance), client, provider, log);
    }

    private sealed class Factory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private static List<ActivityEvent> Rows(ActivityLog log)
    {
        var rows = new List<ActivityEvent>();
        while (log.Reader.TryRead(out var row)) rows.Add(row);
        return rows;
    }

    private static readonly AiProviderConfig Active = new("gemini", null, "key-1", "Main");
    private static readonly AiProviderConfig Backup = new("openai", "gpt-5.5-mini", "key-2", "Backup key");
    private static readonly (string System, string User) Prompt = ("sys JSON", "user");

    private static SetupValues Setup(string id, bool enabled, bool standby, string? key = "k", string provider = "openai") =>
        new(id, id, enabled, new Dictionary<string, string?>
        {
            ["ai.provider"] = provider, ["ai.apiKey"] = key, ["ai.model"] = null,
            [AiOptions.StandbyKey] = standby ? "true" : "false",
        });

    // ------------------------------------------------------ the standby

    [Fact]
    public void The_standby_is_the_first_inactive_setup_marked_with_a_key_saved()
    {
        Assert.Null(AiOptions.Standby([Setup("main", true, standby: false), Setup("s1", false, standby: false)]));
        // The active setup's own mark means nothing; a marked setup with no key is not a provider.
        Assert.Null(AiOptions.Standby([Setup("main", true, standby: true), Setup("s1", false, standby: true, key: null)]));
        var picked = AiOptions.Standby([
            Setup("main", true, standby: true),
            Setup("s1", false, standby: true, key: null),
            Setup("s2", false, standby: true, provider: "anthropic"),
            Setup("s3", false, standby: true),
        ])!;
        Assert.Equal(("anthropic", "s2"), (picked.Provider, picked.Setup));
    }

    [Fact]
    public void The_standby_switch_is_a_field_of_every_ai_setup_and_not_part_of_a_tests_fingerprint()
    {
        Assert.Contains(AiOptions.StandbyKey, Setups.Ai.Fields);
        Assert.Contains(AiOptions.StandbyKey, Setups.Ai.Unproved!);
        var def = SettingsRegistry.Find(AiOptions.StandbyKey)!;
        Assert.True(def.IsBoolean);
        Assert.Equal(("ai", "false"), (def.Group, def.Default));
        var scoped = SettingsRegistry.Find(Setups.FieldKey(AiOptions.StandbyKey, "s1a2b3c4d"))!;
        Assert.Equal((def.Label, def.IsBoolean, def.Default), (scoped.Label, scoped.IsBoolean, scoped.Default));

        var tested = Setup("s1a2b3c4d", false, standby: false);
        Assert.Equal(
            SetupTestLog.Fingerprint(Setups.Ai, tested),
            SetupTestLog.Fingerprint(Setups.Ai, Setup("s1a2b3c4d", false, standby: true)));
    }

    [Theory]
    [InlineData(429, AiFailure.Provider, true)]
    [InlineData(503, AiFailure.Provider, true)]
    [InlineData(0, AiFailure.Paused, true)]
    [InlineData(0, AiFailure.Timeout, true)]
    [InlineData(401, AiFailure.Provider, false)]
    [InlineData(404, AiFailure.Provider, false)]
    [InlineData(0, AiFailure.Refused, false)]
    [InlineData(0, AiFailure.Safety, false)]
    [InlineData(0, AiFailure.Truncated, false)]
    [InlineData(0, AiFailure.Empty, false)]
    public void A_provider_that_could_not_be_reached_is_stood_in_for_but_one_that_answered_is_not(int status, AiFailure failure, bool unreached)
    {
        Assert.Equal(unreached, AiRules.Unreached(new AiProviderException(status, "x", failure)));
        Assert.True(AiRules.Unreached(new HttpRequestException("no route")));
        Assert.False(AiRules.Unreached(new InvalidOperationException("the profile is gone")));
    }

    [Fact]
    public async Task When_the_active_provider_is_down_the_standby_answers_under_its_own_model_on_its_own_row()
    {
        var (caller, _, provider, log) = Wired(r => r.RequestUri!.Host == GeminiHost
            ? Status(HttpStatusCode.ServiceUnavailable, """{"error":{"message":"high demand"}}""")
            : Status(HttpStatusCode.OK, OpenAiAnswer));
        var route = new AiRoute(Active, "gemini-3.6-flash-lite", Backup);

        var answer = await caller.CompleteAsync(route, AiFeature.CategorySuggestion, Prompt, CancellationToken.None);

        Assert.True(answer.Standby);
        Assert.Equal(("openai", "gpt-5.5-mini", "Backup key"), (answer.Provider, answer.Model, answer.Setup));
        Assert.Equal(new AiTokens(12, 5), answer.Completion.Tokens);
        Assert.Equal([GeminiHost, OpenAiHost], provider.Hosts);
        var rows = Rows(log);
        Assert.Equal(2, rows.Count);
        Assert.Equal("gemini/gemini-3.6-flash-lite", rows[0].Subject);
        Assert.Equal("openai/gpt-5.5-mini", rows[1].Subject);
        Assert.StartsWith(AiPrompts.CallDetail(AiFeature.CategorySuggestion) + " · " + AiCaller.StandbyDetail, rows[1].Detail);
    }

    [Fact]
    public async Task A_refusal_a_rejected_key_or_a_missing_model_is_the_active_providers_answer_and_the_standby_is_not_asked()
    {
        var (caller, _, provider, _) = Wired(r => r.RequestUri!.Host == GeminiHost
            ? Status(HttpStatusCode.Unauthorized, """{"error":{"message":"bad key"}}""")
            : Status(HttpStatusCode.OK, OpenAiAnswer));
        var route = new AiRoute(Active, "gemini-3.6-flash", Backup);

        var e = await Assert.ThrowsAsync<AiProviderException>(() => caller.CompleteAsync(route, AiFeature.CategorySuggestion, Prompt, CancellationToken.None));

        Assert.Equal(401, e.StatusCode);
        Assert.Equal(("gemini", "gemini-3.6-flash"), (e.Provider, e.Model));
        Assert.Equal([GeminiHost], provider.Hosts);
    }

    [Fact]
    public async Task Without_a_standby_the_failure_is_thrown_as_it_was_naming_the_active_provider()
    {
        var (caller, _, provider, _) = Wired(_ => Status(HttpStatusCode.TooManyRequests, """{"error":{"message":"quota"}}"""));
        var route = new AiRoute(Active, "gemini-3.6-flash", Standby: null);

        var e = await Assert.ThrowsAsync<AiProviderException>(() => caller.CompleteAsync(route, AiFeature.CategorySuggestion, Prompt, CancellationToken.None));

        Assert.Equal(429, e.StatusCode);
        Assert.True(AiRules.NothingRan(e));
        Assert.Equal(("gemini", "gemini-3.6-flash"), (e.Provider, e.Model));
        Assert.Single(provider.Hosts);
    }

    [Fact]
    public async Task The_standbys_own_failure_names_the_standby_so_a_billed_one_is_spent_under_its_model()
    {
        var (caller, _, _, _) = Wired(r => r.RequestUri!.Host == GeminiHost
            ? Status(HttpStatusCode.BadGateway)
            : Status(HttpStatusCode.OK, OpenAiCut));
        var route = new AiRoute(Active, "gemini-3.6-flash", Backup);

        var e = await Assert.ThrowsAsync<AiProviderException>(() => caller.CompleteAsync(route, AiFeature.CategorySuggestion, Prompt, CancellationToken.None));

        Assert.Equal(AiFailure.Truncated, e.Failure);
        Assert.Equal(("openai", "gpt-5.5-mini"), (e.Provider, e.Model));
        Assert.Equal(new AiTokens(12, 8192), e.Tokens);
        Assert.False(AiRules.NothingRan(e));
    }

    [Fact]
    public async Task The_active_provider_answering_is_never_a_standby_call()
    {
        var (caller, _, provider, log) = Wired(_ => Status(HttpStatusCode.OK, GeminiAnswer));
        var route = new AiRoute(Active, "gemini-3.6-flash", Backup);

        var answer = await caller.CompleteAsync(route, AiFeature.CategorySuggestion, Prompt, CancellationToken.None);

        Assert.False(answer.Standby);
        Assert.Equal(("gemini", "gemini-3.6-flash", "Main"), (answer.Provider, answer.Model, answer.Setup));
        Assert.Equal([GeminiHost], provider.Hosts);
        Assert.Equal(AiPrompts.CallDetail(AiFeature.CategorySuggestion) + " · 10 in · 4 out", Rows(log).Single().Detail);
    }

    [Fact]
    public void The_standby_answers_with_its_own_model_never_the_features()
    {
        Assert.Equal("gpt-5.5-mini", new AiRoute(Active, "gemini-3.6-flash-lite", Backup).StandbyModel);
        Assert.Equal("gpt-5.5", new AiRoute(Active, "gemini-3.6-flash-lite", Backup with { Model = null }).StandbyModel);
        Assert.Null(new AiRoute(Active, "gemini-3.6-flash", null).StandbyModel);
    }

    // ------------------------------------------------- one circuit a host

    [Fact]
    public async Task Each_providers_host_has_a_circuit_of_its_own_so_a_paused_provider_leaves_the_other_open()
    {
        var provider = new Provider(r => r.RequestUri!.Host == GeminiHost
            ? Status(HttpStatusCode.InternalServerError)
            : Status(HttpStatusCode.OK, OpenAiAnswer));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient(AiProviderClient.HttpClientName, c => c.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => provider)
            .AddAiResilience(_ => _ => Task.FromResult(AiLimitValues.Defaults with { Retries = 0, PauseSeconds = 60, CallTimeoutSeconds = 5 }));
        var client = new AiProviderClient(services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>());

        // Four failures in a minute trip the breaker for that host alone.
        for (var i = 0; i < AiResilience.MinimumThroughput; i++)
        {
            var failed = await Assert.ThrowsAsync<AiProviderException>(() => Gemini(client));
            Assert.Equal(500, failed.StatusCode);
        }
        var paused = await Assert.ThrowsAsync<AiProviderException>(() => Gemini(client));
        Assert.Equal(AiFailure.Paused, paused.Failure);
        var sent = provider.Hosts.Count;

        var answer = await client.CompleteAsync("openai", "gpt-5.5", "k", "sys", "user", AiOutputs.PingSchema, CancellationToken.None);
        Assert.Equal(new AiTokens(12, 5), answer.Tokens);
        Assert.Equal(sent + 1, provider.Hosts.Count);
        Assert.Equal(OpenAiHost, provider.Hosts[^1]);

        static Task<AiCompletion> Gemini(AiProviderClient client) =>
            client.CompleteAsync("gemini", "gemini-3.6-flash", "k", "sys", "user", AiOutputs.PingSchema, CancellationToken.None);
    }

    // --------------------------------------------- a model per feature

    [Fact]
    public void A_model_per_feature_is_one_line_a_feature_named_as_the_usage_panel_names_it()
    {
        var list = AiFeatureModels.Parse(
            "# the quick tools\ncategorySuggestion gemini-3.6-flash-lite\nBriefCoach = gemini-3.6-flash-lite\n\nentryDigest: gemini-3.6-pro\ncategorySuggestion\tgemini-3.6-flash-lite-2\n");
        Assert.True(list.Any);
        Assert.Equal("gemini-3.6-flash-lite-2", list.For(AiFeature.CategorySuggestion)); // the last line for a feature wins
        Assert.Equal("gemini-3.6-flash-lite", list.For(AiFeature.BriefCoach));
        Assert.Equal("gemini-3.6-pro", list.For(AiFeature.EntryDigest));
        Assert.Null(list.For(AiFeature.StandingNotes));
        Assert.False(AiFeatureModels.Parse(null).Any);
        Assert.False(AiFeatureModelList.Empty.Any);
    }

    [Fact]
    public void A_line_that_cannot_be_read_or_names_a_feature_the_portal_lacks_is_refused_by_line()
    {
        Assert.Null(AiFeatureModels.Problem(null));
        Assert.Null(AiFeatureModels.Problem("  \n# notes only\n"));
        Assert.Null(AiFeatureModels.Problem("profileSummary gpt-5.5-mini"));
        Assert.StartsWith("Line 2 of model per feature could not be read (gpt-5.5-mini)", AiFeatureModels.Problem("# a\ngpt-5.5-mini"));
        Assert.StartsWith("Line 1 of model per feature could not be read", AiFeatureModels.Problem("categorySuggestion gemini flash"));
        var unknown = AiFeatureModels.Problem("spamFilter gpt-5.5")!;
        Assert.StartsWith("Line 1 of model per feature names a feature the portal does not have (spamFilter)", unknown);
        Assert.Contains("categorySuggestion", unknown);
        Assert.StartsWith("Line 1 of model per feature names a model longer than", AiFeatureModels.Problem("entryDigest " + new string('m', 101)));
        Assert.Equal(14, AiFeatureModels.FeatureNames.Count);
        Assert.DoesNotContain("spamFilter", AiFeatureModels.FeatureNames);

        // The same door every AI setting is checked through, and the row on the AI group.
        Assert.NotNull(AiLimits.Problem(AiFeatureModels.Key, "nothing here"));
        Assert.Null(AiLimits.Problem(AiFeatureModels.Key, "seoMetadata gpt-5.5-mini"));
        var def = SettingsRegistry.Find(AiFeatureModels.Key)!;
        Assert.True(def.IsMultiline);
        Assert.Equal("ai", def.Group);
    }

    [Fact]
    public void A_feature_name_is_the_switchs_in_any_case_and_the_spam_scan_has_none()
    {
        Assert.Equal(AiFeature.ProfileReview, AiFeatureModels.Feature("PROFILEREVIEW"));
        Assert.Equal(AiFeature.ApplicationEvaluation, AiFeatureModels.Feature(" applicationEvaluation "));
        Assert.Null(AiFeatureModels.Feature("spamFilter"));
        Assert.Null(AiFeatureModels.Feature("categorise"));
    }

    // ------------------------------------------------------- embeddings

    [Fact]
    public void An_embedding_request_is_each_providers_own_shape_and_anthropic_has_none()
    {
        var gemini = AiEmbeddings.Build("gemini", "gemini-embedding-2", "k", ["one", "two"]);
        Assert.EndsWith("/models/gemini-embedding-2:batchEmbedContents", gemini.Url);
        Assert.Equal("k", gemini.Headers["x-goog-api-key"]);
        using (var doc = JsonDocument.Parse(gemini.BodyJson))
        {
            var requests = doc.RootElement.GetProperty("requests");
            Assert.Equal(2, requests.GetArrayLength());
            Assert.Equal("models/gemini-embedding-2", requests[0].GetProperty("model").GetString());
            Assert.Equal("two", requests[1].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString());
            Assert.Equal(AiEmbeddings.TaskType, requests[0].GetProperty("taskType").GetString());
        }

        var openai = AiEmbeddings.Build("openai", "text-embedding-3-small", "k", ["one"]);
        Assert.Equal("https://api.openai.com/v1/embeddings", openai.Url);
        Assert.Equal("Bearer k", openai.Headers["Authorization"]);
        using (var doc = JsonDocument.Parse(openai.BodyJson))
        {
            Assert.Equal("text-embedding-3-small", doc.RootElement.GetProperty("model").GetString());
            Assert.Equal("one", doc.RootElement.GetProperty("input")[0].GetString());
        }

        Assert.Equal("gemini-embedding-2", AiEmbeddings.DefaultModel("gemini"));
        Assert.Equal("text-embedding-3-small", AiEmbeddings.DefaultModel("openai"));
        Assert.Contains("Anthropic offers no embedding model", Assert.Throws<AiProviderException>(() => AiEmbeddings.DefaultModel("anthropic")).Message);
        Assert.Contains("Anthropic offers no embedding model", Assert.Throws<AiProviderException>(() => AiEmbeddings.Build("anthropic", "m", "k", ["one"])).Message);
        Assert.Throws<ArgumentOutOfRangeException>(() => AiEmbeddings.Build("gemini", "m", "k", []));
    }

    [Fact]
    public void Vectors_come_back_one_per_text_in_the_order_sent_and_a_short_answer_is_refused()
    {
        var gemini = AiEmbeddings.ReadVectors("gemini", """{"embeddings":[{"values":[1,0]},{"values":[0,0.5]}]}""", 2);
        Assert.Equal([1f, 0f], gemini[0]);
        Assert.Equal([0f, 0.5f], gemini[1]);

        // OpenAI's carry an index; they are put in that order whatever order they arrived in.
        var openai = AiEmbeddings.ReadVectors("openai", """{"data":[{"index":1,"embedding":[0,1]},{"index":0,"embedding":[1,0]}],"usage":{"prompt_tokens":7,"total_tokens":7}}""", 2);
        Assert.Equal([1f, 0f], openai[0]);
        Assert.Equal([0f, 1f], openai[1]);

        var e = Assert.Throws<AiProviderException>(() => AiEmbeddings.ReadVectors("gemini", """{"embeddings":[{"values":[1,0]}]}""", 2));
        Assert.Equal(AiFailure.Empty, e.Failure);
        Assert.Throws<AiProviderException>(() => AiEmbeddings.ReadVectors("openai", """{"data":[{"index":0,"embedding":[]}]}""", 1));
    }

    [Fact]
    public void Cosine_similarity_is_one_for_the_same_direction_zero_for_unrelated_and_nothing_for_no_comparison()
    {
        Assert.Equal(1, AiEmbeddings.Cosine([1, 2, 3], [2, 4, 6]), 6);
        Assert.Equal(0, AiEmbeddings.Cosine([1, 0], [0, 1]), 6);
        Assert.Equal(-1, AiEmbeddings.Cosine([1, 0], [-1, 0]), 6);
        Assert.Equal(0.707, AiEmbeddings.Cosine([1, 0], [1, 1]), 3);
        Assert.Equal(0, AiEmbeddings.Cosine([0, 0], [1, 1]));
        Assert.Equal(0, AiEmbeddings.Cosine([1, 0], [1, 0, 0]));
        Assert.Equal(0, AiEmbeddings.Cosine([], []));
    }

    [Fact]
    public async Task An_embedding_call_goes_through_the_same_pipeline_and_recorder_and_counts_its_tokens()
    {
        var (_, client, provider, log) = Wired(_ => Status(HttpStatusCode.OK,
            """{"data":[{"index":0,"embedding":[1,0]},{"index":1,"embedding":[0,1]}],"usage":{"prompt_tokens":7,"total_tokens":7}}"""));

        var embedding = await client.EmbedAsync("openai", "text-embedding-3-small", "k", ["one", "two"], CancellationToken.None, "matching eval");

        Assert.Equal(2, embedding.Vectors.Count);
        Assert.Equal(new AiTokens(7, 0), embedding.Tokens);
        Assert.Equal([OpenAiHost], provider.Hosts);
        var row = Rows(log).Single();
        Assert.Equal("openai/text-embedding-3-small", row.Subject);
        Assert.StartsWith("matching eval", row.Detail);
    }
}
