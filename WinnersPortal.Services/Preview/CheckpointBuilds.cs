using WinnersPortal.Domain;

namespace WinnersPortal.Services.Preview;

/// <summary>
/// The pure rules of a claimed milestone's build: how often the worker
/// tries to hand one to the host, when a build it is waiting on is given
/// up as lost, what of the log the row keeps, where the whole log goes,
/// and who may read it. The worker and the board read these; nothing
/// here touches a database.
/// </summary>
public static class CheckpointBuilds
{
    /// <summary>Tries to hand a build to the host before the worker gives up on it.</summary>
    public const int MaxAttempts = 6;

    /// <summary>Lines of the log the row keeps for the board's dialog.</summary>
    public const int TailLines = 60;

    /// <summary>And no more than this many characters of them.</summary>
    public const int TailChars = 8_192;

    /// <summary>How long past its own timeout a build the host still calls running is waited on before it is given up.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(5);

    /// <summary>2, 4, 8 … minutes after each failed hand-over, never more than an hour.</summary>
    public static TimeSpan Backoff(int attempts) => TimeSpan.FromMinutes(Math.Min(Math.Pow(2, Math.Max(attempts, 1)), 60));

    /// <summary>Whether a build the host still calls running has outlived its timeout and the grace on top.</summary>
    public static bool TimedOut(DateTimeOffset? startedAtUtc, TimeSpan timeout, DateTimeOffset now) =>
        startedAtUtc is { } started && now - started > timeout + Grace;

    /// <summary>The object key of a build's whole log, under the checkpoint's id.</summary>
    public static string LogKey(Guid checkpointId) => $"builds/{checkpointId:N}.log";

    /// <summary>What a downloaded log is called: the milestone and the commit it built.</summary>
    public static string LogFileName(int milestoneNumber, string? commitSha) =>
        $"build-m{milestoneNumber}-{(commitSha is { Length: >= 7 } sha ? sha[..7] : "commit")}.log";

    /// <summary>
    /// The last lines of a log, for the row: the last <see cref="TailLines"/>,
    /// and no more than <see cref="TailChars"/> characters of those. Null for
    /// a log with nothing in it.
    /// </summary>
    public static string? Tail(string? log)
    {
        if (string.IsNullOrWhiteSpace(log)) return null;
        var lines = log.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        var kept = lines.Length <= TailLines ? lines : lines[^TailLines..];
        var text = string.Join('\n', kept);
        return text.Length <= TailChars ? text : "…" + text[^(TailChars - 1)..];
    }

    /// <summary>
    /// Who may read a build and its log: the entrant, whose work it is;
    /// the opportunity's client, who the build is proof for; an administrator.
    /// Other entrants see the mark on the board and nothing more.
    /// </summary>
    public static bool CanSee(bool isEntrant, bool isOwner, bool isAdmin) => isEntrant || isOwner || isAdmin;

    /// <summary>The wire name of a build's state, for the board and the dialog.</summary>
    public static string StatusName(PreviewBuildStatus status) => status switch
    {
        PreviewBuildStatus.Pending => "pending",
        PreviewBuildStatus.Building => "building",
        PreviewBuildStatus.Built => "built",
        PreviewBuildStatus.Failed => "failed",
        _ => "none",
    };

    // ---------------------------------------------------------- the words

    /// <summary>What the row says when the host never took the build.</summary>
    public static string Unreachable(string why) =>
        Cut($"The build host could not be reached after {MaxAttempts} tries — {why}");

    /// <summary>What the row says when the host lost the build: restarted, or forgot it.</summary>
    public static string Lost() => "The build host lost this build — it was handed over, but the host no longer knows it.";

    /// <summary>What the row says when a build ran past its limit.</summary>
    public static string TimedOutError(TimeSpan timeout) =>
        $"The build ran longer than {timeout.TotalMinutes:0} minutes and was stopped.";

    /// <summary>The host's reason for a failed build, in a line, or the exit code when that is all it gave.</summary>
    public static string Verdict(PreviewHost.BuildAnswer answer) => Cut(
        !string.IsNullOrWhiteSpace(answer.Error) ? answer.Error
        : answer.ExitCode is { } code ? $"docker compose build exited with {code}."
        : "The build host reported a failure without saying why.");

    /// <summary>The 400-character column.</summary>
    public static string Cut(string s) => s.Length <= 400 ? s : s[..397] + "…";
}
