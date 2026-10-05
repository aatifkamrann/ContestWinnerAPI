using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinnersPortal.Services.Activity;

namespace WinnersPortal.Services.Identity;

/// <summary>
/// Request and response shapes for the verification providers, kept pure so
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

    /// <summary>
    /// What the portal asks a session for, whichever provider opens it.
    /// <paramref name="Reference"/> is the portal's own name for the session
    /// — Shufti Pro takes it as the request's reference and hands it back on
    /// every callback; Didit names its sessions itself and never sees it.
    /// <paramref name="WebhookUrl"/> is where Shufti Pro posts its verdicts;
    /// Didit's destination is set in its console instead.
    /// </summary>
    public sealed record SessionAsk(
        Guid UserId, string Reference, string ReturnUrl, string WebhookUrl, string Email, string? Language);

    public const string DiditBase = "https://verification.didit.me/v3";
    public const string ShuftiBase = "https://api.shuftipro.com";

    /// <summary>The route Shufti Pro's callbacks come to; Didit's comes to the bare identity route.</summary>
    public const string ShuftiWebhookPath = "/api/webhooks/identity/shufti";

    /// <summary>
    /// A fresh reference for a Shufti Pro request: the member's id, so the
    /// callback names its member (<see cref="UserOfReference"/>), and a
    /// random tail, because Shufti Pro refuses a reference used before and a
    /// member may verify more than once.
    /// </summary>
    public static string NewReference(Guid userId) => $"wp-{userId:N}-{Guid.NewGuid():N}"[..44];

    /// <summary>The member a reference made by <see cref="NewReference"/> names; null for anything else.</summary>
    public static Guid? UserOfReference(string? reference) =>
        reference is { Length: >= 35 } r && r.StartsWith("wp-", StringComparison.Ordinal)
        && Guid.TryParseExact(r.AsSpan(3, 32), "N", out var id)
            ? id
            : null;

    public static ProviderRequest CreateSession(IdentityProviderConfig config, SessionAsk ask) =>
        config.Provider switch
        {
            IdentityProviders.Didit => new ProviderRequest(
                HttpMethod.Post,
                DiditBase + "/session/",
                new Dictionary<string, string> { ["x-api-key"] = config.ApiKey },
                JsonSerializer.Serialize(new
                {
                    workflow_id = config.WorkflowId,
                    // The member's id, not their email: it is what the
                    // webhook hands back, and the one thing the provider
                    // holds that names a row here.
                    vendor_data = ask.UserId.ToString(),
                    callback = ask.ReturnUrl,
                    // Only the browser the member started in is sent back —
                    // it is the one signed in here, and the one whose page
                    // they left. A phone they finished on after the QR hand-
                    // off stays on the provider's own "done" page.
                    callback_method = "initiator",
                    contact_details = new { email = ask.Email },
                    language = ask.Language ?? "en",
                })),
            IdentityProviders.ShuftiPro => new ProviderRequest(
                HttpMethod.Post,
                ShuftiBase + "/",
                ShuftiHeaders(config),
                JsonSerializer.Serialize(new
                {
                    reference = ask.Reference,
                    callback_url = ask.WebhookUrl,
                    redirect_url = ask.ReturnUrl,
                    email = ask.Email,
                    // Blank: the member picks the country the document is
                    // from, rather than the portal guessing it.
                    country = "",
                    language = (ask.Language ?? "en").ToUpperInvariant(),
                    verification_mode = "any",
                    allow_retry = "1",
                    // A document, read by OCR — the blank fields are what it
                    // is asked to read off — and a selfie matched to it.
                    document = new
                    {
                        proof = "",
                        supported_types = new[] { "id_card", "passport", "driving_license" },
                        name = "",
                        dob = "",
                        document_number = "",
                        issue_date = "",
                        expiry_date = "",
                    },
                    face = new { proof = "" },
                })),
            _ => throw Unknown(config.Provider),
        };

    public static ProviderRequest ReadDecision(IdentityProviderConfig config, string sessionId) => config.Provider switch
    {
        IdentityProviders.Didit => new ProviderRequest(
            HttpMethod.Get,
            $"{DiditBase}/session/{Uri.EscapeDataString(sessionId)}/decision/",
            new Dictionary<string, string> { ["x-api-key"] = config.ApiKey },
            null),
        IdentityProviders.ShuftiPro => new ProviderRequest(
            HttpMethod.Post,
            ShuftiBase + "/status",
            ShuftiHeaders(config),
            JsonSerializer.Serialize(new { reference = sessionId })),
        _ => throw Unknown(config.Provider),
    };

    /// <summary>Shufti Pro's sign-in: HTTP Basic with the client ID and secret key.</summary>
    private static Dictionary<string, string> ShuftiHeaders(IdentityProviderConfig config) => new()
    {
        ["Authorization"] = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{config.ClientId}:{config.ApiKey}")),
    };

    public static Session ParseSession(string provider, string responseJson)
    {
        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;
        if (provider == IdentityProviders.ShuftiPro)
        {
            var reference = Text(root, "reference");
            var link = Text(root, "verification_url");
            var shuftiEvent = Text(root, "event");
            if (reference is null || link is null)
                throw new IdentityProviderException(0, shuftiEvent is null
                    ? "The provider opened no session — its answer had no reference or verification_url."
                    : $"The provider opened no session — it answered {shuftiEvent}{ShuftiError(root)}.");
            return new Session(reference, link, shuftiEvent ?? ShuftiEvents.Pending);
        }
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
        var status = Text(root, provider == IdentityProviders.ShuftiPro ? "event" : "status")
            ?? throw new IdentityProviderException(0, "The provider's decision carried no status.");
        return new Decision(status, DeclineReason(provider, root));
    }

    /// <summary>
    /// The reason category off a webhook or decision body, when there is
    /// one — never the extracted name or number. Didit: the first feature's
    /// status under decision. Shufti Pro: its declined_reason. Absent on an
    /// approval.
    /// </summary>
    public static string? DeclineReason(string provider, JsonElement root)
    {
        if (provider == IdentityProviders.ShuftiPro)
            return Text(root, "event") == ShuftiEvents.Declined ? Text(root, "declined_reason") : null;
        if (!root.TryGetProperty("decision", out var decision) || decision.ValueKind != JsonValueKind.Object) return null;
        foreach (var feature in decision.EnumerateObject())
        {
            if (feature.Value.ValueKind != JsonValueKind.Object) continue;
            if (Text(feature.Value, "status") is { } s && s != "Approved")
                return $"{feature.Name}: {s}";
        }
        return null;
    }

    /// <summary>Didit's reason, for callers that only ever read Didit's bodies.</summary>
    public static string? DeclineReason(JsonElement root) => DeclineReason(IdentityProviders.Didit, root);

    private static string ShuftiError(JsonElement root) =>
        root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
        && Text(error, "message") is { } message
            ? ": " + (message.Length > 300 ? message[..300] : message)
            : "";

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>
    /// The provider's error body — Didit's {"detail":…} or {"message":…},
    /// Shufti Pro's {"error":{"message":…}} — dug out for a readable note.
    /// </summary>
    public static string ErrorDetail(int statusCode, string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object && Text(error, "message") is { } nested)
                return $"The provider answered {statusCode}: {(nested.Length > 300 ? nested[..300] : nested)}";
            foreach (var name in new[] { "detail", "message", "error" })
                if (root.ValueKind == JsonValueKind.Object && Text(root, name) is { } msg)
                    return $"The provider answered {statusCode}: {(msg.Length > 300 ? msg[..300] : msg)}";
        }
        catch (JsonException)
        {
            // fall through to the generic line
        }
        return $"The provider answered {statusCode}.";
    }

    private static IdentityProviderException Unknown(string provider) => new(0,
        $"Unknown identity provider '{provider}' — the portal supports "
        + string.Join(" and ", IdentityProviders.All.Select(p => $"\"{p.Key}\"")) + ".");
}

