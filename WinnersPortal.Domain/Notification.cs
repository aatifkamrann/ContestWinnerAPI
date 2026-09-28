namespace WinnersPortal.Domain;

/// <summary>
/// One line in a member's inbox — what the bell in the header opens. A row
/// is written in the same SaveChanges as the event it reports, beside the
/// email about it, so the bell and the mail never disagree about what
/// happened; it is read by the member and sent nowhere. A click marks it
/// read and follows <see cref="Path"/>; the member can mark it unread
/// again, or the whole inbox either way at once. Rows older than
/// <c>limits.inboxRetentionDays</c> are swept.
/// </summary>
public sealed class Notification
{
    // Column limits: the one place a length is stated; the rules and the
    // DbContext both read it here. A title is an email subject's length,
    // a body a push body's.
    public const int MaxTitleLength = 300;
    public const int MaxBodyLength = 400;

    public Guid Id { get; set; }

    public Guid UserId { get; set; }
    public User? User { get; set; }

    /// <summary>What happened, for the operator: application_received, award_won, opportunity_open…</summary>
    public required string Kind { get; set; }

    public required string Title { get; set; }

    /// <summary>One paragraph: the fact that decides whether to click.</summary>
    public required string Body { get; set; }

    /// <summary>Portal-relative; where a click goes. Null where there is nowhere to go.</summary>
    public string? Path { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    /// <summary>When the member read it; null while unread. Marking it unread clears it.</summary>
    public DateTimeOffset? ReadAtUtc { get; set; }
}
