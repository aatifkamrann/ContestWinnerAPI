namespace WinnersPortal.Domain;

public enum EntryStatus
{
    Active = 0,

    /// <summary>The entrant's own decision to stop.</summary>
    Withdrawn = 1,

    /// <summary>
    /// The client's (or an administrator's) decision that this entry does not
    /// belong in the opportunity. Kept apart from <see cref="Withdrawn"/> because
    /// the record has to stay honest about whose decision it was: leaving is
    /// not the same as being asked to leave, and the entrant is told which.
    /// </summary>
    Removed = 2,

    /// <summary>
    /// The client's (or an administrator's) selection taken back before any
    /// work arrived: the entry steps aside, its repository is archived, and
    /// it leaves the entrant's own list — the application, now not selected,
    /// is what they read. Neither withdrawn (not their decision) nor removed
    /// (no reason owed, and they may be selected again).
    /// </summary>
    Deselected = 3,
}

public enum RepoProvisionStatus
{
    /// <summary>Waiting for the worker (or for GitHub to be configured at all).</summary>
    Pending = 0,

    /// <summary>Repo exists, seeded, and the entrant is invited.</summary>
    Provisioned = 1,

    /// <summary>Gave up after repeated attempts; <see cref="Entry.ProvisionNote"/> says why.</summary>
    Failed = 2,
}

/// <summary>
/// A freelancer's entry into an opportunity. One active entry per freelancer per
/// opportunity, enforced by a partial unique index at the database — withdrawing
/// frees the slot, so re-entering is a new row and the history stays honest.
///
/// The repo fields below are the blueprint's Repo entity folded in: the
/// relationship is strictly one-to-one and lives and dies with the entry.
/// </summary>
public sealed class Entry
{
    // Column limits: the one place a length is stated; the rules and the
    // DbContext both read it here.
    public const int MaxReason = 500;

    public Guid Id { get; set; }

    public Guid OpportunityId { get; set; }
    public Opportunity? Opportunity { get; set; }

    public Guid FreelancerId { get; set; }
    public User? Freelancer { get; set; }

    /// <summary>Where the private-repo invitation goes.</summary>
    public required string GithubUsername { get; set; }

    /// <summary>Optional public one-liner shown in the entrant list.</summary>
    public string? Note { get; set; }

    public EntryStatus Status { get; set; } = EntryStatus.Active;

    // ---- the org-owned private repo -----------------------------------

    /// <summary>owner/name once provisioned; null before and when GitHub is unconfigured.</summary>
    public string? RepoFullName { get; set; }

    /// <summary>GitHub's numeric repository id — stable across renames, unlike the name.</summary>
    public long? RepoId { get; set; }

    public string? DefaultBranch { get; set; }

    public RepoProvisionStatus ProvisionStatus { get; set; } = RepoProvisionStatus.Pending;

    /// <summary>How many times the worker has tried; drives the retry backoff.</summary>
    public int ProvisionAttempts { get; set; }

    public DateTimeOffset? ProvisionAttemptedAtUtc { get; set; }

    /// <summary>The last provisioning error, for the operator — never shown to entrants raw.</summary>
    public string? ProvisionNote { get; set; }

    // ---- webhook-observed activity ------------------------------------

    public DateTimeOffset? LastPushAtUtc { get; set; }
    public int PushCount { get; set; }

    // ---- what the repository's file listing shows -----------------------
    // Read by the worker after each push, never in a page request: the
    // standing score wants to know whether tests, a README and CI exist,
    // and asking GitHub for thirty trees while a board renders is the
    // shape the performance rules forbid. Null until the first read.

    public int? TreeFileCount { get; set; }
    public bool? TreeHasTests { get; set; }
    public bool? TreeHasReadme { get; set; }
    public bool? TreeHasCi { get; set; }
    /// <summary>A root-level compose file — what an opportunity that requires Docker Compose builds from.</summary>
    public bool? TreeHasCompose { get; set; }

    /// <summary>When the listing was last read; older than the last push means stale.</summary>
    public DateTimeOffset? TreeReadAtUtc { get; set; }

    // ---- lifecycle stamps written by the worker -----------------------

    /// <summary>Deadline freeze: final tag created and push access revoked.</summary>
    public DateTimeOffset? FrozenAtUtc { get; set; }

    /// <summary>The client's connected account was granted read access for review.</summary>
    public DateTimeOffset? ReviewAccessGrantedAtUtc { get; set; }

    /// <summary>Losing and withdrawn repos archive; losing work never changes hands.</summary>
    public DateTimeOffset? ArchivedAtUtc { get; set; }

    // ---- the review-fallback ZIP (storage-backed, cut on first request) --

    /// <summary>Object key of the packaged final tag; null until a client first asks for it.</summary>
    public string? ZipStorageKey { get; set; }

    /// <summary>The storage setup the package went to; null on one packaged before there was a choice — the main one.</summary>
    public string? ZipStorageSetup { get; set; }

    public long? ZipSizeBytes { get; set; }

    /// <summary>A frozen repo never changes, so the ZIP is packaged once and cached forever.</summary>
    public DateTimeOffset? ZipPackagedAtUtc { get; set; }

    // ---- removal by the client (or an administrator) -------------------
    // The counterpart of a withdrawal, and deliberately noisier: somebody
    // staked real work on this opportunity and is being told it does not count.
    // The reason is required, is sent to them word for word, and is the only
    // account they will ever get, so it is stored rather than just mailed.

    public DateTimeOffset? RemovedAtUtc { get; set; }

    /// <summary>Why. Sent to the entrant verbatim; never edited afterwards.</summary>
    public string? RemovedReason { get; set; }

    /// <summary>The client, or the administrator who acted over them.</summary>
    public Guid? RemovedByUserId { get; set; }

    /// <summary>
    /// When the entrant's read access to their own repository ends. Removal
    /// archives the repo at once, which stops every push; this is the window
    /// that follows, so a person told to leave can still clone what they
    /// built. Null on every entry that was never removed.
    /// </summary>
    public DateTimeOffset? RepoAccessEndsAtUtc { get; set; }

    /// <summary>Set by the worker once the collaborator is actually off the repository.</summary>
    public DateTimeOffset? AccessRevokedAtUtc { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? WithdrawnAtUtc { get; set; }

    /// <summary>
    /// Why they withdrew, in their own words, if they said. Optional, up to
    /// a removal reason's length; the opportunity's client reads it under the
    /// Withdrawn application. Cleared when the account is erased.
    /// </summary>
    public string? WithdrawnReason { get; set; }

    public List<Checkpoint> Checkpoints { get; set; } = [];

    /// <summary>Files handed in on an opportunity that takes uploads. Confirmed ones only are ever shown.</summary>
    public List<Submission> Submissions { get; set; } = [];
}
