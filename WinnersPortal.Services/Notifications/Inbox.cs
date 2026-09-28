using WinnersPortal.Domain;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Services.Email;

namespace WinnersPortal.Services.Notifications;

/// <summary>
/// Writes the inbox — the lines behind the bell. Like <see cref="Notify"/>,
/// a row is added and never saved here: it commits with the state change
/// it reports. Every email about something the reader is party to makes a
/// line, from the same wording, so nothing is said twice in two ways; the
/// broadcasts make theirs in <see cref="Broadcast"/>, for every subscriber
/// whether or not they take email.
/// </summary>
public static class Inbox
{
    /// <summary>
    /// Kinds that never make a line. The account's own plumbing carries a
    /// code or a token in its link, which the bell must not keep; the
    /// digest repeats what the inbox already holds; and the two broadcasts
    /// are added by the fan-out itself, so a subscriber without email is
    /// told too.
    /// </summary>
    public static readonly IReadOnlySet<string> Silent = new HashSet<string>(StringComparer.Ordinal)
    {
        "welcome", "confirm_code", "password_reset", "account_invitation", "digest",
        "opportunity_open", "winner_announced",
    };

    /// <summary>The line an email makes, or none for a silent kind.</summary>
    public static Notification? FromEmail(Guid userId, string kind, EmailContent content, DateTimeOffset now) =>
        Silent.Contains(kind) ? null : Line(userId, kind, content.Subject, FirstParagraph(content.TextBody), content.ActionPath, now);

    public static Notification Line(Guid userId, string kind, string title, string body, string? path, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        Kind = kind,
        Title = Fit(title, Notification.MaxTitleLength),
        Body = Fit(body, Notification.MaxBodyLength),
        Path = path,
        CreatedAtUtc = now,
    };

    /// <summary>
    /// The first paragraph of an email's text: what happened, before the
    /// paragraph that says what to do about it — the click says that.
    /// </summary>
    internal static string FirstParagraph(string text)
    {
        var s = text.Trim();
        var cut = s.IndexOf("\n\n", StringComparison.Ordinal);
        return (cut < 0 ? s : s[..cut]).Replace('\n', ' ').Trim();
    }

    /// <summary>Cut to the column, ending on an ellipsis rather than mid-word where it had to be cut.</summary>
    internal static string Fit(string text, int max)
    {
        if (text.Length <= max) return text;
        var s = text[..(max - 1)];
        var space = s.LastIndexOf(' ');
        if (space > max / 2) s = s[..space];
        return s.TrimEnd() + "…";
    }
}

/// <summary>How long inbox lines are kept. Pure, so the default and the floor are tests.</summary>
public static class InboxRetention
{
    public const string Key = "limits.inboxRetentionDays";
    public const int DefaultDays = 90;

    /// <summary>Days to keep; 0 keeps everything. Anything unparseable is the default.</summary>
    public static int Days(string? configured) =>
        int.TryParse(configured?.Trim(), out var days) && days >= 0 ? days : DefaultDays;

    public static DateTimeOffset? CutOff(string? configured, DateTimeOffset now) =>
        Days(configured) is var days and > 0 ? now.AddDays(-days) : null;
}
