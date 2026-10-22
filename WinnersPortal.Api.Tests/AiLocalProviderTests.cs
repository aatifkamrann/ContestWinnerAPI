using System.Text.Json;
using WinnersPortal.Services.Ai;
using WinnersPortal.Services.Settings;
using Xunit;

namespace WinnersPortal.Api.Tests;

/// <summary>
/// A model server of the operator's own — Ollama, or anything that answers
/// OpenAI's chat-completions request at a /v1 address: the request it is
/// sent and where, when a key may go with it, how its answers and errors
/// are read, and how a setup and the eval tool find it. Nothing here
/// reaches a network.
/// </summary>
public class AiLocalProviderTests
{
    private static JsonElement Body(AiProviderRequests.AiRequest request) => JsonDocument.Parse(request.BodyJson).RootElement;

    [Fact]
    public void A_blank_address_is_ollama_on_this_machine_and_a_typed_one_loses_its_trailing_slash()
    {
        Assert.Equal("http://localhost:11434/v1", AiProviders.BaseUrl("ollama", null));
        Assert.Equal("http://localhost:11434/v1", AiProviders.BaseUrl("ollama", "  "));
        Assert.Equal("https://ai.example.test/v1", AiProviders.BaseUrl("ollama", " https://ai.example.test/v1/ "));
        Assert.Null(AiProviders.BaseUrl("openai", "http://anywhere/v1")); // a hosted provider's address is fixed
        Assert.True(AiProviders.Find("ollama")!.Local);
        Assert.False(AiProviders.Find("gemini")!.Local);
    }

