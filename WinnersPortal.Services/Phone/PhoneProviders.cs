namespace WinnersPortal.Services.Phone;

/// <summary>How a gateway wants its request written.</summary>
public enum GatewayFormat
{
    /// <summary>A JSON document. Values are JSON-encoded into the template.</summary>
    Json,

    /// <summary>Form fields, as a POST body rather than a query string — an API
    /// key in a URL ends up in every access log between here and there.</summary>
    Form,
}

/// <summary>
/// One gateway the portal knows how to talk to without being told: its
/// address, the shape of its request, and where the key goes. Everything
/// here is what the operator would otherwise have had to copy out of a
/// vendor's documentation into three free-text boxes and get exactly right.
///
/// <para>Placeholders, filled and escaped for the format: <c>{to}</c> the
/// number, <c>{text}</c> the message, <c>{from}</c> the sender ID or
/// from-number, <c>{key}</c> the API key for gateways that take it in the
/// body rather than a header.</para>
/// </summary>
/// <param name="AuthHeader">
/// The header carrying the key, or null when the key rides in the body.
/// "Authorization" is sent as a bearer token; any other name is sent as-is.
/// </param>
/// <param name="Sender">
/// What this gateway calls the sender, for the settings label — or null when
/// it has no such notion and the field can be left empty.
/// </param>
public sealed record PhoneProvider(
    string Key,
    string Label,
    string Url,
    GatewayFormat Format,
    string Body,
    string? AuthHeader,
    string? Sender);

/// <summary>
/// The gateways offered in the Phone settings group, in the order an
/// operator should consider them: the free ones first, because a portal
/// this size sends a handful of codes a day and a spare Android phone with
/// a local SIM does that for the price of an SMS bundle; then the cheap
/// local route for Pakistan; then a global carrier API, which is the right
/// answer only when the members are spread across countries — sending to
/// Pakistan through one costs roughly thirty times what a Pakistani
/// aggregator charges.
/// </summary>
public static class PhoneProviders
{
    /// <summary>Chosen when the portal should not text at all — codes go by email only.</summary>
    public const string None = "none";

    /// <summary>Chosen when the operator writes the request themselves, in the three fields below.</summary>
    public const string Custom = "custom";

    public static readonly IReadOnlyList<PhoneProvider> All =
    [
        new(
            Key: "textbee",
            Label: "TextBee — free, through an Android phone",
            Url: "https://api.textbee.dev/api/v1/gateway/send-sms",
            Format: GatewayFormat.Json,
            Body: """{"recipients":["{to}"],"message":"{text}"}""",
            AuthHeader: "x-api-key",
            Sender: null),

        new(
            Key: "httpsms",
            Label: "httpSMS — free, through an Android phone",
            Url: "https://api.httpsms.com/v1/messages/send",
            Format: GatewayFormat.Json,
            Body: """{"from":"{from}","to":"{to}","content":"{text}"}""",
            AuthHeader: "x-api-key",
            Sender: "The phone's own number, in international form"),

        new(
            Key: "sendpk",
            Label: "SendPK — Pakistani short-code route",
            Url: "https://sendpk.com/api/sms.php",
            Format: GatewayFormat.Form,
            Body: "api_key={key}&sender={from}&mobile={to}&message={text}",
            AuthHeader: null, // their key is a form field, not a header
            Sender: "The sender ID or short code approved on your SendPK account"),

        new(
            Key: "telnyx",
            Label: "Telnyx — global carrier API",
            Url: "https://api.telnyx.com/v2/messages",
            Format: GatewayFormat.Json,
            Body: """{"from":"{from}","to":"{to}","text":"{text}"}""",
            AuthHeader: "Authorization",
            Sender: "Your Telnyx number or messaging profile ID"),
    ];

    /// <summary>What the settings dropdown offers, in order: off, the four above, then custom.</summary>
    public static readonly IReadOnlyList<(string Value, string Label)> Choices =
    [
        (None, "Off — codes go by email only"),
        .. All.Select(p => (p.Key, p.Label)),
        (Custom, "Something else — I will write the request myself"),
    ];

    /// <summary>The preset by key, or null for "none", "custom", and anything unrecognised.</summary>
    public static PhoneProvider? Find(string? key) =>
        All.FirstOrDefault(p => string.Equals(p.Key, key?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether this choice means the operator supplies the request themselves.</summary>
    public static bool IsCustom(string? key) =>
        string.Equals(key?.Trim(), Custom, StringComparison.OrdinalIgnoreCase);
}
