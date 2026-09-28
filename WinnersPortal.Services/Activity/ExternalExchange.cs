using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace WinnersPortal.Services.Activity;

/// <summary>
/// The kinds of outside service the portal calls, one per named HTTP client.
/// The key is what a row stores and the filter sends; the label is what the
/// screen shows.
/// </summary>
public static class ExternalServices
{
    public const string Ai = "ai";
    public const string Identity = "identity";
    public const string GitHub = "github";
    public const string Sms = "sms";
    public const string Captcha = "captcha";
    public const string Push = "push";
    public const string Storage = "storage";
    public const string Email = "email";
    public const string Preview = "preview";

    public static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>
    {
        [Ai] = "AI",
        [Identity] = "Identity verification",
        [GitHub] = "GitHub",
        [Sms] = "SMS",
        [Captcha] = "Captcha",
        [Push] = "Push",
        [Storage] = "Storage",
        [Email] = "Email",
        [Preview] = "Build host",
    };

    public static bool IsKnown(string? service) => service is not null && Labels.ContainsKey(service);
}

/// <summary>
/// One call to an outside API, turned into the two texts a row keeps: what
/// was sent and what came back, each as a request line or status line,
/// headers, a blank line and the body. Pure, so what gets masked is pinned
/// by tests rather than trusted.
///
/// What never reaches a row: a header, query value or body field whose name
/// says it is a secret (a key, a token, a password, a signature, a
/// credential); any value the caller marked secret; and the digits of a
/// texted code. Everything else is kept as it came — the identity
/// provider's answers included, document data and all, because the portal
/// keeps that proof (see IdentityProofWorker). File contents and binary
/// bodies are described, never kept.
/// </summary>
public static partial class ExternalExchange
{
    /// <summary>The longest body a row keeps; the rest is cut and said to be.</summary>
    public const int MaxBody = 64_000;

    /// <summary>The largest response the recorder will buffer to read; above it the body is described.</summary>
    public const long MaxBuffered = 1_000_000;

    public const string Masked = "[redacted]";

    /// <summary>Values the caller knows are secret, carried on the request for the recorder to mask wherever they appear.</summary>
    public static readonly HttpRequestOptionsKey<IReadOnlyCollection<string>> SecretsKey = new("winnersportal.secrets");

    /// <summary>
    /// Marks values the activity log must never show — a key an SMS template
    /// put in the body under a name nobody could guess. Header, query and
    /// field names that say "key" or "token" are masked without it.
    /// </summary>
    public static HttpRequestMessage WithSecrets(this HttpRequestMessage message, params string?[] values)
    {
        var known = message.Options.TryGetValue(SecretsKey, out var already) ? already : [];
        message.Options.Set(SecretsKey, [.. known, .. values.Where(v => !string.IsNullOrEmpty(v)).Select(v => v!)]);
        return message;
    }

    /// <summary>What the caller says the call was about, carried on the request for the row's Subject.</summary>
    public static readonly HttpRequestOptionsKey<string> SubjectKey = new("winnersportal.subject");

    /// <summary>
    /// Names what the call was about on its activity row — an AI call's
    /// provider and model, "openai/gpt-5.5" — so an administrator reads and
    /// filters by it without opening the request.
    /// </summary>
    public static HttpRequestMessage WithSubject(this HttpRequestMessage message, string subject)
    {
        message.Options.Set(SubjectKey, subject);
        return message;
    }

    // ---------------------------------------------------------- naming

    /// <summary>The company behind a host, where it is one the portal is known to call; else the host itself.</summary>
    public static string Vendor(string host)
    {
        host = host.ToLowerInvariant();
        foreach (var (suffix, name) in KnownHosts)
            if (host == suffix || host.EndsWith("." + suffix, StringComparison.Ordinal))
                return name;
        return host;
    }

    private static readonly (string Suffix, string Name)[] KnownHosts =
    [
        ("api.openai.com", "OpenAI"),
        ("api.anthropic.com", "Anthropic"),
        ("generativelanguage.googleapis.com", "Gemini"),
        ("didit.me", "Didit"),
        ("github.com", "GitHub"),
        ("challenges.cloudflare.com", "Cloudflare Turnstile"),
        ("fcm.googleapis.com", "Firebase push"),
        ("push.services.mozilla.com", "Mozilla push"),
        ("notify.windows.com", "Windows push"),
        ("push.apple.com", "Apple push"),
        ("twilio.com", "Twilio"),
        ("amazonaws.com", "Amazon S3"),
        ("mailgun.net", "Mailgun"),
        ("brevo.com", "Brevo"),
    ];

    /// <summary>The row's Action: "Called OpenAI".</summary>
    public static string Action(Uri url) => "Called " + Vendor(url.Host);

