namespace WinnersPortal.Domain;

/// <summary>
/// Where one member stands with the identity provider: the session the
/// portal opened for them, the verdict that came back, and the proof
/// behind it — the provider's whole decision (the checks, the name, number
/// and dates read off the document) and, as <see cref="IdentityDocument"/>
/// rows, copies of the document and selfie images in the portal's own file
/// storage. Administrators read the proof; the member never sees it here.
/// One row per member; a reset deletes it, with its images, and the next
/// start makes a fresh one.
/// </summary>
public sealed class IdentityVerification
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }
    public User? User { get; set; }

    /// <summary>Which provider ran it — "didit" — so a switch of provider does not mistake one's session ids for another's.</summary>
    public required string Provider { get; set; }

    /// <summary>The provider's session id; what its webhooks and its decision endpoint are keyed by.</summary>
    public required string SessionId { get; set; }

    public IdentityStatus Status { get; set; }

    /// <summary>The provider's own event id of the last webhook applied, so a redelivery is a no-op.</summary>
    public string? LastEventId { get; set; }

    /// <summary>The provider's reason category on a decline — "document expired" — never a name or a number off the document.</summary>
    public string? Note { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }

    /// <summary>When a final verdict landed; null while the member is still in the flow.</summary>
    public DateTimeOffset? DecidedAtUtc { get; set; }

    /// <summary>
    /// The provider's decision exactly as it answered: every check, what it
    /// read off the document, and its own links to the images (which it
    /// expires — the copies are <see cref="IdentityDocument"/> rows). Kept
    /// as proof, replaced by the next decision, cleared when the account is
    /// erased.
    /// </summary>
    public string? DecisionJson { get; set; }

    /// <summary>When <see cref="DecisionJson"/> was read from the provider.</summary>
    public DateTimeOffset? DecisionReadAtUtc { get; set; }

    /// <summary>
    /// When the proof worker should next copy the decision and its images:
    /// set to now when a verdict lands, pushed back after a failed try, and
    /// null once the copy is done or the worker has given up.
    /// </summary>
    public DateTimeOffset? ProofDueAtUtc { get; set; }

    /// <summary>How many times the proof worker has tried this decision; it backs off, then gives up.</summary>
    public int ProofAttempts { get; set; }

    /// <summary>Why the last copy fell short — storage not configured, the provider refused — for the administrator.</summary>
    public string? ProofError { get; set; }
}

/// <summary>The provider's statuses, folded to what the portal acts on.</summary>
public enum IdentityStatus
{
    /// <summary>A session exists; the member has not finished it.</summary>
    NotStarted = 0,
    InProgress = 1,
    /// <summary>The provider's reviewer is looking; neither passed nor failed yet.</summary>
    InReview = 2,
    Approved = 3,
    Declined = 4,
    /// <summary>The member dropped out; they may start again.</summary>
    Abandoned = 5,
    /// <summary>The session, or an old approval, timed out; they may start again.</summary>
    Expired = 6,
}
