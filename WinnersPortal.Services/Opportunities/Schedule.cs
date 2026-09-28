using WinnersPortal.Services.Ai;
using WinnersPortal.Domain;

namespace WinnersPortal.Services.Opportunities;

/// <summary>How one entrant stands against one dated milestone.</summary>
public enum MilestoneState
{
    /// <summary>Not claimed, and nothing says it is late yet.</summary>
    Open = 0,

    /// <summary>Not claimed and the date has passed. The only state nobody chooses.</summary>
    Overdue = 1,

    /// <summary>Claimed on or before the date.</summary>
    OnTime = 2,

    /// <summary>Claimed, but after the date.</summary>
    Late = 3,

    /// <summary>Claimed, and the client set no date for this one — done is all it can say.</summary>
    Claimed = 4,
}

/// <summary>
/// An opportunity's three dates and its milestone dates, as pure arithmetic.
///
/// The deadline was doing two jobs: it closed entry and it froze the board.
/// Splitting the first out as <see cref="Opportunity.EntryCloseUtc"/> lets a
/// client stop new entrants early — "join in the first week, build for
/// three" — without shortening the build. An opportunity that never sets one
/// behaves exactly as it always did, because null means "the deadline".
///
/// The start date is information, not a third gate. Entry opens the moment
/// an opportunity is published, whatever the start says — so a client can take
/// entrants, look them over and close entry with the last joining date
/// before the work begins. What <see cref="Opportunity.StartsAtUtc"/> decides is
/// the opportunity duration: the build runs from the start to the deadline, and
/// a blank deadline counts the default duration from it. It is a day, and
/// today — or a day gone by at publish — means "the moment it is published".
///
/// The milestone dates are the other half: with one, a claim can be told
/// on time from late, and an unclaimed milestone can be told open from
/// overdue. Nothing here picks a winner — the client does, and the board
/// is what they read while deciding.
/// </summary>
public static class Schedule
{
    /// <summary>A window shorter than this cannot be entered in; at publish it is a mistake.</summary>
    public const int MinEntryWindowHours = 1;

    /// <summary>The shortest opportunity duration — start to deadline — an opportunity may publish with.</summary>
    public const int MinBuildHours = 24;

    /// <summary>
    /// The start date is a day, not a moment: 00:00 UTC on it. Whatever time
    /// a caller sends is folded to that, so "today" means the same thing to
    /// the form, the gate and the page.
    /// </summary>
    public static DateTimeOffset? Day(DateTimeOffset? at) =>
        at is { } a ? new DateTimeOffset(a.UtcDateTime.Date, TimeSpan.Zero) : null;

    /// <summary>
    /// When the competition's work started, or will: the client's start day
    /// or the moment it was published, whichever is later — a start of
    /// "today" on an opportunity published at three in the afternoon started at
    /// three. A draft has only the day, if that.
    /// </summary>
    public static DateTimeOffset? StartsAt(DateTimeOffset? startsAtUtc, DateTimeOffset? publishedAtUtc) =>
        startsAtUtc is { } s && publishedAtUtc is { } p ? (s > p ? s : p) : startsAtUtc ?? publishedAtUtc;

    /// <summary>Where the opportunity duration counts from for an opportunity published now: its start day if that is still ahead, else now.</summary>
    public static DateTimeOffset Begins(DateTimeOffset? startsAtUtc, DateTimeOffset now) =>
        startsAtUtc is { } s && s > now ? s : now;

    /// <summary>
    /// The start day an opportunity publishes with: its own, unless that day went
    /// by while the draft sat — then the day it is published, since an opportunity
    /// cannot have started before it was posted. Not a refusal: the start
    /// gates nothing, so there is nothing to refuse.
    /// </summary>
    public static DateTimeOffset? StartAtPublish(DateTimeOffset? startsAtUtc, DateTimeOffset now) =>
        startsAtUtc is { } s && s < Day(now) ? Day(now) : startsAtUtc;

    /// <summary>When entry actually closes: the client's own date, or the deadline.</summary>
    public static DateTimeOffset? EntryClosesAt(DateTimeOffset? entryCloseUtc, DateTimeOffset? deadlineUtc) =>
        entryCloseUtc ?? deadlineUtc;

    /// <summary>
    /// Whether a new entrant may still join. The one gate the enter endpoint
    /// asks — and the start date is not part of it: entry is open from the
    /// moment of publishing to the last joining date.
    /// </summary>
    public static bool EntryOpen(
        OpportunityStatus status, DateTimeOffset? entryCloseUtc, DateTimeOffset? deadlineUtc, DateTimeOffset now)
    {
        if (status != OpportunityStatus.Open) return false;
        var closes = EntryClosesAt(entryCloseUtc, deadlineUtc);
        return closes is null || closes > now;
    }

