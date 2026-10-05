using WinnersPortal.Services.Database;
using Xunit;

namespace WinnersPortal.Api.Tests;

/// <summary>
/// What a tested database lets each screen do: the first connect takes a new,
/// empty or portal database; a move takes only an empty one; an
/// administrator's edit saves onto this portal's data and moves into an
/// empty database — and nobody is let onto another portal's or another
/// application's tables.
/// </summary>
public class DatabaseProbeTests
{
    private static DatabaseFacts Facts(
        bool exists = true, bool empty = false, bool portal = false, bool? same = null,
        DatabaseFreshness? freshness = null, string? problem = null) =>
        new(Reachable: problem is null, "SQL Server 16.0", true, true, exists, empty, portal, same, freshness, problem);

    private static readonly DatabaseFacts Missing = Facts(exists: false, empty: true);
    private static readonly DatabaseFacts EmptyDb = Facts(empty: true);
    private static readonly DatabaseFacts ThisPortal = Facts(portal: true, same: true, freshness: new(5, 3, DateTimeOffset.Parse("2026-09-29T10:00Z")));
    private static readonly DatabaseFacts OtherPortal = Facts(portal: true, same: false);
    private static readonly DatabaseFacts Foreign = Facts();

    [Fact]
    public void Connect_takes_a_new_an_empty_or_a_portal_database()
    {
        Assert.Equal("connect", DatabaseProbe.Verdict(Missing, ProbePurpose.Connect, false, null).Action);
        Assert.Equal("connect", DatabaseProbe.Verdict(EmptyDb, ProbePurpose.Connect, false, null).Action);
        Assert.Equal(("connect", null, null), DatabaseProbe.Verdict(ThisPortal, ProbePurpose.Connect, false, null));

        // A portal whose keys are elsewhere may be connected, with the warning.
        var (action, problem, warning) = DatabaseProbe.Verdict(OtherPortal, ProbePurpose.Connect, false, null);
        Assert.Equal("connect", action);
        Assert.Null(problem);
        Assert.Contains("keys", warning);

        var foreign = DatabaseProbe.Verdict(Foreign, ProbePurpose.Connect, false, null);
        Assert.Null(foreign.Action);
        Assert.Contains("not the portal's", foreign.Problem);
    }

    [Fact]
    public void A_move_takes_only_an_empty_database_that_is_not_the_current_one()
    {
        Assert.Equal("move", DatabaseProbe.Verdict(EmptyDb, ProbePurpose.Move, false, null).Action);
        Assert.Equal("move", DatabaseProbe.Verdict(Missing, ProbePurpose.Move, false, null).Action);
        Assert.Contains("already holds", DatabaseProbe.Verdict(ThisPortal, ProbePurpose.Move, false, null).Problem);
        Assert.Contains("on now", DatabaseProbe.Verdict(EmptyDb, ProbePurpose.Move, true, null).Problem);
    }

    [Fact]
    public void A_change_saves_onto_this_portal_and_moves_into_an_empty_database()
    {
        // The same database with a new password, or a restored copy the keys open.
        Assert.Equal(("switch", null, null), DatabaseProbe.Verdict(ThisPortal, ProbePurpose.Change, true, null));
        Assert.Equal("switch", DatabaseProbe.Verdict(ThisPortal, ProbePurpose.Change, false, null).Action);
        Assert.Equal("move", DatabaseProbe.Verdict(EmptyDb, ProbePurpose.Change, false, null).Action);

        var other = DatabaseProbe.Verdict(OtherPortal, ProbePurpose.Change, false, null);
        Assert.Null(other.Action);
        Assert.Contains("another portal", other.Problem);
        Assert.Null(DatabaseProbe.Verdict(Foreign, ProbePurpose.Change, false, null).Action);
    }

    [Fact]
    public void An_older_copy_is_saved_onto_only_with_a_warning()
    {
        var here = new DatabaseFreshness(9, 4, DateTimeOffset.Parse("2026-09-29T12:00Z"));
        var (action, _, warning) = DatabaseProbe.Verdict(ThisPortal, ProbePurpose.Change, false, here);
        Assert.Equal("switch", action);
        Assert.Contains("older", warning);
        Assert.Contains("5 accounts", warning);

        var level = new DatabaseFreshness(5, 3, DateTimeOffset.Parse("2026-09-29T10:00Z"));
        Assert.Null(DatabaseProbe.Verdict(ThisPortal, ProbePurpose.Change, false, level).Warning);
    }

    [Fact]
    public void A_problem_found_on_the_way_stops_every_purpose()
    {
        var unreachable = Facts(problem: "The server could not be reached");
        foreach (var purpose in Enum.GetValues<ProbePurpose>())
            Assert.Equal((null, "The server could not be reached", null), DatabaseProbe.Verdict(unreachable, purpose, false, null));
    }
}
