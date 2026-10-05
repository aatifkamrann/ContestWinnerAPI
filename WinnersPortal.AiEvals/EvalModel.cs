using System.Diagnostics;
using System.Text;
using WinnersPortal.Services.Ai;

namespace WinnersPortal.AiEvals;

/// <summary>One model the run talks to — the one under test, or the judge.</summary>
public interface IEvalModel
{
    string Provider { get; }

    string Model { get; }

    /// <summary>
    /// The model's text, how long it took and what it cost in tokens where
    /// the provider said, the answer held to the schema as the portal holds
    /// it; throws <see cref="AiProviderException"/> when the provider would
    /// not answer.
    /// </summary>
    Task<(string Text, int LatencyMs, AiTokens? Tokens)> CompleteAsync(string system, string user, object schema, CancellationToken ct);
}

/// <summary>
/// The real thing: the same request bodies and response paths the portal
/// sends and reads (<see cref="AiProviderRequests"/>), over a plain
/// HttpClient with the key from the settings. One retry on a 429 or a
/// 5xx, honouring Retry-After, so a provider's bad minute does not fail a
/// case that never ran; each call is bounded by the eval call timeout
/// (Settings → AI evals).
/// </summary>
public sealed class HttpEvalModel(string provider, string model, string apiKey, int timeoutSeconds = EvalSettings.DefaultTimeoutSeconds, HttpMessageHandler? handler = null) : IEvalModel
{
    private readonly HttpClient http = new(handler ?? new HttpClientHandler(), disposeHandler: handler is null)
    {
        Timeout = TimeSpan.FromSeconds(timeoutSeconds),
    };

    public string Provider => provider;

    public string Model => model;

    public async Task<(string Text, int LatencyMs, AiTokens? Tokens)> CompleteAsync(string system, string user, object schema, CancellationToken ct)
    {
        var (body, ms) = await EvalHttp.SendAsync(http, AiProviderRequests.Build(provider, model, apiKey, system, user, schema), ct);
        return (AiProviderRequests.ExtractText(provider, body), ms, AiProviderRequests.ReadTokens(provider, body));
    }
}

/// <summary>
/// The embedding call the matching run makes (<see cref="EvalMatching"/>):
/// the portal's own request and response shapes (<see cref="AiEmbeddings"/>)
/// over the same plain client and the same one retry as the model under test.
/// </summary>
public sealed class HttpEvalEmbedder(string provider, string model, string apiKey, int timeoutSeconds = EvalSettings.DefaultTimeoutSeconds, HttpMessageHandler? handler = null)
{
    private readonly HttpClient http = new(handler ?? new HttpClientHandler(), disposeHandler: handler is null)
    {
        Timeout = TimeSpan.FromSeconds(timeoutSeconds),
    };

    public string Provider => provider;

    public string Model => model;

    /// <summary>One vector per text, in order, and how long the call took; throws <see cref="AiProviderException"/> when the provider would not answer.</summary>
    public async Task<(AiEmbedding Embedding, int LatencyMs)> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        var (body, ms) = await EvalHttp.SendAsync(http, AiEmbeddings.Build(provider, model, apiKey, texts), ct);
        return (new AiEmbedding(AiEmbeddings.ReadVectors(provider, body, texts.Count), AiProviderRequests.ReadTokens(provider, body)), ms);
    }
}

/// <summary>The send both eval clients share: one retry on a 429 or a 5xx, honouring Retry-After, so a provider's bad minute does not fail a case that never ran.</summary>
internal static class EvalHttp
{
    public static async Task<(string Body, int LatencyMs)> SendAsync(HttpClient http, AiProviderRequests.AiRequest request, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var watch = Stopwatch.StartNew();
            using var message = new HttpRequestMessage(HttpMethod.Post, request.Url)
            {
                Content = new StringContent(request.BodyJson, Encoding.UTF8, "application/json"),
            };
            foreach (var (name, value) in request.Headers)
                message.Headers.TryAddWithoutValidation(name, value);

            using var response = await http.SendAsync(message, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (response.IsSuccessStatusCode)
                return (body, (int)watch.ElapsedMilliseconds);

            var status = (int)response.StatusCode;
            if (attempt == 1 && status is 429 or >= 500)
            {
                await Task.Delay(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5), ct);
                continue;
            }
            throw new AiProviderException(status, AiProviderRequests.ErrorDetail(status, body));
        }
    }
}
