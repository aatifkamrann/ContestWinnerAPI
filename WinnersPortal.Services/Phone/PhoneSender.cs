using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using WinnersPortal.Services.Activity;
using WinnersPortal.Services.Settings;

namespace WinnersPortal.Services.Phone;

/// <summary>
/// The one thing the portal ever texts: a confirmation code. It goes out
/// through an HTTP SMS gateway — one request, with the key in a header or
/// in the body — rather than a carrier SDK, because every gateway worth
/// recommending to a portal this size speaks that shape. The operator picks
/// one from <see cref="PhoneProviders"/> and gives it a key; the address
/// and the request body come with the choice, so there is nothing to copy
/// out of a vendor's documentation and get subtly wrong. "Something else"
/// leaves all three fields to them.
///
/// Sent inline from the request rather than through an outbox: the person
/// is looking at "check your phone", and whether it went is part of what
/// that screen has to say. The email code takes the outbox as every email
/// does. Ten seconds is the most a registration will wait on the gateway.
///
/// A portal can hold several gateways (Settings/Setups.cs), one of them
/// active: a text goes through the active one.
/// </summary>
public sealed class PhoneSender(SettingsService settings, IHttpClientFactory httpFactory, ILogger<PhoneSender> log)
{
    public const string HttpClientName = "phone";
    public const string ProviderKey = "phone.provider";
    public const string ApiKeyKey = "phone.gatewayApiKey";
    public const string SenderKey = "phone.senderId";
    public const string UrlKey = "phone.gatewayUrl";
    public const string HeaderKey = "phone.gatewayAuthHeader";
    public const string BodyKey = "phone.gatewayBody";

    /// <summary>What "Something else" starts from — TextBee's request, the simplest of the lot.</summary>
    public const string DefaultBody = """{"recipients":["{to}"],"message":"{text}"}""";
    public const string DefaultHeader = "x-api-key";

    // The default encoder writes + as +, which every parser reads and
    // no operator debugging a gateway log wants to. Quotes, backslashes and
    // control characters are still escaped, which is the whole point.
    private static readonly JavaScriptEncoder Relaxed = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;

    /// <summary>A resolved gateway: everything one send needs, and nothing from settings after this.</summary>
    private sealed record Gateway(
        string Name, string Url, GatewayFormat Format, string Body, string? AuthHeader, string? ApiKey, string Sender);

    /// <summary>Pure: an address worth calling is an absolute http(s) one.</summary>
    public static bool Configured(string? url) =>
        Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    /// <summary>Whether this portal can text at all — the join form asks before it promises to.</summary>
    public async Task<bool> CanSendAsync(CancellationToken ct = default) =>
        await GatewayAsync(ct) is not null;

    /// <summary>The active setup's gateway, once it names one the portal can call.</summary>
    private async Task<Gateway?> GatewayAsync(CancellationToken ct) =>
        await settings.ActiveSetupAsync(Setups.Phone, ct) is { } active ? Resolve(active) : null;

    /// <summary>Pure: whether a setup names a gateway the portal can call — a provider it knows, or a complete custom request.</summary>
    public static bool Ready(SetupValues s) => Resolve(s) is not null;

    private static Gateway? Resolve(SetupValues s)
    {
        var chosen = s.Get(ProviderKey);
        var apiKey = s.Get(ApiKeyKey);
        var sender = s.Get(SenderKey)?.Trim() ?? "";

        if (PhoneProviders.Find(chosen) is { } preset)
            return new Gateway(s.Name, preset.Url, preset.Format, preset.Body, preset.AuthHeader, apiKey, sender);

        if (!PhoneProviders.IsCustom(chosen)) return null; // "none", unset, or a key nothing answers to

        var url = s.Get(UrlKey)?.Trim();
        if (!Configured(url)) return null;
        var header = s.Get(HeaderKey)?.Trim();
        var body = s.Get(BodyKey);
        return new Gateway(
            s.Name,
            url!,
            // A custom request that looks like form fields is sent as form
            // fields: an operator who pasted a=1&b=2 did not mean JSON.
            body is not null && body.TrimStart().StartsWith('{') ? GatewayFormat.Json : GatewayFormat.Form,
            string.IsNullOrWhiteSpace(body) ? DefaultBody : body,
            // A body carrying {key} has already said where the key goes;
            // sending it in a header as well would hand it to the gateway
            // twice, in a place it never asked for it.
            body?.Contains("{key}", StringComparison.Ordinal) == true
                ? null
                : string.IsNullOrEmpty(header) ? DefaultHeader : header,
            apiKey,
            sender);
    }

    /// <summary>
    /// The request body with its placeholders filled and escaped for the
    /// format, so a message with a quote or an ampersand in it still leaves
    /// one well-formed document and no value can break out of its field.
    /// </summary>
    public static string Body(string template, GatewayFormat format, string to, string text, string from, string key)
    {
        var escape = format == GatewayFormat.Json
            ? (Func<string, string>)(v => JsonEncodedText.Encode(v, Relaxed).ToString())
            : Uri.EscapeDataString;
        return template
            .Replace("{to}", escape(to), StringComparison.Ordinal)
            .Replace("{text}", escape(text), StringComparison.Ordinal)
            .Replace("{from}", escape(from), StringComparison.Ordinal)
            .Replace("{key}", escape(key), StringComparison.Ordinal);
    }