    /// <summary>
    /// What a draft may hold. Deliberately lenient about the past — a draft
    /// is a scratchpad, and a date that has gone stale while it sat there is
    /// the publish gate's business, not the save button's. Impossible orders
    /// are refused here, though: a start at or after the deadline, entry
    /// closing after it, a milestone due after it. The start orders nothing
    /// else — the joining date and the milestones may fall either side of it.
    /// </summary>
    public static string? DraftProblem(
        DateTimeOffset? startsAtUtc,
        DateTimeOffset? entryCloseUtc,
        DateTimeOffset? deadlineUtc,
        IEnumerable<DateTimeOffset?> milestoneDues)
    {
        if (startsAtUtc is not null && deadlineUtc is not null && startsAtUtc >= deadlineUtc)
            return "The competition cannot start at or after the deadline — there would be no time to build.";

        if (entryCloseUtc is not null && deadlineUtc is not null && entryCloseUtc > deadlineUtc)
            return "Entry cannot close after the deadline — nobody could enter in the time left.";

        if (deadlineUtc is not null)
        {
            var i = 0;
            foreach (var due in milestoneDues)
            {
                i++;
                if (due is not null && due > deadlineUtc)
                    return $"Milestone {i} is due after the deadline. Move the date, or move the deadline.";
            }
        }
        return null;
    }

    /// <summary>
    /// What publishing may hold. The one-way door: after this, entrants have
    /// committed time against these dates and none of them move. The
    /// opportunity duration counts from the start day where that is still ahead
    /// and from publishing otherwise; the time to join always counts from
    /// publishing, because that is when entry opens.
    /// </summary>
    public static string? PublishProblem(
        DateTimeOffset? startsAtUtc,
        DateTimeOffset? entryCloseUtc,
        DateTimeOffset deadlineUtc,
        IEnumerable<DateTimeOffset?> milestoneDues,
        DateTimeOffset now)
    {
        var draft = DraftProblem(startsAtUtc, entryCloseUtc, deadlineUtc, milestoneDues);
        if (draft is not null) return draft;

        if (deadlineUtc < Begins(startsAtUtc, now).AddHours(MinBuildHours))
            return startsAtUtc is not null && startsAtUtc > now
                ? $"The deadline must be at least {MinBuildHours} hours after the competition starts — entrants need time to build."
                : $"The deadline must be at least {MinBuildHours} hours away — entrants need time to build.";

        if (entryCloseUtc is not null && entryCloseUtc < now.AddHours(MinEntryWindowHours))
            return $"Entry would close within {MinEntryWindowHours} hour of publishing — nobody could join. "
                + "Push the last joining date out, or leave it blank to keep entry open to the deadline.";

        var i = 0;
        foreach (var due in milestoneDues)
        {
            i++;
            if (due is not null && due <= now)
                return $"Milestone {i} is due in the past. Entrants cannot hit a date that has already gone.";
        }
        return null;
    }

    /// <summary>
    /// One cell of the board. <paramref name="claimedAtUtc"/> is the webhook's
    /// stamp; on or before the date counts as on time, because a date is a
    /// day people work towards, not a race they must beat.
    /// </summary>
    public static MilestoneState StateOf(
        DateTimeOffset? dueUtc, DateTimeOffset? claimedAtUtc, DateTimeOffset now)
    {
        if (claimedAtUtc is not null)
        {
            if (dueUtc is null) return MilestoneState.Claimed;
            return claimedAtUtc <= dueUtc ? MilestoneState.OnTime : MilestoneState.Late;
        }
        return dueUtc is not null && dueUtc < now ? MilestoneState.Overdue : MilestoneState.Open;
    }

    public static string Name(MilestoneState state) => state switch
    {
        MilestoneState.Overdue => "overdue",
        MilestoneState.OnTime => "on_time",
        MilestoneState.Late => "late",
        MilestoneState.Claimed => "claimed",
        _ => "open",
    };

    /// <summary>
    /// One entrant's row summarised. <paramref name="Dated"/> is how many of
    /// the opportunity's milestones carry a date at all — without it "3 on time"
    /// out of what is unanswerable.
    /// </summary>
    public sealed record Standing(int Done, int OnTime, int Late, int Overdue, int Dated);

    public static Standing Stand(IEnumerable<MilestoneState> states)
    {
        int done = 0, onTime = 0, late = 0, overdue = 0, dated = 0;
        foreach (var s in states)
        {
            if (s is MilestoneState.OnTime or MilestoneState.Late or MilestoneState.Claimed) done++;
            if (s is MilestoneState.OnTime) { onTime++; dated++; }
            if (s is MilestoneState.Late) { late++; dated++; }
            if (s is MilestoneState.Overdue) { overdue++; dated++; }
        }
        return new Standing(done, onTime, late, overdue, dated);
    }

    /// <summary>
    /// What sorts the board. A milestone met on time is worth two, one met
    /// late is worth one, and nothing else scores — so keeping to the dates
    /// outranks finishing the same work behind them, which is the whole
    /// point of putting dates on the checklist. An opportunity with no dates
    /// anywhere scores every row at zero and the board keeps join order,
    /// exactly as it read before any of this existed.
    /// </summary>
    public static int Score(Standing s) => s.OnTime * 2 + s.Late;
}
