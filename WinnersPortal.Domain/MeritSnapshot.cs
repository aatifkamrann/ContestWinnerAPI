namespace WinnersPortal.Domain;

/// <summary>
/// A freelancer's merit score as it stood on one day — the only place the
/// score is ever written down. Merit is arithmetic over rows and is read
/// fresh everywhere it shows, which is right for a number that must never
/// drift from the facts under it; but a trend needs a yesterday to compare
/// with, and the facts do not keep one (a rating edited or an entry
/// withdrawn changes what the score <em>was</em>). So the snapshot worker
/// writes one row per freelancer per UTC day, and the leaderboard's arrow
/// is today's live score against the oldest of those in its window.
///
/// Keyed by (UserId, DayUtc): a day is recorded once, and the worker writes
/// the day's batch only when no row for the day exists yet. Rows older than
/// the retention are swept by the same worker.
/// </summary>
public sealed class MeritSnapshot
{
    public Guid UserId { get; set; }

    /// <summary>The UTC calendar day the score was read on.</summary>
    public DateOnly DayUtc { get; set; }

    /// <summary>The score out of <see cref="Profiles.Merit.Max"/> at that read.</summary>
    public int Score { get; set; }
}
