using System.Text;
using System.Text.Json;
using Polly.CircuitBreaker;
using Polly.Timeout;
using WinnersPortal.Services.Activity;

namespace WinnersPortal.Services.Ai;

/// <summary>
/// Request and response shapes for the supported providers, kept pure so
/// tests can pin the wire format. Like the GitHub App JWT and the S3
/// presigner before it: three POST bodies and three response paths are not
/// worth an SDK each — and one interface is what makes switching providers a
/// settings change instead of a rewrite. The list itself is
/// <see cref="AiProviders"/>; this file is what each entry means on the wire.
/// </summary>
public static class AiProviderRequests
{
    public sealed record AiRequest(string Url, IReadOnlyDictionary<string, string> Headers, string BodyJson);

    /// <summary>
    /// The answer's token budget, the same on every provider. It covers the
    /// model's own thinking as well as the answer, so it is sized for the
    /// longest draft — a board of forty entrants — not for the shortest; an
    /// answer that still runs past it is refused as cut short
    /// (<see cref="AiFailure.Truncated"/>) rather than parsed as far as it got.
    /// </summary>
    public const int AnswerTokens = 8192;

    /// <summary>A blank ai.model means "the sensible default for the provider".</summary>
    public static string DefaultModel(string provider) =>
        AiProviders.Find(provider)?.DefaultModel ?? throw Unknown(provider);

    private static AiProviderException Unknown(string provider) => new(0,
        $"Unknown AI provider '{provider}' — the portal supports {AiProviders.Named}.");