    /// <summary>
    /// The row's Path: host and path, never the query — the same rule every
    /// other row keeps. A push endpoint's last segment is the browser's
    /// subscription, and is cut.
    /// </summary>
    public static string Path(string service, Uri url)
    {
        var path = url.AbsolutePath;
        if (service == ExternalServices.Push && path.LastIndexOf('/') is var cut and > 0)
            path = path[..(cut + 1)] + "…";
        return url.IsDefaultPort ? url.Host + path : $"{url.Host}:{url.Port}{path}";
    }

    // ---------------------------------------------------------- the texts

    /// <summary>"POST https://…", the headers, a blank line and the body.</summary>
    public static string RequestText(
        string service, string method, Uri url, IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers,
        string? body, IReadOnlyCollection<string> secrets)
    {
        var text = new StringBuilder();
        text.Append(method).Append(' ').Append(Url(service, url, secrets)).Append('\n');
        AppendHeaders(text, headers, secrets);
        AppendBody(text, body);
        return text.ToString();
    }

    /// <summary>"200 OK · 842 ms", the headers, a blank line and the body.</summary>
    public static string ResponseText(
        int status, string? reason, long elapsedMs, IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers,
        string? body, IReadOnlyCollection<string> secrets)
    {
        var text = new StringBuilder();
        text.Append(status);
        if (!string.IsNullOrWhiteSpace(reason)) text.Append(' ').Append(reason);
        text.Append(" · ").Append(elapsedMs.ToString("N0")).Append(" ms\n");
        AppendHeaders(text, headers, secrets);
        AppendBody(text, body);
        return text.ToString();
    }

    /// <summary>What a call that got no answer leaves: the time it took and the reason, in the exception's words.</summary>
    public static string NoAnswerText(long elapsedMs, Exception e, bool cancelled, IReadOnlyCollection<string> secrets) =>
        Scrub(cancelled
            ? $"No answer · {elapsedMs:N0} ms\n\nCancelled or timed out before the service answered."
            : $"No answer · {elapsedMs:N0} ms\n\n{e.GetType().Name}: {e.Message}", secrets);

    private static void AppendHeaders(StringBuilder text, IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers, IReadOnlyCollection<string> secrets)
    {
        foreach (var (name, values) in headers)
            text.Append(name).Append(": ")
                .Append(SecretName(name) ? Masked : Scrub(string.Join(", ", values), secrets))
                .Append('\n');
    }

    private static void AppendBody(StringBuilder text, string? body)
    {
        if (string.IsNullOrEmpty(body)) return;
        text.Append('\n').Append(body);
    }

    /// <summary>The URL with every secret-named query value masked.</summary>
    public static string Url(string service, Uri url, IReadOnlyCollection<string> secrets)
    {
        var path = Path(service, url);
        var shown = $"{url.Scheme}://{path}";
        if (string.IsNullOrEmpty(url.Query) || url.Query == "?") return Scrub(shown, secrets);
        var pairs = url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(pair =>
        {
            var eq = pair.IndexOf('=');
            var name = Uri.UnescapeDataString(eq < 0 ? pair : pair[..eq]);
            return eq < 0 || !SecretName(name) ? pair : pair[..eq] + "=" + Masked;
        });
        return Scrub(shown + "?" + string.Join('&', pairs), secrets);
    }

    // ---------------------------------------------------------- bodies

    /// <summary>
    /// A body as a row keeps it: JSON re-indented with secret fields masked,
    /// a form with secret fields masked, anything else as text — then any
    /// marked secret replaced wherever it still appears, a texted code's
    /// digits masked, and the whole cut to <see cref="MaxBody"/>.
    /// </summary>
    public static string Body(string service, string? mediaType, string body, IReadOnlyCollection<string> secrets, bool fromProvider)
    {
        var shown = mediaType switch
        {
            _ when IsJson(mediaType) => Json(service, body) ?? body,
            "application/x-www-form-urlencoded" => Form(service, body),
            _ => body,
        };
        shown = Scrub(shown, secrets);
        // The code in a text message is the one secret the gateway is sent
        // on purpose; its answer carries none.
        if (service == ExternalServices.Sms && !fromProvider) shown = CodeDigits().Replace(shown, Masked);
        return Cut(shown);
    }

    /// <summary>What stands in for a body the row does not keep.</summary>
    public static string Described(long? length, string? mediaType, string why) =>
        $"[{(length is { } n ? $"{n:N0} bytes" : "a body")}{(mediaType is null ? "" : " of " + mediaType)} — {why}]";