/// <summary>
/// Shufti Pro's event words, and what they mean here. A request's status
/// is its latest event; three events carry no verdict of their own.
/// </summary>
public static class ShuftiEvents
{
    public const string Pending = "request.pending";
    public const string Received = "request.received";
    public const string ReviewPending = "review.pending";
    public const string Accepted = "verification.accepted";
    public const string Declined = "verification.declined";
    public const string Timeout = "request.timeout";
    public const string Cancelled = "verification.cancelled";
    public const string StatusChanged = "verification.status.changed";
    public const string DataChanged = "request.data.changed";
    public const string Deleted = "request.deleted";

    /// <summary>
    /// An event that says something changed without saying to what — a
    /// back-office reviewer's new verdict, edited data. The request's status
    /// is read back instead of applying the event.
    /// </summary>
    public static bool ReadBack(string? shuftiEvent) => shuftiEvent is StatusChanged or DataChanged;

    /// <summary>
    /// An event that is no verdict at all: the operator deleted the
    /// request's data at Shufti Pro. The member's standing here is kept —
    /// the portal holds its own copy of the proof.
    /// </summary>
    public static bool Ignored(string? shuftiEvent) => shuftiEvent is Deleted;
}

/// <summary>
/// Verifies Shufti Pro's <c>Signature</c> header: hex SHA-256 of the raw body
/// with the secret key's own hex SHA-256 appended — or, for accounts made
/// before March 2023 that kept their key, the secret key itself appended.
/// Both are tried. Shufti Pro stamps no time on a callback, so a replay is
/// stopped by the delivery's unique id instead (<see cref="DeliveryId"/>).
/// </summary>
public static class ShuftiSignature
{
    public static bool Verify(string secretKey, ReadOnlySpan<byte> payload, string? signatureHeader)
    {
        if (string.IsNullOrEmpty(secretKey) || string.IsNullOrEmpty(signatureHeader) || signatureHeader.Length != 64) return false;
        byte[] given;
        try
        {
            given = Convert.FromHexString(signatureHeader);
        }
        catch (FormatException)
        {
            return false;
        }
        var body = payload.ToArray();
        var current = CryptographicOperations.FixedTimeEquals(Hash(body, Hex(Encoding.UTF8.GetBytes(secretKey))), given);
        var legacy = CryptographicOperations.FixedTimeEquals(Hash(body, secretKey), given);
        return current | legacy;
    }

