using WinnersPortal.Domain;
using WinnersPortal.Services.Leaderboard;
using Xunit;
using static WinnersPortal.Services.Leaderboard.Leaderboard;

namespace WinnersPortal.Api.Tests;

/// <summary>
/// The public Talent page's rules (Leaderboard/Talent.cs): which members a
/// filter keeps, who wears the tick, and who fills each of the four panels.
/// Pure functions over hand-built members, so each rule is pinned to what
/// the page will actually show.
/// </summary>
public class TalentTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    private static Member M(
        string name, int merit = 50, int ratings = 0, int stars = 0, int onTime = 0, int dated = 0,
        int wins = 0, string? zone = null, string[]? categories = null, int? trend = null,
        Availability? availability = null, int? years = null) =>
        new(Guid.NewGuid(), name, null, null, null, RegionOf(zone), categories ?? [],
            merit, ratings, stars, onTime, dated, wins, trend, Now.AddDays(-365), availability, years);

    // ----------------------------------------------------------- filters

    [Fact]
    public void The_empty_filter_keeps_everybody()
    {
        Assert.True(Talent.Filter.None.IsEmpty);
        Assert.True(Talent.Matches(M("Blank"), Talent.Filter.None));
        Assert.True(Talent.Matches(M("Full", 90, 3, 15, 4, 4, 2, "Asia/Karachi", ["web"], 5, Availability.Now, 8), Talent.Filter.None));
    }

    [Fact]
    public void A_category_or_a_region_keeps_those_placed_on_it()
    {
        var web = M("Web", categories: ["web", "mobile"], zone: "Europe/London");
        var mobile = M("Mobile", categories: ["mobile"], zone: "Asia/Karachi");

        var byCategory = Talent.Filter.None with { Category = "web" };
        Assert.True(Talent.Matches(web, byCategory));
        Assert.False(Talent.Matches(mobile, byCategory));

        var byRegion = Talent.Filter.None with { Region = "asia" };
        Assert.False(Talent.Matches(web, byRegion));
        Assert.True(Talent.Matches(mobile, byRegion));
    }

    [Fact]
    public void A_floor_keeps_members_at_or_above_it()
    {
        var f = Talent.Filter.None with { MinMerit = 50, MinWins = 3 };
        Assert.True(Talent.Matches(M("Exactly", merit: 50, wins: 3), f));
        Assert.True(Talent.Matches(M("Above", merit: 80, wins: 9), f));
        Assert.False(Talent.Matches(M("Low score", merit: 49, wins: 9), f));
        Assert.False(Talent.Matches(M("Few wins", merit: 80, wins: 2), f));
    }

    [Fact]
    public void What_a_profile_has_not_said_cannot_satisfy_a_filter_that_asks_for_it()
    {
        var years = Talent.Filter.None with { MinYears = 5 };
        Assert.True(Talent.Matches(M("Said", years: 5), years));
        Assert.False(Talent.Matches(M("Fewer", years: 4), years));
        Assert.False(Talent.Matches(M("Silent"), years));

        var now = Talent.Filter.None with { Availability = Availability.Now };
        Assert.True(Talent.Matches(M("Free", availability: Availability.Now), now));
        Assert.False(Talent.Matches(M("Busy", availability: Availability.WithinTwoWeeks), now));
        Assert.False(Talent.Matches(M("Unsaid"), now));
    }

    [Theory]
    [InlineData("75", 75)]
    [InlineData(" 25 ", 25)]
    [InlineData("60", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("high", null)]
    public void A_floor_is_read_only_from_the_offered_list(string? asked, int? floor)
    {
        Assert.Equal(floor, Talent.FloorOf(asked, Talent.MeritFloors));
    }

    // ---------------------------------------------------------- the tick

    [Fact]
    public void The_tick_comes_from_a_client_s_verdict_and_nothing_else()
    {
        Assert.True(Talent.Verified(M("Won", wins: 1)));
        Assert.True(Talent.Verified(M("Rated", ratings: 1, stars: 3)));
        Assert.False(Talent.Verified(M("Well written", merit: 99, years: 20, availability: Availability.Now)));
    }

    // -------------------------------------------------------- the panels

    [Fact]
    public void Top_talent_is_the_four_highest_scores_in_the_board_s_order()
    {
        var members = new[] { M("D", 40), M("A", 90), M("E", 30), M("B", 80), M("C", 60) };
        Assert.Equal(["A", "B", "C", "D"], Talent.Top(members).Select(m => m.Name));
    }

    [Fact]
    public void Rising_talent_is_the_steepest_climb_and_only_a_climb()
    {
        var members = new[]
        {
            M("Held", 90, trend: 0),
            M("Fell", 95, trend: -4),
            M("New", 99),
            M("Slow", 40, trend: 2),
            M("Fast", 30, trend: 12),
            M("Also fast", 70, trend: 12),
            M("Faster", 20, trend: 20),
            M("Steady", 50, trend: 5),
        };
        // Steepest first; the same climb from a higher score first; the rest are not rising.
        Assert.Equal(["Faster", "Also fast", "Fast", "Steady"], Talent.Rising(members).Select(m => m.Name));
    }

    [Fact]
    public void Category_leaders_is_the_top_score_per_kind_of_work_strongest_first()
    {
        var members = new[]
        {
            M("Web one", 70, categories: ["web"]),
            M("Web two", 85, categories: ["web", "mobile"]),
            M("Mobile one", 60, categories: ["mobile"]),
            M("Data one", 90, categories: ["data"]),
            M("Nobody's", 99),
        };
        var leaders = Talent.Leaders(members, ["web", "mobile", "data", "design"]);

        Assert.Equal([("data", "Data one"), ("web", "Web two"), ("mobile", "Web two")],
            leaders.Select(x => (x.Category, x.Leader.Name)));
    }

    [Fact]
    public void Category_leaders_carries_at_most_a_row()
    {
        var kinds = Enumerable.Range(0, 7).Select(i => $"kind{i}").ToList();
        var members = kinds.Select((k, i) => M($"Leader {i}", 50 + i, categories: [k])).ToList();
        var leaders = Talent.Leaders(members, kinds);

        Assert.Equal(Talent.PanelSize, leaders.Count);
        Assert.Equal("Leader 6", leaders[0].Leader.Name);
    }

    [Fact]
    public void Delivery_champions_lead_on_the_on_time_share_with_more_milestones_first_among_equals()
    {
        var members = new[]
        {
            M("Untested", 99),
            M("Perfect of two", 30, onTime: 2, dated: 2),
            M("Perfect of six", 20, onTime: 6, dated: 6),
            M("Nine of ten", 80, onTime: 9, dated: 10),
            M("Half", 70, onTime: 1, dated: 2),
            M("Late", 60, onTime: 0, dated: 3),
        };
        Assert.Equal(["Perfect of six", "Perfect of two", "Nine of ten", "Half"],
            Talent.Champions(members).Select(m => m.Name));
    }

    [Fact]
    public void A_panel_over_nobody_is_empty_not_an_error()
    {
        Assert.Empty(Talent.Top([]));
        Assert.Empty(Talent.Rising([M("Still", trend: 0)]));
        Assert.Empty(Talent.Leaders([M("Uncategorised")], ["web"]));
        Assert.Empty(Talent.Champions([M("Untested")]));
    }
}
