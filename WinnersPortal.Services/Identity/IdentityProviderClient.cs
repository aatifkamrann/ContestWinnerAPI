using System.Text;
using System.Text.Json;
using WinnersPortal.Services.Activity;

namespace WinnersPortal.Services.Identity;

/// <summary>
/// Request and response shapes for the verification provider, kept pure so
/// tests can pin the wire format: two calls, a session opened and a
/// decision read. The key travels in a header, never in the URL — URLs end
/// up in proxy and provider logs; headers don't.
/// </summary>
public static class IdentityProviderRequests
{
    public sealed record ProviderRequest(HttpMethod Method, string Url, IReadOnlyDictionary<string, string> Headers, string? BodyJson);

    /// <summary>What a session open answers: the id the webhooks will carry, and where to send the member.</summary>
    public sealed record Session(string SessionId, string Url, string Status);

    /// <summary>What the decision endpoint answers: the status, and on a decline the provider's reason category.</summary>
    public sealed record Decision(string Status, string? Reason);

    public const string DiditBase = "https://verification.didit.me/v3";

    public static ProviderRequest CreateSession(
        string provider, string apiKey, string workflowId, Guid userId, string callbackUrl, string email, string? language) =>
        provider switch
        {
            IdentityProviders.Didit => new ProviderRequest(
                HttpMethod.Post,
                DiditBase + "/session/",
                new Dictionary<string, string> { ["x-api-key"] = apiKey },
                JsonSerializer.Serialize(new
                {
                    workflow_id = workflowId,
                    // The member's id, not their email: it is what the
                    // webhook hands back, and the one thing the provider
                    // holds that names a row here.
                    vendor_data = userId.ToString(),
                    callback = callbackUrl,
                    contact_details = new { email },
                    language = language ?? "en",
                })),
            _ => throw Unknown(provider),
        };

    public static ProviderRequest ReadDecision(string provider, string apiKey, string sessionId) => provider switch
    {
        IdentityProviders.Didit => new ProviderRequest(
            HttpMethod.Get,
            $"{DiditBase}/session/{Uri.EscapeDataString(sessionId)}/decision/",
            new Dictionary<string, string> { ["x-api-key"] = apiKey },
            null),
        _ => throw Unknown(provider),
    };

    public static Session ParseSession(string provider, string responseJson)
    {
        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;
        var id = Text(root, "session_id");
        var url = Text(root, "url");
        if (id is null || url is null)
            throw new IdentityProviderException(0, "The provider opened no session — its answer had no session_id or url.");
        return new Session(id, url, Text(root, "status") ?? "Not Started");
    }

    public static Decision ParseDecision(string provider, string responseJson)
    {
        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;
        var status = Text(root, "status")
            ?? throw new IdentityProviderException(0, "The provider's decision carried no status.");
        return new Decision(status, DeclineReason(root));
    }

    /// <summary>
    /// The reason category off a webhook or decision body, when there is
    /// one: the first feature's status detail under decision, never the
    /// extracted name or number. Absent on an approval.
    /// </summary>
    public static string? DeclineReason(JsonElement root)
    {
        if (!root.TryGetProperty("decision", out var decision) || decision.ValueKind != JsonValueKind.Object) return null;
        foreach (var feature in decision.EnumerateObject())
        {
            if (feature.Value.ValueKind != JsonValueKind.Object) continue;
            if (Text(feature.Value, "status") is { } s && s != "Approved")
                return $"{feature.Name}: {s}";
        }
        return null;
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>The provider's error body is {"detail":…} or {"message":…}; dig it out for a readable note.</summary>
    public static string ErrorDetail(int statusCode, string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            foreach (var name in new[] { "detail", "message", "error" })
                if (Text(doc.RootElement, name) is { } msg)
                    return $"The provider answered {statusCode}: {(msg.Length > 300 ? msg[..300] : msg)}";
        }
        catch (JsonException)
        {
            // fall through to the generic line
        }
        return $"The provider answered {statusCode}.";
    }

    private static IdentityProviderException Unknown(string provider) => new(0,
        $"Unknown identity provider '{provider}' — the portal supports \"{IdentityProviders.Didit}\".");
}