    /// <summary>Whether a body of this type is text a person can read: JSON, XML, a form, plain text.</summary>
    public static bool Readable(string? mediaType) =>
        mediaType is not null
        && (IsJson(mediaType)
            || mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("application/xml", StringComparison.OrdinalIgnoreCase)
            || mediaType.EndsWith("+xml", StringComparison.OrdinalIgnoreCase));

    private static bool IsJson(string? mediaType) =>
        mediaType is not null
        && (mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
            || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase));

    public static string Cut(string text) =>
        text.Length <= MaxBody ? text : text[..MaxBody] + $"\n… [{text.Length - MaxBody:N0} more characters not kept]";

    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        // Keep a name with an accent, and a URL's ampersand, readable.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string? Json(string service, string body)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return null; // not JSON after all; shown as text
        }
        if (root is null) return body;
        return Mask(root, service)?.ToJsonString(Indented) ?? "null";
    }

    private static JsonNode? Mask(JsonNode? node, string service)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var name in obj.Select(p => p.Key).ToList())
                {
                    var value = obj[name];
                    if (!SecretField(service, name))
                    {
                        // Read into, in place; nothing but a secret name is masked.
                        if (value is JsonObject or JsonArray) Mask(value, service);
                    }
                    // A whole object under a secret name goes; a string does,
                    // and a number stays — max_tokens is a count, not a key.
                    else if (value is JsonObject or JsonArray
                        || (value is JsonValue v && v.GetValueKind() == JsonValueKind.String))
                        obj[name] = Masked;
                }
                return obj;
            case JsonArray array:
                foreach (var item in array)
                    if (item is JsonObject or JsonArray) Mask(item, service);
                return array;
            default:
                return node;
        }
    }

    private static string Form(string service, string body) =>
        string.Join('&', body.Split('&').Select(pair =>
        {
            var eq = pair.IndexOf('=');
            if (eq < 0) return pair;
            var name = Uri.UnescapeDataString(pair[..eq].Replace('+', ' '));
            return SecretField(service, name) || name.Equals("response", StringComparison.OrdinalIgnoreCase)
                ? pair[..eq] + "=" + Masked
                : pair;
        }));

    // ---------------------------------------------------------- names

    /// <summary>
    /// A header, query or field name that says it holds a secret. Numbers
    /// are never masked by name alone — "max_tokens" and "output_tokens" are
    /// counts, not credentials.
    /// </summary>
    public static bool SecretName(string name)
    {
        var n = name.Replace("-", "").Replace("_", "").ToLowerInvariant();
        return n is "key" or "auth" or "sig"
            || n.Contains("secret") || n.Contains("password") || n.Contains("token") || n.Contains("apikey")
            || n.Contains("authorization") || n.Contains("signature") || n.Contains("credential")
            || n.Contains("cookie") || n.Contains("privatekey") || n.EndsWith("amzsecuritytoken", StringComparison.Ordinal);
    }

    /// <summary>
    /// A body field that is a secret for this service in particular: the
    /// code GitHub exchanges for a token, the captcha widget's answer, and an
    /// email's content — its subject, both bodies and its headers carry
    /// reset links, sign-in codes and unsubscribe tokens, so a mail API's row
    /// keeps who it was from and to, and the service's answer, and no more.
    /// </summary>
    private static bool SecretField(string service, string name) =>
        SecretName(name)
        || (service == ExternalServices.GitHub && name.Equals("code", StringComparison.OrdinalIgnoreCase))
        || (service == ExternalServices.Captcha && name.Equals("response", StringComparison.OrdinalIgnoreCase))
        || (service == ExternalServices.Email && EmailContent(name));

    /// <summary>Mailgun's subject, text, html and h: headers; Brevo's subject, htmlContent, textContent and headers.</summary>
    private static bool EmailContent(string name) =>
        name.ToLowerInvariant() is "subject" or "text" or "html" or "htmlcontent" or "textcontent" or "headers"
        || name.StartsWith("h:", StringComparison.OrdinalIgnoreCase);

    /// <summary>Every value the caller marked secret, wherever it still appears — a key the SMS template put in the body.</summary>
    public static string Scrub(string text, IReadOnlyCollection<string> secrets)
    {
        foreach (var secret in secrets)
            if (secret.Length >= 6) text = text.Replace(secret, Masked, StringComparison.Ordinal);
        return text;
    }

    /// <summary>A confirmation code in a text message: four to eight digits standing alone. A phone number is longer.</summary>
    [GeneratedRegex(@"(?<![\d+])\d{4,8}(?!\d)")]
    private static partial Regex CodeDigits();

    /// <summary>The media type off a content's headers, lower-cased; null when it has none.</summary>
    public static string? MediaType(HttpContentHeaders? headers) => headers?.ContentType?.MediaType?.ToLowerInvariant();
}
