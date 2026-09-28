using System.Text;
using System.Text.Json;
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

    /// <summary>A blank ai.model means "the sensible default for the provider".</summary>
    public static string DefaultModel(string provider) =>
        AiProviders.Find(provider)?.DefaultModel ?? throw Unknown(provider);

    private static AiProviderException Unknown(string provider) => new(0,
        $"Unknown AI provider '{provider}' — the portal supports {AiProviders.Named}.");

    public static AiRequest Build(string provider, string model, string apiKey, string system, string user)
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
                    generationConfig = new { temperature = 0.2, response_mime_type = "application/json" },
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
                // held by the prompt and the output gate, not by sampling.
                // The budget covers the model's own thinking as well as the
                // answer, so it is sized for the longest draft — a board of
                // forty entrants — not for the shortest.
                JsonSerializer.Serialize(new
                {
                    model,
                    max_tokens = 8192,
                    system,
                    messages = new[] { new { role = "user", content = user } },
                })),
            "openai" => new AiRequest(
                "https://api.openai.com/v1/chat/completions",
                new Dictionary<string, string> { ["Authorization"] = $"Bearer {apiKey}" },
                // JSON mode holds the answer to one object, which the
                // house rules already ask for by name — the mode refuses a
                // prompt that never says "JSON". No sampling parameters,
                // for the same reason as Anthropic's: the current models
                // answer 400 to a temperature. The completion budget is
                // the newer field; max_tokens is refused by these models.
                JsonSerializer.Serialize(new
                {
                    model,
                    messages = new[]
                    {
                        new { role = "system", content = system },
                        new { role = "user", content = user },
                    },
                    response_format = new { type = "json_object" },
                    max_completion_tokens = 8192,
                })),
            _ => throw Unknown(provider),
        };
    }

    /// <summary>The model's text out of each provider's envelope.</summary>
    public static string ExtractText(string provider, string responseJson)
    {
        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;
        var text = provider switch
        {
            "gemini" => root.TryGetProperty("candidates", out var candidates)
                && candidates.GetArrayLength() > 0
                && candidates[0].TryGetProperty("content", out var content)
                && content.TryGetProperty("parts", out var parts)
                && parts.GetArrayLength() > 0
                    ? parts[0].GetProperty("text").GetString()
                    : null,
            "anthropic" => root.TryGetProperty("content", out var blocks)
                && blocks.GetArrayLength() > 0
                    ? blocks[0].GetProperty("text").GetString()
                    : null,
            // A refusal arrives as a sibling of content, with content null;
            // both read as "no text" below, which is the right outcome.
            "openai" => root.TryGetProperty("choices", out var choices)
                && choices.GetArrayLength() > 0
                && choices[0].TryGetProperty("message", out var message)
                && message.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.String
                    ? content.GetString()
                    : null,
            _ => throw Unknown(provider),
        };
        return text ?? throw new AiProviderException(0,
            "The provider answered, but with no text — possibly a safety block or an empty completion.");
    }

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
    /// unknown provider, an empty completion).
    /// </summary>
    public static string Unavailable(int statusCode) => statusCode switch
    {
        401 or 403 => "The AI provider rejected this portal's key. The operator can check it under AI in settings.",
        404 => "The AI provider does not offer the model this portal is set to. The operator can check the model name under AI in settings.",
        429 => "The AI assistant has used up its quota or rate limit for now. Please try again later.",
        >= 500 => "The AI provider is not available right now. Please try again in a few minutes.",
        _ => "The AI assistant could not answer this time. Please try again; if it keeps happening, the operator can check the AI settings.",
    };
}

/// <summary>
/// The one HTTP path to a model provider. Everything that calls a model —
/// the worker's jobs and the admin's test button — goes through here, which
/// is what makes "what leaves the server" a one-file audit.
/// </summary>
public sealed class AiProviderClient(IHttpClientFactory httpFactory)
{
    public const string HttpClientName = "ai";

    public async Task<string> CompleteAsync(
        string provider, string model, string apiKey, string system, string user, CancellationToken ct)
    {
        var request = AiProviderRequests.Build(provider, model, apiKey, system, user);
        using var message = new HttpRequestMessage(HttpMethod.Post, request.Url)
        {
            Content = new StringContent(request.BodyJson, Encoding.UTF8, "application/json"),
        };
        foreach (var (name, value) in request.Headers)
            message.Headers.TryAddWithoutValidation(name, value);
        message.WithSecrets(apiKey);
        // Members see "YupAI"; the activity row says which vendor and model it was.
        message.WithSubject(AiProviders.CallName(provider, model));

        using var response = await httpFactory.CreateClient(HttpClientName).SendAsync(message, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new AiProviderException((int)response.StatusCode,
                AiProviderRequests.ErrorDetail((int)response.StatusCode, body));
        return AiProviderRequests.ExtractText(provider, body);
    }
}

public sealed class AiProviderException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;

    /// <summary>The line a person sees. The Message is for the log.</summary>
    public string Friendly => AiProviderRequests.Unavailable(StatusCode);
}