/// <summary>
/// The one HTTP path to the verification provider. A session opened for a
/// member and a verdict read back go through here, and nothing else does —
/// which is what makes "what leaves the server" a one-file audit.
/// </summary>
public sealed class IdentityProviderClient(IHttpClientFactory httpFactory)
{
    public const string HttpClientName = "identity";

    public async Task<IdentityProviderRequests.Session> CreateSessionAsync(
        string provider, string apiKey, string workflowId, Guid userId, string callbackUrl, string email, string? language,
        CancellationToken ct)
    {
        var body = await SendAsync(
            IdentityProviderRequests.CreateSession(provider, apiKey, workflowId, userId, callbackUrl, email, language), ct);
        return IdentityProviderRequests.ParseSession(provider, body);
    }

    public async Task<IdentityProviderRequests.Decision> ReadDecisionAsync(
        string provider, string apiKey, string sessionId, CancellationToken ct) =>
        IdentityProviderRequests.ParseDecision(provider, await ReadDecisionJsonAsync(provider, apiKey, sessionId, ct));

    /// <summary>The decision as the provider wrote it, every field — what the proof worker keeps.</summary>
    public Task<string> ReadDecisionJsonAsync(string provider, string apiKey, string sessionId, CancellationToken ct) =>
        SendAsync(IdentityProviderRequests.ReadDecision(provider, apiKey, sessionId), ct);

    /// <summary>
    /// One image off a decision, from the provider's own signed link — no
    /// key sent, the link is the permission. Null content type when the
    /// answer carried none; refuses anything over <paramref name="maxBytes"/>.
    /// </summary>
    public async Task<(byte[] Bytes, string? ContentType)> DownloadAsync(string url, long maxBytes, CancellationToken ct)
    {
        using var response = await httpFactory.CreateClient(HttpClientName)
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new IdentityProviderException((int)response.StatusCode, $"The image link answered {(int)response.StatusCode}.");
        if (response.Content.Headers.ContentLength > maxBytes)
            throw new IdentityProviderException(0, $"The image is over {maxBytes / (1024 * 1024)} MB; its link is kept instead.");
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        if (bytes.LongLength > maxBytes)
            throw new IdentityProviderException(0, $"The image is over {maxBytes / (1024 * 1024)} MB; its link is kept instead.");
        return (bytes, response.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>The raw status of one call, for the settings test — a 404 on a made-up session proves the key is accepted.</summary>
    public async Task<int> ProbeAsync(IdentityProviderRequests.ProviderRequest request, CancellationToken ct)
    {
        using var message = Message(request);
        using var response = await httpFactory.CreateClient(HttpClientName).SendAsync(message, ct);
        return (int)response.StatusCode;
    }

    private async Task<string> SendAsync(IdentityProviderRequests.ProviderRequest request, CancellationToken ct)
    {
        using var message = Message(request);
        using var response = await httpFactory.CreateClient(HttpClientName).SendAsync(message, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new IdentityProviderException((int)response.StatusCode,
                IdentityProviderRequests.ErrorDetail((int)response.StatusCode, body));
        return body;
    }

    private static HttpRequestMessage Message(IdentityProviderRequests.ProviderRequest request)
    {
        var message = new HttpRequestMessage(request.Method, request.Url);
        foreach (var (name, value) in request.Headers) message.Headers.TryAddWithoutValidation(name, value);
        message.WithSecrets([.. request.Headers.Values]);
        if (request.BodyJson is not null)
            message.Content = new StringContent(request.BodyJson, Encoding.UTF8, "application/json");
        return message;
    }
}

/// <summary>A refusal from the provider, with the status it came with; 0 is the portal's own refusal.</summary>
public sealed class IdentityProviderException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;

    /// <summary>What a member is told — never the provider's own words, which go to the log.</summary>
    public string Friendly => StatusCode switch
    {
        401 or 403 => "The verification provider rejected this portal's key. An administrator can check it under Identity verification in settings.",
        404 => "The verification provider does not know the workflow this portal is set to. An administrator can check it under Identity verification in settings.",
        429 => "The verification provider is rate-limiting this portal for now. Please try again in a few minutes.",
        >= 500 => "The verification provider is not available right now. Please try again in a few minutes.",
        _ => "Verification could not be started this time. Please try again; if it keeps happening, an administrator can check the settings.",
    };
}