    /// <summary>The header value for the current scheme; used by tests and a local delivery simulator.</summary>
    public static string Compute(string secretKey, ReadOnlySpan<byte> payload) =>
        Convert.ToHexString(Hash(payload.ToArray(), Hex(Encoding.UTF8.GetBytes(secretKey)))).ToLowerInvariant();

    /// <summary>
    /// A callback's delivery id: its body's hash. The same callback posted
    /// twice is one delivery; a later event on the same request is another.
    /// </summary>
    public static string DeliveryId(ReadOnlySpan<byte> payload) =>
        "shufti:" + Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant()[..40];

    private static byte[] Hash(byte[] body, string suffix)
    {
        var tail = Encoding.UTF8.GetBytes(suffix);
        var all = new byte[body.Length + tail.Length];
        body.CopyTo(all, 0);
        tail.CopyTo(all, body.Length);
        return SHA256.HashData(all);
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

/// <summary>
/// The one HTTP path to the verification providers. A session opened for a
/// member and a verdict read back go through here, and nothing else does —
/// which is what makes "what leaves the server" a one-file audit.
/// </summary>
public sealed class IdentityProviderClient(IHttpClientFactory httpFactory)
{
    public const string HttpClientName = "identity";

    public async Task<IdentityProviderRequests.Session> CreateSessionAsync(
        IdentityProviderConfig config, IdentityProviderRequests.SessionAsk ask, CancellationToken ct)
    {
        var body = await SendAsync(IdentityProviderRequests.CreateSession(config, ask), ct);
        return IdentityProviderRequests.ParseSession(config.Provider, body);
    }

    public async Task<IdentityProviderRequests.Decision> ReadDecisionAsync(
        IdentityProviderConfig config, string sessionId, CancellationToken ct) =>
        IdentityProviderRequests.ParseDecision(config.Provider, await ReadDecisionJsonAsync(config, sessionId, ct));

    /// <summary>The decision as the provider wrote it, every field — what the proof worker keeps.</summary>
    public Task<string> ReadDecisionJsonAsync(IdentityProviderConfig config, string sessionId, CancellationToken ct) =>
        SendAsync(IdentityProviderRequests.ReadDecision(config, sessionId), ct);

    /// <summary>
    /// One image off a decision. Didit's links are signed — no key sent, the
    /// link is the permission — and are fetched with a GET. Shufti Pro's are
    /// fetched with a POST carrying the decision's access token. Null content
    /// type when the answer carried none; refuses anything over
    /// <paramref name="maxBytes"/>.
    /// </summary>
    public async Task<(byte[] Bytes, string? ContentType)> DownloadAsync(
        IdentityProof.Image image, long maxBytes, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(image.AccessToken is null ? HttpMethod.Get : HttpMethod.Post, image.Url);
        if (image.AccessToken is not null)
        {
            message.Content = new StringContent(
                JsonSerializer.Serialize(new { access_token = image.AccessToken }), Encoding.UTF8, "application/json");
            message.WithSecrets([image.AccessToken]);
        }
        using var response = await httpFactory.CreateClient(HttpClientName)
            .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new IdentityProviderException((int)response.StatusCode, $"The image link answered {(int)response.StatusCode}.");
        if (response.Content.Headers.ContentLength > maxBytes)
            throw new IdentityProviderException(0, $"The image is over {maxBytes / (1024 * 1024)} MB; its link is kept instead.");
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        if (bytes.LongLength > maxBytes)
            throw new IdentityProviderException(0, $"The image is over {maxBytes / (1024 * 1024)} MB; its link is kept instead.");
        return (bytes, response.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>
    /// The raw status and body of one call, for the settings test — a
    /// made-up session asked about proves the key is accepted.
    /// </summary>
    public async Task<(int Status, string Body)> ProbeAsync(IdentityProviderRequests.ProviderRequest request, CancellationToken ct)
    {
        using var message = Message(request);
        using var response = await httpFactory.CreateClient(HttpClientName).SendAsync(message, ct);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(ct));
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
