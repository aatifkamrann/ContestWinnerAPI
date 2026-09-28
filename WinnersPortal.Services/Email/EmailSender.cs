using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using MailKit.Security;
using MimeKit;
using WinnersPortal.Services.Settings;

namespace WinnersPortal.Services.Email;

/// <summary>
/// How a message leaves. The worker still owns real notifications —
/// batching twenty rows through one transport — but it composes each
/// message through <see cref="Compose"/> and opens the transport through
/// <see cref="OpenAsync"/> here, so the settings test send below travels the
/// exact template, sender, and connection every notification uses. A test
/// that took a different path would prove nothing.
///
/// <para>A portal can hold several email setups (Settings/Setups.cs), each an
/// SMTP server or a mail service's HTTPS API (<see cref="EmailProviders"/>).
/// The outbox sends through the active one; the test sends through one named
/// setup, active or not.</para>
/// </summary>
public sealed class EmailSender(SettingsService settings, IHttpClientFactory httpFactory, ILogger<EmailSender> log)
{
    public const string HttpClientName = "email";

    /// <summary>The MIME shape of every portal email — worker batches and the test send alike.</summary>
    public static MimeMessage Compose(
        string portalName, string from, string toName, string toEmail,
        string subject, string textBody, string? actionText, string? url, string accent,
        string? unsubscribeUrl = null)
    {
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(portalName, from));
        mime.To.Add(new MailboxAddress(toName, toEmail));
        mime.Subject = subject;
        // The header mail clients turn into their own unsubscribe button,
        // on the mail that has one.
        if (unsubscribeUrl is not null) mime.Headers.Add("List-Unsubscribe", $"<{unsubscribeUrl}>");
        mime.Body = new BodyBuilder
        {
            TextBody = EmailRender.Text(toName, textBody, actionText, url, portalName, unsubscribeUrl),
            HtmlBody = EmailRender.Html(toName, textBody, actionText, url, portalName, accent, unsubscribeUrl),
        }.ToMessageBody();
        return mime;
    }

    public const string DefaultFrom = "no-reply@winnersportal.local";

    /// <summary>
    /// Pure: what a setup still lacks before it can send — the SMTP host, or
    /// the service's key (and Mailgun's domain) — in words for the test's
    /// answer. Null when it has everything. Whether it is active is the
    /// caller's question.
    /// </summary>
    public static string? Problem(SetupValues s)
    {
        bool Blank(string key) => string.IsNullOrWhiteSpace(s.Get(key));
        return EmailProviders.Of(s) switch
        {
            EmailProviders.Smtp => Blank("email.smtpHost")
                ? "This setup is not complete — an SMTP host is required."
                : null,
            EmailProviders.Mailgun => (Blank(EmailProviders.ApiKeyKey), Blank(EmailProviders.DomainKey)) switch
            {
                (true, true) => "This setup is not complete — Mailgun needs an API key and the sending domain.",
                (true, false) => "This setup is not complete — Mailgun needs an API key.",
                (false, true) => "This setup is not complete — Mailgun needs the sending domain.",
                _ => null,
            },
            EmailProviders.Brevo => Blank(EmailProviders.ApiKeyKey)
                ? "This setup is not complete — Brevo needs an API key."
                : null,
            var other => $"This setup sends through “{other}”, which the portal does not know — choose the email service again.",
        };
    }

    /// <summary>Pure: a setup with everything its way of sending needs.</summary>
    public static bool Ready(SetupValues s) => Problem(s) is null;

    public static int Port(SetupValues s) => int.TryParse(s.Get("email.smtpPort"), out var port) ? port : 25;

    public static string From(SetupValues? s) =>
        s?.Get("email.fromAddress")?.Trim() is { Length: > 0 } from ? from : DefaultFrom;

    private static string Domain(SetupValues s) => s.Get(EmailProviders.DomainKey)!.Trim().TrimEnd('/');

    /// <summary>Pure: where a setup's mail goes, as the log and the test's answer name it.</summary>
    public static string Where(SetupValues s) => EmailProviders.Of(s) switch
    {
        EmailProviders.Mailgun => $"Mailgun ({Domain(s)})",
        EmailProviders.Brevo => "Brevo",
        _ => $"{s.Get("email.smtpHost")?.Trim()}:{Port(s)}",
    };

    /// <summary>
    /// Pure: the test's answer when the SMTP server turns the sign-in down —
    /// the server's own words, then what most often causes it: a space at
    /// either end of the password (kept as typed, since a password may carry
    /// one on purpose), and on Brevo's relay the two values its SMTP & API
    /// page invites pasting in the wrong place.
    /// </summary>
    public static string SignInRefused(SetupValues s, string? serverSaid)
    {
        var parts = new List<string> { "The SMTP server refused the sign-in — check the SMTP user and password." };
        if (serverSaid?.Trim().TrimEnd('.') is { Length: > 0 } said)
            parts.Add($"It answered: “{said}”.");
        var password = s.Get("email.smtpPassword") ?? "";
        if (password.Length > 0 && password.Trim().Length != password.Length)
            parts.Add("The saved password begins or ends with a space — retype it if the space is not part of it.");
        if (IsBrevoRelay(s))
            parts.Add(password.TrimStart().StartsWith("xkeysib-", StringComparison.Ordinal)
                ? "That password is a Brevo API key (xkeysib-…); the relay needs an SMTP key (xsmtpsib-…) from SMTP & API → SMTP."
                : "With Brevo, the user is the Login shown under SMTP & API → SMTP (often …@smtp-brevo.com, "
                  + "not the account's email) and the password an SMTP key (xsmtpsib-…).");
        return string.Join(" ", parts);
    }

    private static bool IsBrevoRelay(SetupValues s) =>
        s.Get("email.smtpHost")?.Trim().ToLowerInvariant() is { } host
        && (host.EndsWith("brevo.com", StringComparison.Ordinal) || host.EndsWith("sendinblue.com", StringComparison.Ordinal));

    /// <summary>The setup the outbox sends through: the active one, once it has what it needs. Null otherwise.</summary>
    public static async Task<SetupValues?> ServerAsync(SettingsService settings, CancellationToken ct) =>
        await settings.ActiveSetupAsync(Setups.Email, ct) is { } active && Ready(active) ? active : null;

    /// <summary>
    /// The way out for one ready setup: an SMTP session connected and signed
    /// in — which throws, as it always has, when the server will not have it
    /// — or a mail service's API, which has nothing to open and answers for
    /// itself at the first send.
    /// </summary>
    public async Task<IMailTransport> OpenAsync(SetupValues s, CancellationToken ct)
    {
        if (Problem(s) is { } problem) throw new InvalidOperationException(problem);
        var key = s.Get(EmailProviders.ApiKeyKey)?.Trim() ?? "";
        switch (EmailProviders.Of(s))
        {
            case EmailProviders.Mailgun:
                var baseUrl = EmailProviders.MailgunBase(s);
                var domain = Domain(s);
                return new ApiTransport(httpFactory.CreateClient(HttpClientName), EmailProviders.Mailgun, Where(s), key,
                    m => MailApi.Mailgun(baseUrl, domain, key, m));
            case EmailProviders.Brevo:
                return new ApiTransport(httpFactory.CreateClient(HttpClientName), EmailProviders.Brevo, Where(s), key,
                    m => MailApi.Brevo(key, m));
            default:
                var client = new MailKit.Net.Smtp.SmtpClient();
                try
                {
                    await ConnectAsync(client, s, ct);
                }
                catch
                {
                    client.Dispose();
                    throw;
                }
                return new SmtpTransport(client, Where(s));
        }
    }

    /// <summary>Whether email can leave — invitations and reset links are refused up front when it cannot.</summary>
    public static async Task<bool> IsConfiguredAsync(SettingsService settings, CancellationToken ct) =>
        await ServerAsync(settings, ct) is not null;

    /// <summary>Connects and, when the setup has a user, signs in — the one way the outbox and the test both open a session.</summary>
    public static async Task ConnectAsync(MailKit.Net.Smtp.SmtpClient client, SetupValues s, CancellationToken ct)
    {
        client.ServerCertificateValidationCallback = AcceptCertificate;
        await client.ConnectAsync(s.Get("email.smtpHost")!.Trim(), Port(s), SecureSocketOptions.Auto, ct);
        // Trimmed here as well as on save: a user stored before the save
        // trimmed it may still carry a pasted space, which no server accepts.
        var user = s.Get("email.smtpUser")?.Trim();
        if (!string.IsNullOrEmpty(user))
            await client.AuthenticateAsync(user, s.Get("email.smtpPassword") ?? "", ct);
    }

    /// <summary>
    /// The server's certificate, judged as a browser judges it. MailKit asks
    /// for revocation to be checked and, by itself, fails the connection when
    /// the revocation list cannot be fetched — which on Linux happens
    /// whenever the certificate authority's list (Let's Encrypt's run to a
    /// quarter of a megabyte, one per shard, and Brevo's relay rotates
    /// between servers on different shards) is slow to arrive. A revoked,
    /// expired, untrusted or misnamed certificate is still refused; only
    /// "could not find out" is let through.
    /// </summary>
    private static bool AcceptCertificate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors) =>
        CertificateAcceptable(errors, chain?.ChainStatus.Select(st => st.Status) ?? []);

    /// <summary>Pure: <see cref="AcceptCertificate"/>'s rule, on the handshake's findings.</summary>
    public static bool CertificateAcceptable(SslPolicyErrors errors, IEnumerable<X509ChainStatusFlags> chainStatus) =>
        errors == SslPolicyErrors.None
        || (errors == SslPolicyErrors.RemoteCertificateChainErrors
            && chainStatus.All(st => (st & ~RevocationUnknown) == X509ChainStatusFlags.NoError));

    private const X509ChainStatusFlags RevocationUnknown =
        X509ChainStatusFlags.RevocationStatusUnknown | X509ChainStatusFlags.OfflineRevocation;

    /// <summary>
    /// The wizard's line, finally honoured: prove the pipeline by sending to
    /// the administrator who is standing right there. Connect, authenticate,
    /// send one real email — a wrong SMTP password or API key answers here in
    /// seconds, not as a notification that silently never arrives.
    /// </summary>
    public async Task<(bool Ok, string Detail)> TestAsync(string setupId, string toName, string toEmail, CancellationToken ct)
    {
        var setup = await settings.SetupAsync(Setups.Email, setupId, ct);
        if (setup is null)
            return (false, "That email setup is not in the list any more — reload the settings.");
        if (Problem(setup) is { } problem)
            return (false, problem);
        var where = Where(setup);
        var from = From(setup);
        var portalName = await settings.GetAsync("branding.portalName", ct) ?? "Winners Portal";
        var publicUrl = await settings.GetAsync("branding.publicUrl", ct) ?? "http://localhost";
        var accent = await settings.GetAsync("branding.accentColor", ct) ?? "#0f766e";

        var content = Emails.TestSend(portalName);
        var mime = Compose(portalName, from, toName, toEmail, content.Subject, content.TextBody,
            content.ActionText,
            content.ActionPath is null ? null : EmailRender.AbsoluteUrl(publicUrl, content.ActionPath),
            accent);

        try
        {
            await using var transport = await OpenAsync(setup, ct);
            await transport.SendAsync(mime, ct);
            return (true, $"Sent to {toEmail} through {where} — now check that inbox, spam folder included.");
        }
        catch (AuthenticationException e)
        {
            log.LogWarning(e, "SMTP test send: authentication refused by {Where}.", where);
            return (false, SignInRefused(setup, e.Message));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Connect refused, DNS, TLS mismatch, a refused key, a rejected
            // sender — the service's own words are the most actionable
            // thing we have. The APIs' refusals already say who answered.
            log.LogWarning(e, "Test email through {Where} failed.", where);
            return (false, e.Message);
        }
    }
}
