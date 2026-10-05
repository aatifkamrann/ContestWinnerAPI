using WinnersPortal.Services.Opportunities;
using WinnersPortal.Domain;
using Xunit;

namespace WinnersPortal.Api.Tests;

/// <summary>
/// The two opportunity dates and the milestone dates. Every rule here decides
/// either who may still join or how a board row reads, so each one is worth
/// pinning: an opportunity that sets no last joining date and no milestone dates
/// must behave exactly as it did before either existed.
/// </summary>
public class ScheduleTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-04T12:00:00Z");
    private static DateTimeOffset At(double hours) => Now.AddHours(hours);

    [Fact]
    public void No_last_joining_date_means_the_deadline()
    {
        var deadline = At(240);

        Assert.Equal(deadline, Schedule.EntryClosesAt(null, deadline));
        Assert.Equal(At(48), Schedule.EntryClosesAt(At(48), deadline));
        // An opportunity with neither is a draft; nothing closes, and nothing crashes.
        Assert.Null(Schedule.EntryClosesAt(null, null));
    }

    [Fact]
    public void The_start_date_is_a_day_in_utc()
    {
        Assert.Equal(DateTimeOffset.Parse("2026-09-04T00:00:00Z"), Schedule.Day(DateTimeOffset.Parse("2026-09-04T17:30:00Z")));
        // East of Greenwich an early hour is still the day before in UTC.
        Assert.Equal(DateTimeOffset.Parse("2026-09-04T00:00:00Z"), Schedule.Day(DateTimeOffset.Parse("2026-09-05T01:00:00+05:00")));
        Assert.Null(Schedule.Day(null));
    }

    [Fact]
    public void A_competition_starts_on_its_day_or_when_published_whichever_is_later()
    {
        Assert.Equal(At(-10), Schedule.StartsAt(null, At(-10)));
        Assert.Equal(At(48), Schedule.StartsAt(At(48), At(-10)));
        // A start of today on an opportunity published at ten: it started at ten.
        Assert.Equal(At(-2), Schedule.StartsAt(At(-12), At(-2)));
        // A draft has only the day, if that.
        Assert.Equal(At(48), Schedule.StartsAt(At(48), null));
        Assert.Null(Schedule.StartsAt(null, null));

        // Publishing now: the duration counts from the day where it is ahead, else now.
        Assert.Equal(Now, Schedule.Begins(null, Now));
        Assert.Equal(Now, Schedule.Begins(At(-12), Now));
        Assert.Equal(At(48), Schedule.Begins(At(48), Now));

        // A day gone by while the draft sat publishes as today; today and later stay.
        Assert.Equal(Schedule.Day(Now), Schedule.StartAtPublish(At(-36), Now));
        Assert.Equal(At(-12), Schedule.StartAtPublish(At(-12), Now));
        Assert.Equal(At(48), Schedule.StartAtPublish(At(48), Now));
        Assert.Null(Schedule.StartAtPublish(null, Now));
    }

    [Theory]
    // Open, entry closes later: the ordinary case.
    [InlineData(OpportunityStatus.Open, 48d, 240d, true)]
    // Open, but the last joining date has gone — the build runs on without new entrants.
    [InlineData(OpportunityStatus.Open, -1d, 240d, false)]
    // Open with no early close: the deadline is the gate, exactly as before.
    [InlineData(OpportunityStatus.Open, null, 240d, true)]
    [InlineData(OpportunityStatus.Open, null, -1d, false)]
    // Every other status is shut regardless of the dates.
    [InlineData(OpportunityStatus.Draft, 48d, 240d, false)]
    [InlineData(OpportunityStatus.Reviewing, 48d, 240d, false)]
    [InlineData(OpportunityStatus.Awarded, 48d, 240d, false)]
    [InlineData(OpportunityStatus.Cancelled, 48d, 240d, false)]
    public void Entry_is_open_only_while_a_live_opportunity_still_takes_joiners(
        OpportunityStatus status, double? closeHours, double deadlineHours, bool expected) =>
        Assert.Equal(expected, Schedule.EntryOpen(
            status,
            closeHours is null ? null : At(closeHours.Value),
            At(deadlineHours),
            Now));

    [Fact]
    public void A_draft_may_hold_stale_dates_but_not_impossible_ones()
    {
        // A scratchpad: dates in the past save fine, because the publish gate
        // is where they have to be real.
        Assert.Null(Schedule.DraftProblem(At(-200), At(-100), At(-50), [At(-60)]));
        Assert.Null(Schedule.DraftProblem(null, null, null, [null, null]));

        // Entry closing after the deadline is not stale, it is impossible.
        Assert.Contains("after the deadline",
            Schedule.DraftProblem(null, At(300), At(240), [])!);
        // …and so is a milestone due after the whole opportunity ends.
        Assert.Contains("Milestone 2",
            Schedule.DraftProblem(null, null, At(240), [At(100), At(300)])!);
    }

    [Fact]
    public void The_start_date_orders_nothing_but_the_duration()
    {
        // Starting at or after the deadline leaves no opportunity at all.
        Assert.Contains("no time to build", Schedule.DraftProblem(At(240), null, At(240), [])!);
        // Entry may close before the work starts: take entrants, settle the field, then build.
        Assert.Null(Schedule.DraftProblem(At(48), At(24), At(240), []));
        // A milestone may fall due before the start, too.
        Assert.Null(Schedule.DraftProblem(At(48), null, At(240), [At(40), At(100)]));
        // And at publish the time to join counts from publishing, not from the start.
        Assert.Null(Schedule.PublishProblem(At(48), At(2), At(240), [], Now));
        Assert.Contains("of publishing", Schedule.PublishProblem(At(48), At(0.5), At(240), [], Now)!);
    }

    [Fact]
    public void Publishing_refuses_a_window_nobody_could_enter_in()
    {
        var deadline = At(240);

        Assert.Null(Schedule.PublishProblem(null, At(48), deadline, [At(100)], Now));
        Assert.Null(Schedule.PublishProblem(null, null, deadline, [], Now));

        // Closing entry within the hour makes the opportunity unenterable.
        Assert.Contains("nobody could join",
            Schedule.PublishProblem(null, At(0.5), deadline, [], Now)!);
        Assert.Contains("nobody could join",
            Schedule.PublishProblem(null, At(-5), deadline, [], Now)!);

        // A milestone already due at publish is a date entrants cannot hit.
        Assert.Contains("due in the past",
            Schedule.PublishProblem(null, null, deadline, [At(100), At(-1)], Now)!);

        // The shortest opportunity duration, counted from publishing where there is no start.
        Assert.Contains("24 hours away", Schedule.PublishProblem(null, null, At(10), [], Now)!);
    }

    [Fact]
    public void Publishing_counts_the_opportunity_duration_from_the_start_day()
    {
        var deadline = At(240);
        Assert.Null(Schedule.PublishProblem(At(48), At(72), deadline, [At(100)], Now));

        // Today — 00:00 UTC, twelve hours before now — means now.
        Assert.Null(Schedule.PublishProblem(At(-12), null, deadline, [], Now));

        // A later start: twenty hours from it to the deadline is too short,
        // however far that deadline is from the moment of publishing.
        Assert.Contains("24 hours after the competition starts",
            Schedule.PublishProblem(At(220), null, deadline, [], Now)!);
        // A start of today counts the duration from publishing instead.
        Assert.Contains("24 hours away", Schedule.PublishProblem(At(-12), null, At(20), [], Now)!);
    }

    [Theory]
    // Nothing claimed, no date: the milestone is simply open.
    [InlineData(null, null, MilestoneState.Open)]
    // Nothing claimed, date still ahead — open. Date gone — overdue.
    [InlineData(24d, null, MilestoneState.Open)]
    [InlineData(-24d, null, MilestoneState.Overdue)]
    // Claimed with no date to judge it against: done is all it can say.
    [InlineData(null, -5d, MilestoneState.Claimed)]
    // Claimed before the date, and exactly on it — both count as on time.
    [InlineData(24d, 20d, MilestoneState.OnTime)]
    [InlineData(24d, 24d, MilestoneState.OnTime)]
    // Claimed after it.
    [InlineData(24d, 25d, MilestoneState.Late)]
    public void A_cell_reads_from_its_date_and_its_claim(
        double? dueHours, double? claimedHours, MilestoneState expected) =>
        Assert.Equal(expected, Schedule.StateOf(
            dueHours is null ? null : At(dueHours.Value),
            claimedHours is null ? null : At(claimedHours.Value),
            Now));

    [Fact]
    public void A_row_summarises_to_what_the_client_reads_while_deciding()
    {
        var standing = Schedule.Stand([
            MilestoneState.OnTime, MilestoneState.OnTime,
            MilestoneState.Late,
            MilestoneState.Overdue,
            MilestoneState.Claimed, // done, but its milestone carries no date
            MilestoneState.Open,
        ]);

        Assert.Equal(4, standing.Done); // two on time, one late, one dateless claim
        Assert.Equal(2, standing.OnTime);
        Assert.Equal(1, standing.Late);
        Assert.Equal(1, standing.Overdue);
        Assert.Equal(4, standing.Dated); // the two Open/Claimed rows carry no date
    }

    [Fact]
    public void Keeping_to_the_dates_outranks_finishing_the_same_work_behind_them()
    {
        var punctual = Schedule.Stand([MilestoneState.OnTime, MilestoneState.OnTime]);
        var behind = Schedule.Stand([MilestoneState.Late, MilestoneState.Late]);
        var further = Schedule.Stand([MilestoneState.Late, MilestoneState.Overdue]);

        Assert.True(Schedule.Score(punctual) > Schedule.Score(behind));
        Assert.True(Schedule.Score(behind) > Schedule.Score(further));

        // One on time beats two late — that is the whole point of the dates.
        Assert.True(Schedule.Score(Schedule.Stand([MilestoneState.OnTime]))
            > Schedule.Score(Schedule.Stand([MilestoneState.Late])));
    }

    [Fact]
    public void An_opportunity_with_no_milestone_dates_scores_every_row_the_same()
    {
        // Which is what keeps a dateless board in join order: the sort is
        // stable, so with equal scores nothing moves.
        var finished = Schedule.Stand([MilestoneState.Claimed, MilestoneState.Claimed]);
        var untouched = Schedule.Stand([MilestoneState.Open, MilestoneState.Open]);

        Assert.Equal(0, Schedule.Score(finished));
        Assert.Equal(0, Schedule.Score(untouched));
        Assert.Equal(0, finished.Dated);
    }

    [Fact]
    public void Every_state_has_a_name_the_web_tier_can_switch_on()
    {
        var names = Enum.GetValues<MilestoneState>().Select(Schedule.Name).ToList();

        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
        Assert.All(names, n => Assert.Matches("^[a-z_]+$", n));
    }
}
