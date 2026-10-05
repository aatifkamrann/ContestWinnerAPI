namespace WinnersPortal.Domain;

/// <summary>
/// A member's report of a conversation they are party to: the client or the
/// entrant says something in it is wrong, why, and in a few words what. The
/// conversation is the entry, so the report hangs off the entry like the
/// lines do. Open until an administrator marks it reviewed, which stamps
/// <see cref="ResolvedAtUtc"/>; a reporter has at most one open report per
/// conversation. The other side is never told.
/// </summary>
public sealed class ChatReport
{
    // Column limits: the one place each is stated.
    public const int MaxReasonLength = 20;
    public const int MaxDetailsLength = 1000;
    public const int MaxResolutionLength = 500;

    public Guid Id { get; set; }

    public Guid EntryId { get; set; }
    public Entry? Entry { get; set; }

    /// <summary>The entrant or the client — the entry says which.</summary>
    public Guid ReporterId { get; set; }
    public User? Reporter { get; set; }

    /// <summary>One of ChatRules.ReportReasons' keys.</summary>
    public required string Reason { get; set; }

    /// <summary>What the reporter added in their own words; required for "other".</summary>
    public string? Details { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    /// <summary>When an administrator marked it reviewed; null while open.</summary>
    public DateTimeOffset? ResolvedAtUtc { get; set; }

    /// <summary>Who marked it reviewed; nulled if that account is ever deleted outright.</summary>
    public Guid? ResolvedById { get; set; }
    public User? ResolvedBy { get; set; }

    /// <summary>The administrator's note on what was done, for the next one to read.</summary>
    public string? Resolution { get; set; }
}
