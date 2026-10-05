namespace WinnersPortal.Domain;

/// <summary>
/// One entry's progress against one milestone — claimed from inside the repo
/// (a pushed tag <c>m1</c>, <c>m2</c>… or a pull request), stamped by the
/// webhook with when and by which commit. The opportunity page pivots these rows
/// into the board: one row per entrant, one column per milestone.
/// </summary>
public sealed class Checkpoint
{
    /// <summary>The longest request for changes a client may write.</summary>
    public const int MaxChangesNote = 1000;

    public Guid Id { get; set; }

    public Guid EntryId { get; set; }
    public Entry? Entry { get; set; }

    public Guid MilestoneId { get; set; }
    public Milestone? Milestone { get; set; }

    /// <summary>Head commit of the claim, when the delivery carried one.</summary>
    public string? CommitSha { get; set; }

    /// <summary>"tag" or "pull_request" — how the milestone was claimed.</summary>
    public required string Via { get; set; }

    /// <summary>The tag or branch that carried the claim, e.g. <c>m2</c>.</summary>
    public required string Ref { get; set; }

    public DateTimeOffset ClaimedAtUtc { get; set; }

    // ---- review and payment, paid by milestone ---------------------------
    // Only on an opportunity paid by milestone: the claim is the hired
    // freelancer handing the milestone in, the client approves it (or asks
    // for changes), pays it off-portal and marks it paid — and the next
    // milestone opens only then. Null on a competitive opportunity.

    /// <summary>When the client asked for changes; cleared when the freelancer hands the milestone in again.</summary>
    public DateTimeOffset? ChangesRequestedAtUtc { get; set; }

    /// <summary>What the client asked to change, in their words; kept after a resubmission as the last request.</summary>
    public string? ChangesNote { get; set; }

    /// <summary>When the client approved the milestone as done.</summary>
    public DateTimeOffset? ApprovedAtUtc { get; set; }

    /// <summary>When the client confirmed paying this milestone's amount. Opens the next milestone.</summary>
    public DateTimeOffset? PaidAtUtc { get; set; }

    // ---- the build of the claimed commit --------------------------------
    // Only on an opportunity that requires Docker Compose, and only for a claim
    // that carried a commit: the webhook queues it (Pending, due now), the
    // preview worker has the build host clone that commit and build the
    // compose file, and the outcome lands here for the board to show.

    /// <summary>None where the opportunity asks for no build; otherwise where the build stands.</summary>
    public PreviewBuildStatus BuildStatus { get; set; }

    /// <summary>Tries to hand the build to the host so far; the worker gives up after a few.</summary>
    public int BuildAttempts { get; set; }

    /// <summary>When the worker next hands the build to the host; null once it has, or has given up.</summary>
    public DateTimeOffset? BuildDueAtUtc { get; set; }

    public DateTimeOffset? BuildStartedAtUtc { get; set; }

    public DateTimeOffset? BuildFinishedAtUtc { get; set; }

    /// <summary>Why the build failed, in a line — the host's verdict, or the worker's when the host could not be reached.</summary>
    public string? BuildError { get; set; }

    /// <summary>The last lines of the build log, for the board's dialog; the whole log is in storage.</summary>
    public string? BuildLogTail { get; set; }

    /// <summary>Object key of the full build log, once stored; null when the portal has no storage.</summary>
    public string? BuildLogKey { get; set; }

    /// <summary>The storage setup the log was written to, so a later switch of store does not strand it.</summary>
    public string? BuildLogSetup { get; set; }
}

/// <summary>Where a claimed milestone's build stands.</summary>
public enum PreviewBuildStatus
{
    /// <summary>No build asked for: the opportunity does not require one, or the claim carried no commit.</summary>
    None = 0,
    /// <summary>Queued for the build host.</summary>
    Pending = 1,
    /// <summary>Handed to the host, not finished.</summary>
    Building = 2,
    Built = 3,
    Failed = 4,
}