    [Fact]
    public void The_request_is_openais_at_the_servers_address_with_the_older_token_field()
    {
        var request = AiProviderRequests.Build("ollama", "llama3.1:8b", "", "sys", "user", AiOutputs.PingSchema, "http://ollama:11434/v1");

        Assert.Equal("http://ollama:11434/v1/chat/completions", request.Url);
        Assert.Empty(request.Headers); // no key, no header
        var body = Body(request);
        Assert.Equal("llama3.1:8b", body.GetProperty("model").GetString());
        Assert.Equal("system", body.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.Equal("user", body.GetProperty("messages")[1].GetProperty("content").GetString());
        Assert.Equal("json_schema", body.GetProperty("response_format").GetProperty("type").GetString());
        Assert.Equal(AiProviderRequests.AnswerTokens, body.GetProperty("max_tokens").GetInt32());
        Assert.False(body.TryGetProperty("max_completion_tokens", out _));
        Assert.False(body.GetProperty("stream").GetBoolean());
        Assert.False(body.TryGetProperty("temperature", out _));
    }

    [Theory]
    [InlineData("https://ai.example.test/v1", true)]
    [InlineData("http://localhost:11434/v1", true)]
    [InlineData("http://127.0.0.1:1234/v1", true)]
    [InlineData("http://ollama:11434/v1", false)]
    [InlineData("http://10.0.0.5:11434/v1", false)]
    public void A_key_goes_only_over_https_or_to_this_machine(string baseUrl, bool sent)
    {
        var request = AiProviderRequests.Build("ollama", "m", "gateway-key", "s", "u", AiOutputs.PingSchema, baseUrl);
        Assert.Equal(sent, request.Headers.TryGetValue("Authorization", out var bearer));
        if (sent) Assert.Equal("Bearer gateway-key", bearer);
        Assert.Equal(sent, AiProviders.KeyMayGoTo(baseUrl));
    }

    [Fact]
    public void The_answer_the_bill_and_a_cut_off_answer_are_read_the_openai_way()
    {
        const string answer = """{"choices":[{"message":{"role":"assistant","content":"{\"ok\":true}"},"finish_reason":"stop"}],"usage":{"prompt_tokens":40,"completion_tokens":6}}""";
        Assert.Equal("{\"ok\":true}", AiProviderRequests.ExtractText("ollama", answer));
        Assert.Equal(new AiTokens(40, 6), AiProviderRequests.ReadTokens("ollama", answer));

        var e = Assert.Throws<AiProviderException>(() => AiProviderRequests.ExtractText("ollama",
            """{"choices":[{"message":{"content":"{\"ok\":"},"finish_reason":"length"}]}"""));
        Assert.Equal(AiFailure.Truncated, e.Failure);
    }

    [Theory]
    [InlineData("""{"error":"model 'llama3.1:8b' not found"}""", "The provider answered 404: model 'llama3.1:8b' not found")]
    [InlineData("""{"error":{"message":"Unknown model"}}""", "The provider answered 404: Unknown model")]
    [InlineData("""<html>Not Found</html>""", "The provider answered 404.")]
    public void Ollamas_error_string_is_read_beside_the_hosted_shape(string body, string expected) =>
        Assert.Equal(expected, AiProviderRequests.ErrorDetail(404, body));

    [Fact]
    public void Embeddings_go_to_the_same_server_in_openais_shape()
    {
        var request = AiEmbeddings.Build("ollama", AiEmbeddings.DefaultModel("ollama"), "", ["a", "b"], "http://localhost:11434/v1");
        Assert.Equal("http://localhost:11434/v1/embeddings", request.Url);
        Assert.Equal("nomic-embed-text", Body(request).GetProperty("model").GetString());

        var vectors = AiEmbeddings.ReadVectors("ollama",
            """{"data":[{"index":1,"embedding":[0,1]},{"index":0,"embedding":[1,0]}]}""", 2);
        Assert.Equal([1f, 0f], vectors[0]);
    }

    private static SetupValues Setup(string provider, string? key = null, string? baseUrl = null, string? model = null) =>
        new("main", "Main", true, new Dictionary<string, string?>
        {
            ["ai.provider"] = provider,
            ["ai.apiKey"] = key,
            [AiProviders.BaseUrlKey] = baseUrl,
            ["ai.model"] = model,
        });

    [Fact]
    public void A_local_setup_is_ready_with_no_key_and_carries_its_address_a_hosted_one_is_not()
    {
        var local = AiOptions.Config(Setup("ollama"));
        Assert.NotNull(local);
        Assert.Equal(("ollama", "", "http://localhost:11434/v1"), (local!.Provider, local.ApiKey, local.BaseUrl));

        Assert.Equal("http://box:11434/v1", AiOptions.Config(Setup("ollama", baseUrl: "http://box:11434/v1/"))!.BaseUrl);
        Assert.Null(AiOptions.Config(Setup("gemini")));
        Assert.Null(AiOptions.Config(Setup("openai", key: "k", baseUrl: "http://box/v1"))!.BaseUrl);
    }

    [Fact]
    public void A_standby_may_be_a_local_server_with_no_key()
    {
        var standby = AiOptions.Standby([
            new SetupValues("s1", "Box", false, new Dictionary<string, string?> { ["ai.provider"] = "ollama", [AiOptions.StandbyKey] = "true" }),
        ]);
        Assert.Equal("ollama", standby?.Provider);
    }

    [Theory]
    [InlineData("ollama", "http://ollama:11434/v1", "k", true)]
    [InlineData("ollama", "http://ollama:11434/v1", null, false)]
    [InlineData("ollama", "https://ai.example.test/v1", "k", false)]
    [InlineData("ollama", null, "k", false)] // this machine
    [InlineData("gemini", "http://anywhere/v1", "k", false)] // a hosted provider ignores the field
    public void A_key_beside_plain_http_to_another_machine_is_refused_at_the_save(string provider, string? baseUrl, string? key, bool refused)
    {
        var problem = AiProviders.KeyProblem(provider, baseUrl, key);
        Assert.Equal(refused, problem is not null);
        if (refused) Assert.Contains("plain text", problem);
    }

    [Theory]
    [InlineData("http://localhost:11434/v1", true)]
    [InlineData("https://ai.example.test/v1", true)]
    [InlineData("", true)]
    [InlineData("localhost:11434", false)]
    [InlineData("ftp://box/v1", false)]
    [InlineData("http://box/v1?key=x", false)]
    public void A_server_address_must_be_an_http_address_with_no_query(string value, bool ok)
    {
        Assert.Equal(ok, AiProviders.Problem(AiProviders.BaseUrlKey, value) is null);
        Assert.Equal(ok, AiProviders.Problem(EvalSettings.BaseUrlKey, value) is null);
        Assert.Null(AiProviders.Problem("ai.model", "localhost:11434"));
    }

    private static Func<string, string?> Env(params (string Name, string Value)[] set) =>
        name => set.FirstOrDefault(v => v.Name == name).Value;

    [Fact]
    public void The_eval_tool_finds_a_local_server_and_needs_no_key_for_it()
    {
        var blank = EvalSettings.Choose("ollama", null, Env());
        Assert.True(blank.Local);
        Assert.Null(blank.ApiKey);
        Assert.Equal(("http://localhost:11434/v1", "default"), (blank.BaseUrl, blank.BaseUrlFrom));

        var saved = EvalSettings.Choose(null, new EvalSettings.Saved("ollama", null, BaseUrl: "https://ai.example.test/v1"),
            Env((EvalSettings.BaseUrlEnv, "http://other/v1")));
        Assert.Equal(("https://ai.example.test/v1", EvalSettings.FromSettings), (saved.BaseUrl, saved.BaseUrlFrom));

        var typed = EvalSettings.Choose("ollama", null, Env((EvalSettings.BaseUrlEnv, "http://other/v1")), flagBaseUrl: "http://box:11434/v1");
        Assert.Equal(("http://box:11434/v1", "--base-url"), (typed.BaseUrl, typed.BaseUrlFrom));

        var hosted = EvalSettings.Choose("gemini", null, Env((EvalSettings.BaseUrlEnv, "http://other/v1")));
        Assert.False(hosted.Local);
        Assert.Null(hosted.BaseUrl);
    }
}
