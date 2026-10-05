using WinnersPortal.Domain;

namespace WinnersPortal.Services.Preview;

/// <summary>
/// The rules of a preview that need nothing but their arguments: who may run
/// one, when the final may run, what the states are called on the wire, and
/// the sentences a person reads when a preview stops or does not start. The
/// retry arithmetic is a build's (<see cref="CheckpointBuilds"/>).
/// </summary>
public static class Previews
{
    /// <summary>The most of an entrant's <c>PREVIEW.md</c> the portal keeps and shows.</summary>
    public const int MaxNotes = 8_192;

    /// <summary>
    /// Who may start, stop and open a preview: the entrant (their own work),
    /// the opportunity's client, and administrators — the build log's readers.
    /// A client sees the product before the deadline, never the code.
    /// </summary>
    public static bool CanRun(bool isEntrant, bool isOwner, bool isAdmin) =>
        CheckpointBuilds.CanSee(isEntrant, isOwner, isAdmin);

    /// <summary>
    /// Why the final version cannot be run, or null. It is the <c>final</c>
    /// tag the deadline freeze made, so there is none before the freeze, and
    /// only an opportunity that asked entries to run with Docker Compose runs one.
    /// </summary>
    public static string? FinalProblem(OpportunityStatus status, bool requiresCompose, bool frozen, EntryStatus entry)
    {
        if (!requiresCompose) return "This opportunity did not ask entries to run with Docker Compose.";
        if (entry != EntryStatus.Active) return "This entry is no longer in the opportunity.";
        if (status is not (OpportunityStatus.Reviewing or OpportunityStatus.Awarded) || !frozen)
            return "The final version runs once the deadline has passed and the entry is frozen.";
        return null;
    }

    /// <summary>Why an opportunity cannot ask for Docker Compose now: a requirement nothing checks is one nobody should enter on.</summary>
    public const string ComposeUnavailable =
        "Docker Compose builds are not available on this portal right now — an administrator has to switch on a "
        + "build host and pass its test first (Settings → Build host).";

    public const string PublishWithoutBuilds =
        "This draft asks entries to run with Docker Compose, and builds are not available on this portal right now. "
        + "Open it in the editor and save it again, which drops the requirement, or ask an administrator to switch on "
        + "and test the build host.";

    public const string PreviewsOff =
        "Previews are off until an administrator switches on the build host and it passes its test.";

    public const string NoAddress =
        "Previews are not set up on this portal yet — an administrator sets a preview address under Settings → Build host.";

    public const string BuildsFirst = "This milestone has to build before it can run.";

    public const string NotRunning = "This preview is not running — start it first.";

    public static string MaxRunningText(int max) =>
        $"{max} preview{(max == 1 ? " is" : "s are")} already running on the build host — stop one, or wait for one to stop itself.";

    /// <summary>The wire name the page reads.</summary>
    public static string StatusName(PreviewStatus s) => s switch
    {
        PreviewStatus.Pending => "pending",
        PreviewStatus.Starting => "starting",
        PreviewStatus.Running => "running",
        PreviewStatus.Stopped => "stopped",
        PreviewStatus.Failed => "failed",
        _ => "none",
    };

    /// <summary>Why a preview came down, as the agent put it, in words.</summary>
    public static string StopReasonText(string? agentReason, int idleMinutes) => agentReason switch
    {
        "idle" => $"Stopped after {idleMinutes} minutes without a visit.",
        "requested" => "Stopped.",
        "restart" => "Stopped when the build host restarted.",
        _ => "Stopped.",
    };

    public static string Lost() => "The build host no longer had this preview. Start it again.";

    public static string StartTimedOut(TimeSpan timeout) =>
        $"It did not start within {Math.Round((timeout + CheckpointBuilds.Grace).TotalMinutes)} minutes, so the build host was told to take it down.";

    public static string Unreachable(string why) =>
        CheckpointBuilds.Cut($"The build host could not be reached to start this preview ({why}).");
}
