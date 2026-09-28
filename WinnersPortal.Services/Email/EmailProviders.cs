using WinnersPortal.Services.Settings;

namespace WinnersPortal.Services.Email;

/// <summary>
/// The ways an email setup can send: an SMTP server, or one of two mail
/// services over their HTTPS APIs. The APIs are worth having beside SMTP for
/// one reason above the rest: they leave on port 443, which no host blocks,
/// where SMTP needs 587 or 465 open outbound — and EC2, among others,
/// throttles or shuts those until someone asks. They also answer each send
/// with a message id and the service's own words, where a relay says 250 and
/// is never heard from again.
///
/// <para>Both services also offer an SMTP relay; an operator who prefers
/// that picks SMTP here and types the relay's host and login instead.</para>
/// </summary>
public static class EmailProviders
{
    public const string ProviderKey = "email.provider";
    public const string ApiKeyKey = "email.apiKey";
    public const string DomainKey = "email.mailgunDomain";
    public const string RegionKey = "email.mailgunRegion";

    public const string Smtp = "smtp";
    public const string Mailgun = "mailgun";
    public const string Brevo = "brevo";

    /// <summary>Mailgun keeps an account's domains in one region, and each region has an API host of its own.</summary>
    public const string RegionUs = "us";
    public const string RegionEu = "eu";

    /// <summary>What the settings dropdown offers, in order: what every install had, then the two APIs.</summary>
    public static readonly IReadOnlyList<(string Value, string Label)> Choices =
    [
        (Smtp, "SMTP server"),
        (Mailgun, "Mailgun (HTTPS API)"),
        (Brevo, "Brevo (HTTPS API)"),
    ];

    public static readonly IReadOnlyList<(string Value, string Label)> Regions =
    [
        (RegionUs, "US — api.mailgun.net"),
        (RegionEu, "EU — api.eu.mailgun.net"),
    ];

    /// <summary>
    /// The fields each way of sending reads. A field in none of these lists —
    /// the choice itself, the From address — belongs to every one. The
    /// settings screen shows a setup only the fields its choice reads
    /// (SettingsScreen.tsx keeps the same lists).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> FieldsOf =
        new Dictionary<string, IReadOnlyList<string>>
        {
            [Smtp] = ["email.smtpHost", "email.smtpPort", "email.smtpUser", "email.smtpPassword"],
            [Mailgun] = [ApiKeyKey, DomainKey, RegionKey],
            [Brevo] = [ApiKeyKey],
        };

    /// <summary>
    /// The way a setup sends, lower-cased; SMTP when it names none — every
    /// setup saved before there was a choice. Anything else is passed through
    /// as it is, for <see cref="EmailSender.Problem"/> to refuse by name.
    /// </summary>
    public static string Of(SetupValues s) =>
        s.Get(ProviderKey)?.Trim().ToLowerInvariant() is { Length: > 0 } chosen ? chosen : Smtp;

    /// <summary>The API host for a Mailgun region; the US one unless the setup says EU.</summary>
    public static string MailgunBase(SetupValues s) =>
        string.Equals(s.Get(RegionKey)?.Trim(), RegionEu, StringComparison.OrdinalIgnoreCase)
            ? "https://api.eu.mailgun.net"
            : "https://api.mailgun.net";

    public const string BrevoUrl = "https://api.brevo.com/v3/smtp/email";
}