    /// <param name="schema">
    /// The shape the answer is held to (<see cref="AiOutputs.Schema"/>), sent
    /// in each provider's own structured-output field so the model cannot
    /// answer off it: OpenAI's strict <c>json_schema</c>, Gemini's
    /// <c>responseJsonSchema</c>, Anthropic's <c>output_config</c>. The
    /// prompt still names the shape in words — the schema holds the keys and
    /// types, the prompt says what goes in them.
    /// </param>
    public static AiRequest Build(string provider, string model, string apiKey, string system, string user, object schema)
    {
        return provider switch
        {
            // The key travels in a header, never in the URL — URLs end up in
            // proxy and provider logs; headers don't.
            "gemini" => new AiRequest(
                $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(model)}:generateContent",
                new Dictionary<string, string> { ["x-goog-api-key"] = apiKey },
                JsonSerializer.Serialize(new
                {
                    system_instruction = new { parts = new[] { new { text = system } } },
                    contents = new[] { new { role = "user", parts = new[] { new { text = user } } } },
                    generationConfig = new
                    {
                        temperature = 0.2,
                        responseMimeType = "application/json",
                        responseJsonSchema = schema,
                        maxOutputTokens = AnswerTokens,
                    },
                })),
            "anthropic" => new AiRequest(
                "https://api.anthropic.com/v1/messages",
                new Dictionary<string, string>
                {
                    ["x-api-key"] = apiKey,
                    ["anthropic-version"] = "2023-06-01",
                },
                // No sampling parameters: the current Claude models reject a
                // non-default temperature outright, and the JSON shape is
                // held by the schema and the output gate, not by sampling.
                JsonSerializer.Serialize(new
                {
                    model,
                    max_tokens = AnswerTokens,
                    system,
                    messages = new[] { new { role = "user", content = user } },
                    output_config = new { format = new { type = "json_schema", schema } },
                })),
            "openai" => new AiRequest(
                "https://api.openai.com/v1/chat/completions",
                new Dictionary<string, string> { ["Authorization"] = $"Bearer {apiKey}" },
                // Strict schema mode holds the answer to exactly the shape;
                // like the older JSON mode it refuses a prompt that never
                // says "JSON", which the house rules do by name. No sampling
                // parameters, for the same reason as Anthropic's: the
                // current models answer 400 to a temperature. The completion
                // budget is the newer field; max_tokens is refused by these
                // models.
                JsonSerializer.Serialize(new
                {
                    model,
                    messages = new[]
                    {
                        new { role = "system", content = system },
                        new { role = "user", content = user },
                    },
                    response_format = new
                    {
                        type = "json_schema",
                        json_schema = new { name = "answer", strict = true, schema },
                    },
                    max_completion_tokens = AnswerTokens,
                })),
            _ => throw Unknown(provider),
        };
    }

    /// <summary>
    /// The model's text out of each provider's envelope — after reading why
    /// the model stopped. A 200 is not an answer: an answer cut off at the
    /// token budget, one the model declined to give, or one the provider's
    /// own filter stopped each arrives as a 200 with a reason beside the
    /// text, and each is thrown as the named failure it is
    /// (<see cref="AiFailure"/>) rather than handed to the validator as far
    /// as it got. Text blocks are joined wherever the provider sends more
    /// than one; a thinking model's reasoning blocks are not text.
    /// </summary>
    public static string ExtractText(string provider, string responseJson)
    {
        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;
        var text = provider switch
        {
            "gemini" => Gemini(root),
            "anthropic" => Anthropic(root),
            "openai" => OpenAi(root),
            _ => throw Unknown(provider),
        };
        return text.Length > 0 ? text : throw new AiProviderException(0,
            "The provider answered, but with no text — an empty completion.", AiFailure.Empty);
    }

    private static string Gemini(JsonElement root)
    {
        // A prompt the filter would not even read has no candidates at all,
        // and says why in promptFeedback.
        if (root.TryGetProperty("promptFeedback", out var feedback) && Str(feedback, "blockReason") is { } block)
            throw Stopped(AiFailure.Safety, $"The provider blocked the request (blockReason {block}).");
        if (!root.TryGetProperty("candidates", out var candidates)
            || candidates.ValueKind != JsonValueKind.Array || candidates.GetArrayLength() == 0)
            return "";
        var candidate = candidates[0];
        var reason = Str(candidate, "finishReason");
        switch (reason)
        {
            case "MAX_TOKENS":
                throw Stopped(AiFailure.Truncated, "The provider stopped at the answer's token budget (finishReason MAX_TOKENS).");
            case "SAFETY" or "RECITATION" or "BLOCKLIST" or "PROHIBITED_CONTENT" or "SPII" or "IMAGE_SAFETY":
                throw Stopped(AiFailure.Safety, $"The provider stopped the answer (finishReason {reason}).");
        }
        if (!candidate.TryGetProperty("content", out var content)
            || !content.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array)
            return "";
        // A thinking model's thoughts come as parts flagged thought: true.
        return string.Concat(parts.EnumerateArray()
            .Where(p => !(p.TryGetProperty("thought", out var t) && t.ValueKind == JsonValueKind.True))
            .Select(p => Str(p, "text") ?? ""));
    }

    private static string Anthropic(JsonElement root)
    {
        switch (Str(root, "stop_reason"))
        {
            case "max_tokens":
                throw Stopped(AiFailure.Truncated, "The provider stopped at the answer's token budget (stop_reason max_tokens).");
            case "refusal":
                throw Stopped(AiFailure.Refused, "The model declined to answer (stop_reason refusal).");
        }
        if (!root.TryGetProperty("content", out var blocks) || blocks.ValueKind != JsonValueKind.Array)
            return "";
        return string.Concat(blocks.EnumerateArray()
            .Where(b => Str(b, "type") == "text")
            .Select(b => Str(b, "text") ?? ""));
    }

    private static string OpenAi(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            return "";
        var choice = choices[0];
        switch (Str(choice, "finish_reason"))
        {
            case "length":
                throw Stopped(AiFailure.Truncated, "The provider stopped at the answer's token budget (finish_reason length).");
            case "content_filter":
                throw Stopped(AiFailure.Safety, "The provider's filter stopped the answer (finish_reason content_filter).");
        }
        if (!choice.TryGetProperty("message", out var message)) return "";
        // A refusal arrives as a sibling of content, with content null.
        if (Str(message, "refusal") is { Length: > 0 } refusal)
            throw Stopped(AiFailure.Refused, $"The model declined to answer: {AiRules.Clip(refusal, 300)}");
        return Str(message, "content") ?? "";
    }

    private static AiProviderException Stopped(AiFailure failure, string message) => new(0, message, failure);

    /// <summary>
    /// What the call cost, as the provider counted it in its own answer:
    /// tokens sent and tokens answered, each provider's name for them
    /// folded into the two every price page quotes. A thinking model's
    /// reasoning is billed as output and counted with it; a cached prefix
    /// is still input, counted at full weight here rather than at the
    /// provider's discount, so an estimate errs high. Null when the
    /// answer carries no count at all.
    /// </summary>
    public static AiTokens? ReadTokens(string provider, string responseJson)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(responseJson);
        }
        catch (JsonException)
        {
            return null;
        }
        using (doc)
        {
            var root = doc.RootElement;
            return provider switch
            {
                "gemini" when Obj(root, "usageMetadata") is { } u =>
                    new AiTokens(Num(u, "promptTokenCount"), Num(u, "candidatesTokenCount") + Num(u, "thoughtsTokenCount")),
                "anthropic" when Obj(root, "usage") is { } u =>
                    new AiTokens(Num(u, "input_tokens") + Num(u, "cache_creation_input_tokens") + Num(u, "cache_read_input_tokens"),
                        Num(u, "output_tokens")),
                "openai" when Obj(root, "usage") is { } u =>
                    new AiTokens(Num(u, "prompt_tokens"), Num(u, "completion_tokens")),
                _ => null,
            };
        }
    }

    private static JsonElement? Obj(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    private static long Num(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    private static string? Str(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    /// <summary>All three providers wrap errors as {"error":{"message":…}}; dig it out for a readable note.</summary>
    public static string ErrorDetail(int statusCode, string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err)
                && err.TryGetProperty("message", out var msg)
                && msg.ValueKind == JsonValueKind.String)
                return $"The provider answered {statusCode}: {AiRules.Clip(msg.GetString(), 300)}";
        }
        catch (JsonException)
        {
            // fall through to the generic line
        }
        return $"The provider answered {statusCode}.";
    }

    /// <summary>
    /// What a person is told when the provider would not answer — never the
    /// provider's own words, which name quotas, billing pages and key
    /// formats a member cannot act on. Those go to the log, where the
    /// operator reads them. Status 0 is the portal's own refusal (an
    /// unknown provider, an empty completion), or one of the named ways a
    /// 200 carried no answer.
    /// </summary>
    public static string Unavailable(int statusCode, AiFailure failure = AiFailure.Provider) => failure switch
    {
        AiFailure.Truncated =>
            "The AI assistant's answer ran past its length limit and was cut short. Please try again; if it keeps happening, the operator can check the model under AI in settings.",
        AiFailure.Refused or AiFailure.Safety =>
            "The AI provider declined to answer this request. Please try again with different wording; if it keeps happening, the operator can read the provider's reason in the API log.",
        AiFailure.Timeout =>
            "The AI provider did not answer in time. Please try again in a few minutes; if it keeps happening, the operator can raise the call timeout under AI in settings.",
        AiFailure.Paused =>
            "The AI provider has been failing, so the portal is giving it a short rest. Please try again in a minute or two.",
        _ => statusCode switch
        {
            401 or 403 => "The AI provider rejected this portal's key. The operator can check it under AI in settings.",
            404 => "The AI provider does not offer the model this portal is set to. The operator can check the model name under AI in settings.",
            429 => "The AI assistant has used up its quota or rate limit for now. Please try again later.",
            >= 500 => "The AI provider is not available right now. Please try again in a few minutes.",
            _ => "The AI assistant could not answer this time. Please try again; if it keeps happening, the operator can check the AI settings.",
        },
    };
}

