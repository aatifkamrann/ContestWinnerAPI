using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MimeKit;
using WinnersPortal.Services.Activity;

namespace WinnersPortal.Services.Email;

/// <summary>
/// One way out for the outbox: opened once for a batch and sent through as
/// often as the batch needs. An SMTP session is a real connection; a mail
/// service's API is one HTTPS request a message, with nothing to open.
/// </summary>
public interface IMailTransport : IAsyncDisposable
{
    /// <summary>Where the mail goes, for the log and the test's answer: "smtp.example.com:587", "Mailgun (mg.example.com)".</summary>
    string Where { get; }

    /// <summary>
    /// Sends one message. A <see cref="MailServiceDown"/> is a failure every
    /// message would share; any other exception is this message's own.
    /// </summary>
    Task SendAsync(MimeMessage message, CancellationToken ct);
}

/// <summary>
/// A send that failed for a reason every message would share — the key
/// refused, the service throttling or down, the network — rather than one of
/// this message's own. The outbox stops its batch and leaves the message's
/// attempts alone, as it always has when an SMTP server will not connect: a
/// wrong key must not use up eight retries on every email in the queue.
/// </summary>
public sealed class MailServiceDown(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>An SMTP session, connected and signed in by <see cref="EmailSender.OpenAsync"/>.</summary>
internal sealed class SmtpTransport(MailKit.Net.Smtp.SmtpClient client, string where) : IMailTransport
{
    public string Where => where;

    public async Task SendAsync(MimeMessage message, CancellationToken ct) => await client.SendAsync(message, ct);

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (client.IsConnected) await client.DisconnectAsync(true);
        }
        catch (Exception)
        {
            // the messages are sent and recorded; a rude goodbye changes nothing
        }
        client.Dispose();
    }
}

/// <summary>
/// A mail service's HTTPS API. Mailgun and Brevo differ only in the request
/// they want and the advice worth giving when they refuse it, so both are
/// this one class with a different <see cref="MailApi"/> request.
/// </summary>
internal sealed class ApiTransport(
    HttpClient http, string provider, string where, string apiKey, Func<MimeMessage, HttpRequestMessage> request)
    : IMailTransport
{
    public string Where => where;

    public async Task SendAsync(MimeMessage message, CancellationToken ct)
    {
        // The key rides in a header either way; marked, so the activity
        // log's copy of the request masks it wherever else it might land.
        using var outgoing = request(message).WithSecrets(apiKey);
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(outgoing, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // DNS, TLS, a timeout — the service, not the message.
            throw new MailServiceDown($"{where} could not be reached: {e.Message}", e);
        }
        using (response)
        {
            if (response.IsSuccessStatusCode) return;
            var status = (int)response.StatusCode;
            var said = MailApi.Said(await response.Content.ReadAsStringAsync(ct));
            var text = $"{where} answered {status}{(said.Length == 0 ? "" : ": " + said)}{MailApi.Hint(provider, status)}";
            if (MailApi.ServiceWide(status)) throw new MailServiceDown(text);
            throw new InvalidOperationException(text);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// The two mail APIs' requests, built from the one MIME message every portal
/// email is composed as (<see cref="EmailSender.Compose"/>) — so the test
/// send, the worker and all three ways out carry the same subject, the same
/// two bodies and the same unsubscribe header. Pure, so what each service is
/// sent is pinned by tests.
/// </summary>
public static class MailApi
{
    /// <summary>
    /// A refusal every message would get: the key (401), throttling (429), the
    /// service itself (5xx). Kept narrow on purpose — anything else stops only
    /// its own message, since a batch that stopped at a message the service
    /// will never take would hold every email behind it until it expired.
    /// </summary>
    public static bool ServiceWide(int status) => status is 401 or 429 or >= 500;

    /// <summary>
    /// Mailgun's form: from, to, subject, both bodies, and the unsubscribe
    /// header as h:List-Unsubscribe — Mailgun's way of carrying a header.
    /// </summary>
    public static List<KeyValuePair<string, string>> MailgunForm(MimeMessage m)
    {
        var form = new List<KeyValuePair<string, string>>
        {
            new("from", m.From.Mailboxes.First().ToString()),
            new("to", m.To.Mailboxes.First().ToString()),
            new("subject", m.Subject ?? ""),
        };
        if (m.TextBody is { } text) form.Add(new("text", text));
        if (m.HtmlBody is { } html) form.Add(new("html", html));
        if (m.Headers["List-Unsubscribe"] is { } stop) form.Add(new("h:List-Unsubscribe", stop));
        return form;
    }

    public static HttpRequestMessage Mailgun(string baseUrl, string domain, string apiKey, MimeMessage m) =>
        new(HttpMethod.Post, $"{baseUrl}/v3/{Uri.EscapeDataString(domain)}/messages")
        {
            Content = new FormUrlEncodedContent(MailgunForm(m)),
            // Mailgun signs in as the user "api", with the key as its password.
            Headers = { Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes("api:" + apiKey))) },
        };

    public sealed record BrevoAddress(string Email, string? Name);

    public sealed record BrevoEmail(
        BrevoAddress Sender,
        IReadOnlyList<BrevoAddress> To,
        string Subject,
        string? HtmlContent,
        string? TextContent,
        IReadOnlyDictionary<string, string>? Headers);

    public static readonly JsonSerializerOptions BrevoJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Brevo's transactional email: the same parts as JSON, a name left out rather than sent empty.</summary>
    public static BrevoEmail BrevoBody(MimeMessage m)
    {
        static BrevoAddress Address(MailboxAddress a) =>
            new(a.Address, string.IsNullOrWhiteSpace(a.Name) ? null : a.Name);
        return new BrevoEmail(
            Address(m.From.Mailboxes.First()),
            [Address(m.To.Mailboxes.First())],
            m.Subject ?? "",
            m.HtmlBody,
            m.TextBody,
            m.Headers["List-Unsubscribe"] is { } stop
                ? new Dictionary<string, string> { ["List-Unsubscribe"] = stop }
                : null);
    }

    public static HttpRequestMessage Brevo(string apiKey, MimeMessage m)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, EmailProviders.BrevoUrl)
        {
            Content = JsonContent.Create(BrevoBody(m), options: BrevoJson),
        };
        request.Headers.Add("api-key", apiKey);
        request.Headers.Accept.ParseAdd("application/json");
        return request;
    }

    /// <summary>
    /// The service's own words from a refusal: the "message" both of them put
    /// in a JSON answer, or the answer itself when it is plain text ("Forbidden").
    /// </summary>
    public static string Said(string body)
    {
        var text = body.Trim();
        if (text.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                    text = message.GetString()!.Trim();
            }
            catch (JsonException)
            {
                // not JSON after all; quoted as it came
            }
        }
        return text.Length > 300 ? text[..300] + "…" : text;
    }

    /// <summary>What to check, for the refusals whose cause is always the same few things.</summary>
    public static string Hint(string provider, int status) => (provider, status) switch
    {
        (EmailProviders.Mailgun, 401) =>
            " — check the API key, and that the region is the one the account is in.",
        (EmailProviders.Mailgun, 404) =>
            " — check the sending domain, and that the region is the one it was added in.",
        (EmailProviders.Brevo, 401) =>
            " — check the API key (an API key, not an SMTP key), and that Brevo’s Authorised IPs let this server in.",
        _ => "",
    };
}
