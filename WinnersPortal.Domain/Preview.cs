namespace WinnersPortal.Domain;

/// <summary>
/// One running copy of an entrant's app on the build host, behind an address
/// only the people who may see it can open. A milestone's preview runs the
/// claimed commit; the final preview runs the <c>final</c> tag the deadline
/// freeze made.
///
/// The row's id is not its own: it is the checkpoint's id for a milestone and
/// the entry's id for the final. The build host names everything after the
/// first eight characters of that id (the compose project <c>cp&lt;id8&gt;</c>,
/// the address <c>p-&lt;id8&gt;.…</c>), so a milestone's preview starts from the
/// images its build already left there, and there is only ever one preview
/// of a thing.
/// </summary>
public sealed class Preview
{
    public const int MaxRef = 64;
    public const int MaxStopReason = 200;
    public const int MaxError = 400;

    /// <summary>The checkpoint's id (a milestone) or the entry's id (the final).</summary>
    public Guid Id { get; set; }

    public Guid EntryId { get; set; }
    public Entry? Entry { get; set; }

    /// <summary>The claimed milestone this runs; null for the final.</summary>
    public Guid? CheckpointId { get; set; }
    public Checkpoint? Checkpoint { get; set; }

    /// <summary>What the host checks out: the claim's commit, or <c>final</c>.</summary>
    public required string Ref { get; set; }

    /// <summary>The commit the host actually ran, as it reported it.</summary>
    public string? Sha { get; set; }

    public PreviewStatus Status { get; set; }

    /// <summary>Tries to hand the run to the host so far; the worker gives up after a few.</summary>
    public int Attempts { get; set; }

    /// <summary>When the worker next hands the run to the host; null once it has, or has given up.</summary>
    public DateTimeOffset? DueAtUtc { get; set; }

    /// <summary>Who pressed the button last.</summary>
    public Guid RequestedByUserId { get; set; }

    public DateTimeOffset RequestedAtUtc { get; set; }

    public DateTimeOffset? StartedAtUtc { get; set; }

    /// <summary>The last visit the host saw, as of the worker's last look.</summary>
    public DateTimeOffset? LastSeenAtUtc { get; set; }

    public DateTimeOffset? StoppedAtUtc { get; set; }

    /// <summary>Someone asked for it to stop; the worker tells the host.</summary>
    public bool StopRequested { get; set; }

    /// <summary>Why it stopped, in a line.</summary>
    public string? StopReason { get; set; }

    /// <summary>Why it did not start, in a line.</summary>
    public string? Error { get; set; }

    /// <summary>The last lines of the start log, for the dialog.</summary>
    public string? LogTail { get; set; }

    /// <summary>The entrant's <c>PREVIEW.md</c> at that commit — test logins and notes — as the host read it.</summary>
    public string? Notes { get; set; }
}

/// <summary>Where a preview stands.</summary>
public enum PreviewStatus
{
    None = 0,
    /// <summary>Asked for; waiting for the worker to hand it to the host.</summary>
    Pending = 1,
    /// <summary>Handed to the host, which is checking it out and starting the containers.</summary>
    Starting = 2,
    /// <summary>Up; its address opens it.</summary>
    Running = 3,
    /// <summary>Taken down — by a person, by the idle stop, or because the host lost it.</summary>
    Stopped = 4,
    /// <summary>Did not start.</summary>
    Failed = 5,
}