/// <summary>
/// Why a call yielded no answer: the provider's own status, or one of the
/// named ways a 200 carries nothing usable. The name decides what a person
/// is told and whether the worker tries again — a cut-off answer or an
/// empty one may well pass next time; a refusal is the provider's reading
/// of the input, and the same input asked again gets the same reading.
/// </summary>
public enum AiFailure
{
    /// <summary>The provider answered with an error status; the message says which.</summary>
    Provider,
    /// <summary>The answer ran past the token budget and was cut off.</summary>
    Truncated,
    /// <summary>The model itself declined to answer.</summary>
    Refused,
    /// <summary>The provider's own filter stopped the request or the answer.</summary>
    Safety,
    /// <summary>A 200 with no text in it.</summary>
    Empty,
    /// <summary>No attempt answered within the call timeout, retries included.</summary>
    Timeout,
    /// <summary>Nothing was sent: the provider has been failing and the portal is pausing its calls.</summary>
    Paused,
}

/// <summary>
/// The one HTTP path to a model provider. Everything that calls a model —
/// the worker's jobs and the admin's test button — goes through here, which
/// is what makes "what leaves the server" a one-file audit.
/// </summary>
public sealed class AiProviderClient(IHttpClientFactory httpFactory)
{
    public const string HttpClientName = "ai";