    /// <summary>
    /// One message to one number, through the active gateway. False, with
    /// the gateway's own words, on any failure — never a throw, because the
    /// caller's next line is "the code went by email instead" and that has
    /// to be reachable.
    /// </summary>
    public async Task<(bool Ok, string Detail)> SendAsync(string to, string text, CancellationToken ct = default) =>
        await GatewayAsync(ct) is { } gateway
            ? await SendAsync(gateway, to, text, ct)
            : (false, "No SMS gateway is configured on this portal.");

    /// <summary>
    /// One message through one named setup, active or not — the settings
    /// test, which proves a gateway before it carries codes.
    /// </summary>
    public async Task<(bool Ok, string Detail)> SendThroughAsync(string setupId, string to, string text, CancellationToken ct = default)
    {
        var setup = await settings.SetupAsync(Setups.Phone, setupId, ct);
        if (setup is null)
            return (false, "That phone message setup is not in the list any more — reload the settings.");
        if (Resolve(setup) is not { } gateway)
            return (false, "This setup names no gateway yet — choose a provider, or complete the custom request.");
        return await SendAsync(gateway, to, text, ct);
    }

    private async Task<(bool Ok, string Detail)> SendAsync(Gateway gateway, string to, string text, CancellationToken ct)
    {
        try
        {
            var body = Body(gateway.Body, gateway.Format, to, text, gateway.Sender, gateway.ApiKey ?? "");
            using var request = new HttpRequestMessage(HttpMethod.Post, gateway.Url)
            {
                Content = new StringContent(
                    body,
                    Encoding.UTF8,
                    gateway.Format == GatewayFormat.Json ? "application/json" : "application/x-www-form-urlencoded"),
            };
            // A template may put the key anywhere, under any name; the
            // activity log's copy of this request masks it wherever it lands.
            request.WithSecrets(gateway.ApiKey);
            if (!string.IsNullOrEmpty(gateway.ApiKey) && gateway.AuthHeader is { } header)
            {
                // "Authorization" is the one header with a grammar of its
                // own: a key with a colon in it is a user and a password
                // (Twilio's SID and token, and every provider that copied
                // them), anything else is a bearer token. Every other
                // header name is sent exactly as given.
                if (header.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                    request.Headers.Authorization = gateway.ApiKey.Contains(':', StringComparison.Ordinal)
                        ? new AuthenticationHeaderValue("Basic",
                            Convert.ToBase64String(Encoding.UTF8.GetBytes(gateway.ApiKey)))
                        : new AuthenticationHeaderValue("Bearer", gateway.ApiKey);
                else
                    request.Headers.TryAddWithoutValidation(header, gateway.ApiKey);
            }

            using var response = await httpFactory.CreateClient(HttpClientName).SendAsync(request, ct);
            var answer = await response.Content.ReadAsStringAsync(ct);
            var quoted = answer.Length == 0 ? "" : $": {(answer.Length > 300 ? answer[..300] : answer)}";

            // Several of these gateways answer 200 whatever they think of
            // the request and say what they think in the body — so a
            // refusal that reads like one is reported as one, in their
            // words, rather than as a code that silently went nowhere.
            if (!response.IsSuccessStatusCode)
            {
                log.LogWarning("SMS to {To} refused by {Gateway} with {Status}.", to, gateway.Name, (int)response.StatusCode);
                return (false, $"The gateway answered {(int)response.StatusCode}{(quoted.Length == 0 ? "." : quoted)}");
            }
            if (LooksLikeRefusal(answer))
            {
                log.LogWarning("SMS to {To} accepted by {Gateway} with an error body.", to, gateway.Name);
                return (false, $"The gateway took the request and refused it{quoted}");
            }
            return (true, $"The gateway accepted the message ({(int)response.StatusCode}){quoted}");
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Timeout, DNS, TLS — the gateway, not the number.
            log.LogWarning(e, "SMS to {To} failed to send through {Gateway}.", to, gateway.Name);
            return (false, $"The gateway could not be reached: {e.Message}");
        }
    }

    /// <summary>
    /// A 200 that is really a no. Deliberately narrow — a body that merely
    /// carries an empty error field, or a message id with letters in it,
    /// must not turn a delivered code into a reported failure. Pure, so the
    /// guesswork is at least pinned by tests.
    /// </summary>
    public static bool LooksLikeRefusal(string body)
    {
        var b = body.TrimStart();
        if (b.Length == 0) return false;
        // The plain-text convention the PHP-era gateways share: the whole
        // answer is "ERR …", "ERROR: …", "FAILED …", "Invalid …".
        if (b.StartsWith("ERR", StringComparison.OrdinalIgnoreCase)
            || b.StartsWith("FAIL", StringComparison.OrdinalIgnoreCase)
            || b.StartsWith("INVALID", StringComparison.OrdinalIgnoreCase)) return true;
        // JSON that says so in the two fields everybody spells the same way.
        return b.StartsWith('{')
            && (b.Replace(" ", "").Contains("\"success\":false", StringComparison.OrdinalIgnoreCase)
                || b.Replace(" ", "").Contains("\"status\":\"error\"", StringComparison.OrdinalIgnoreCase));
    }
}
