namespace WinnersPortal.Domain;

public enum EmailStatus
{
    /// <summary>Waiting for the worker (or for SMTP to be configured at all).</summary>
    Pending = 0,

    /// <summary>Accepted by the SMTP server.</summary>
    Sent = 1,

    /// <summary>Gave up — repeated send failures, or the row expired unsent.
    /// <see cref="EmailMessage.LastError"/> says which.</summary>
    Failed = 2,
}

/// <summary>
/// The email outbox. A row is written in the same SaveChanges as the state
/// change it announces — an entry that exists but whose email was lost, or an
/// email about an entry that was rolled back, are both impossible by
/// construction. The worker drains rows in the background so no HTTP request
/// ever waits on an SMTP server.
///
/// The body is stored as text paragraphs plus a relative action path; the
/// worker renders HTML and absolutizes the link at send time, so a change to
/// the layout or the portal's public URL applies to everything still queued.
/// </summary>
public sealed class EmailMessage
{
    public Guid Id { get; set; }

    /// <summary>What happened, for the operator: entry_received, repo_ready, digest…</summary>
    public required string Kind { get; set; }

    public required string ToEmail { get; set; }
    public required string ToName { get; set; }

    public required string Subject { get; set; }

    /// <summary>Plain-text paragraphs separated by blank lines. Rendered into
    /// both MIME parts — there is no separately authored HTML to drift.</summary>
    public required string TextBody { get; set; }

    /// <summary>Optional call to action: the link label…</summary>
    public string? ActionText { get; set; }

    /// <summary>…and its portal-relative path, absolutized against
    /// branding.publicUrl when the message is sent, not when it is queued.</summary>
    public string? ActionPath { get; set; }

    /// <summary>Idempotency handle for messages that could be composed twice —
    /// the digest uses one per user per day. Unique where present.</summary>
    public string? DedupeKey { get; set; }

    /// <summary>For mail the reader opted into: the one-click unsubscribe path,
    /// absolutized at send time like the action. Null for transactional mail,
    /// which has no opt-out.</summary>
    public string? UnsubscribePath { get; set; }

    public EmailStatus Status { get; set; } = EmailStatus.Pending;

    /// <summary>How many times the worker has tried; drives the retry backoff.</summary>
    public int Attempts { get; set; }

    public DateTimeOffset? AttemptedAtUtc { get; set; }
    public DateTimeOffset? SentAtUtc { get; set; }

    /// <summary>The last SMTP error, for the operator.</summary>
    public string? LastError { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }
}