    /// <param name="schema">The shape the answer is held to (<see cref="AiOutputs.Schema"/>).</param>
    /// <param name="detail">
    /// What the call was for, on its activity row: the feature and prompt
    /// version (<see cref="AiPrompts.CallDetail"/>), or the settings test's
    /// own words. Null leaves the row with the provider and model alone.
    /// </param>
    /// <returns>The model's text and what the call cost in tokens, where the provider said.</returns>
    public async Task<AiCompletion> CompleteAsync(
        string provider, string model, string apiKey, string system, string user, object schema, CancellationToken ct,
        string? detail = null)
    {
        var body = await SendAsync(
            AiProviderRequests.Build(provider, model, apiKey, system, user, schema),
            apiKey, AiProviders.CallName(provider, model), detail, ct);
        // A 200 that carries no answer still carries its bill — an answer
        // cut off at the token budget is the dearest kind — so the count
        // travels with the failure for the caller to spend.
        var tokens = AiProviderRequests.ReadTokens(provider, body);
        try
        {
            return new AiCompletion(AiProviderRequests.ExtractText(provider, body), tokens);
        }
        catch (AiProviderException e)
        {
            e.Tokens = tokens;
            throw;
        }
    }

    /// <summary>
    /// One embedding call: a vector per text, through the same pipeline
    /// and recorder as a completion, its row named by provider and model
    /// the same way (<see cref="AiEmbeddings"/>). Nothing on the portal
    /// asks for one yet; the eval tool's matching run measures them first.
    /// </summary>
    public async Task<AiEmbedding> EmbedAsync(
        string provider, string model, string apiKey, IReadOnlyList<string> texts, CancellationToken ct, string? detail = null)
    {
        var body = await SendAsync(
            AiEmbeddings.Build(provider, model, apiKey, texts), apiKey, AiProviders.CallName(provider, model), detail, ct);
        return new AiEmbedding(AiEmbeddings.ReadVectors(provider, body, texts.Count), AiProviderRequests.ReadTokens(provider, body));
    }

    /// <summary>
    /// The send every call shares: the key in the headers and masked on
    /// the row, the subject and detail on it, the pipeline's two named
    /// failures, and an error status thrown with the provider's words.
    /// </summary>
    private async Task<string> SendAsync(
        AiProviderRequests.AiRequest request, string apiKey, string subject, string? detail, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, request.Url)
        {
            Content = new StringContent(request.BodyJson, Encoding.UTF8, "application/json"),
        };
        foreach (var (name, value) in request.Headers)
            message.Headers.TryAddWithoutValidation(name, value);
        message.WithSecrets(apiKey);
        // Members see the portal's own name; the activity row says which vendor and model it was.
        message.WithSubject(subject);
        if (detail is not null) message.WithDetail(detail);

        // The named client carries the resilience pipeline (AiResilience):
        // the retries happen inside this one send, and what comes out is
        // the last attempt's answer, or the named reason there was none.
        HttpResponseMessage response;
        try
        {
            response = await httpFactory.CreateClient(HttpClientName).SendAsync(message, ct);
        }
        catch (TimeoutRejectedException e)
        {
            throw new AiProviderException(0, "No attempt answered within the call timeout: " + e.Message, AiFailure.Timeout);
        }
        catch (BrokenCircuitException e)
        {
            throw new AiProviderException(0, "Nothing was sent: the provider has been failing and its calls are paused. " + e.Message, AiFailure.Paused);
        }
        using var _ = response;
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new AiProviderException((int)response.StatusCode,
                AiProviderRequests.ErrorDetail((int)response.StatusCode, body));
        return body;
    }
}

/// <summary>A provider's answer: the model's text, and the tokens it counted — null when it counted none.</summary>
public sealed record AiCompletion(string Text, AiTokens? Tokens);

public sealed class AiProviderException(int statusCode, string message, AiFailure failure = AiFailure.Provider) : Exception(message)
{
    public int StatusCode { get; } = statusCode;

    /// <summary>Which named failure this is; <see cref="AiFailure.Provider"/> for an error status.</summary>
    public AiFailure Failure { get; } = failure;

    /// <summary>What the provider billed for a 200 that carried no answer; null for every other failure.</summary>
    public AiTokens? Tokens { get; internal set; }

    /// <summary>The provider the failure came from, where the caller named it (<see cref="AiCaller"/>); null on a bare call.</summary>
    public string? Provider { get; private set; }

    /// <summary>The model the failure came from, named with the provider.</summary>
    public string? Model { get; private set; }

    internal void Name(string provider, string model)
    {
        Provider = provider;
        Model = model;
    }

    /// <summary>The line a person sees. The Message is for the log.</summary>
    public string Friendly => AiProviderRequests.Unavailable(StatusCode, Failure);
}
