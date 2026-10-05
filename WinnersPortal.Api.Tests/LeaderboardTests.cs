using WinnersPortal.Services.Opportunities;
using WinnersPortal.Services.Profiles;
using WinnersPortal.Services.Leaderboard;
using Xunit;
using static WinnersPortal.Services.Leaderboard.Leaderboard;

namespace WinnersPortal.Api.Tests;

/// <summary>
/// The public leaderboard's rules (Leaderboard/Leaderboard.cs): which tab
/// lists whom, how a rank-by orders and ranks, where a member is placed,
/// and what the trend arrow compares. Pure functions over hand-built
/// members, so each rule is pinned to what the page will actually say.
/// </summary>
public class LeaderboardTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 9, 16);

    private static Member M(
        string name, int merit = 50, int ratings = 0, int stars = 0, int onTime = 0, int dated = 0,
        int wins = 0, string? zone = null, string[]? categories = null, int joinedDaysAgo = 365, int? trend = null) =>
        new(Guid.NewGuid(), name, null, null, null, RegionOf(zone), categories ?? [],
            merit, ratings, stars, onTime, dated, wins, trend, Now.AddDays(-joinedDaysAgo));

    // ----------------------------------------------------------- regions

    [Theory]
    [InlineData("Asia/Karachi", "asia")]
    [InlineData("Indian/Maldives", "asia")]
    [InlineData("Europe/London", "europe")]
    [InlineData("Atlantic/Reykjavik", "europe")]
    [InlineData("America/New_York", "americas")]
    [InlineData("America/Argentina/Buenos_Aires", "americas")]
    [InlineData("Africa/Lagos", "africa")]
    [InlineData("Australia/Sydney", "oceania")]
    [InlineData("Pacific/Auckland", "oceania")]
    public void A_time_zone_places_a_member_on_its_continent(string zone, string region)
    {
        Assert.Equal(region, RegionOf(zone));
        Assert.NotNull(RegionLabel(region));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("UTC")]
    [InlineData("Etc/GMT+5")]
    [InlineData("/Karachi")]
    [InlineData("Mars/Olympus")]
    public void A_zone_the_board_cannot_place_puts_a_member_on_no_region(string? zone)
    {
        Assert.Null(RegionOf(zone));
        Assert.Null(RegionLabel(RegionOf(zone)));
    }

    // ---------------------------------------------------------- the tabs

    [Fact]
    public void Global_lists_everyone_and_the_other_tabs_narrow()
    {
        var karachi = M("Karachi", zone: "Asia/Karachi", categories: ["web", "mobile"], joinedDaysAgo: 400);
        var london = M("London", zone: "Europe/London", categories: ["design"], joinedDaysAgo: 10, wins: 1);
        var nowhere = M("Nowhere", ratings: 2, stars: 9, joinedDaysAgo: 89);
        Member[] all = [karachi, london, nowhere];

        Assert.Equal(all, all.Where(m => Listed(m, Tab.Global, null, null, Now)));
        Assert.Equal([karachi], all.Where(m => Listed(m, Tab.Regional, "asia", null, Now)));
        Assert.Empty(all.Where(m => Listed(m, Tab.Regional, null, null, Now)));
        Assert.Equal([karachi], all.Where(m => Listed(m, Tab.Category, null, "mobile", Now)));
        Assert.Empty(all.Where(m => Listed(m, Tab.Category, null, null, Now)));
        // Ninety days, counted from now — the ninetieth day is in, the four-hundredth is not.
        Assert.Equal([london, nowhere], all.Where(m => Listed(m, Tab.Rising, null, null, Now)));
        // A client's verdict: a win or a rating. A profile alone is not performance.
        Assert.Equal([london, nowhere], all.Where(m => Listed(m, Tab.Performance, null, null, Now)));
    }

    // ---------------------------------------------------------- ordering

    [Fact]
    public void Each_rank_by_orders_best_first_on_its_own_figure()
    {
        var a = M("A", merit: 40, ratings: 1, stars: 5, onTime: 1, dated: 4, wins: 0);
        var b = M("B", merit: 90, ratings: 10, stars: 45, onTime: 3, dated: 4, wins: 2);
        var c = M("C", merit: 70, ratings: 4, stars: 19, onTime: 2, dated: 2, wins: 1);

        Assert.Equal([b, c, a], Order([a, b, c], RankBy.Merit));
        Assert.Equal([a, c, b], Order([a, b, c], RankBy.Rating));    // 5.0, 4.75, 4.5
        Assert.Equal([c, b, a], Order([a, b, c], RankBy.Delivery));  // 100%, 75%, 25%
        Assert.Equal([b, c, a], Order([a, b, c], RankBy.Wins));
    }

    [Fact]
    public void A_figure_the_portal_has_not_read_sorts_below_every_real_one()
    {
        var unrated = M("Unrated", merit: 99);
        var one = M("One star", merit: 10, ratings: 1, stars: 1);
        var undated = M("Undated", merit: 99);
        var late = M("Late", merit: 10, onTime: 0, dated: 3);

        Assert.Null(Rating(unrated));
        Assert.Null(Delivery(undated));
        Assert.Equal(0, Delivery(late));
        // Nought on time is a real figure; no rating at all is not one.
        Assert.Equal([one, unrated], Order([unrated, one], RankBy.Rating));
        Assert.Equal([late, undated], Order([undated, late], RankBy.Delivery));
    }

    [Fact]
    public void Among_equals_more_behind_the_figure_comes_first_then_merit_then_the_name()
    {
        var two = M("Two ratings", merit: 60, ratings: 2, stars: 10);
        var five = M("Five ratings", merit: 60, ratings: 5, stars: 25);
        Assert.Equal([five, two], Order([two, five], RankBy.Rating));

        var few = M("Few dated", merit: 80, onTime: 2, dated: 2);
        var many = M("Many dated", merit: 20, onTime: 6, dated: 6);
        Assert.Equal([many, few], Order([few, many], RankBy.Delivery));

        var zed = M("Zed", merit: 50, wins: 1);
        var amy = M("amy", merit: 50, wins: 1);
        Assert.Equal([amy, zed], Order([zed, amy], RankBy.Wins));
        Assert.Equal([amy, zed], Order([zed, amy], RankBy.Merit));
    }

    [Fact]
    public void Equal_figures_share_a_rank_and_the_next_takes_the_place_after_them()
    {
        var first = M("First", merit: 90);
        var alsoFirst = M("Also first", merit: 90);
        var third = M("Third", merit: 70);
        var listed = Order([third, first, alsoFirst], RankBy.Merit);

        Assert.Equal(1, Rank(first, listed, RankBy.Merit));
        Assert.Equal(1, Rank(alsoFirst, listed, RankBy.Merit));
        Assert.Equal(3, Rank(third, listed, RankBy.Merit));
        // Alone on the board is first, not nowhere.
        Assert.Equal(1, Rank(third, [third], RankBy.Merit));
    }

    [Fact]
    public void Members_without_the_figure_have_no_rank_on_it()
    {
        var rated = M("Rated", ratings: 1, stars: 3);
        var x = M("X");
        var y = M("Y");
        var listed = Order([x, rated, y], RankBy.Rating);

        Assert.Equal(1, Rank(rated, listed, RankBy.Rating));
        Assert.Null(Rank(x, listed, RankBy.Rating));
        Assert.Null(Rank(y, listed, RankBy.Rating));
        // A board nobody has been rated on yet has no #1, not four of them.
        Assert.Null(Rank(x, [x, y], RankBy.Rating));
    }

    // ------------------------------------------------------------ figures

    [Fact]
    public void Delivery_is_the_on_time_share_rounded_and_rating_is_the_star_average()
    {
        Assert.Equal(67, Delivery(M("x", onTime: 2, dated: 3)));
        Assert.Equal(100, Delivery(M("x", onTime: 3, dated: 3)));
        Assert.Equal(4.5, Rating(M("x", ratings: 2, stars: 9)));
    }

    // ---------------------------------------------------------- the trend

    [Fact]
    public void The_trend_is_today_against_the_oldest_day_in_the_window()
    {
        (DateOnly, int)[] history =
        [
            (Today.AddDays(-31), 10),  // outside the window — a month and a day ago
            (Today.AddDays(-30), 40),  // the oldest inside it
            (Today.AddDays(-7), 55),
            (Today, 70),               // today's own row is not a yesterday
        ];

        Assert.Equal(20, Trend(60, history, Today));
        Assert.Equal(-5, Trend(35, history, Today));
        Assert.Equal(0, Trend(40, history, Today));
    }

    [Fact]
    public void A_member_with_no_earlier_day_has_no_trend_rather_than_a_trend_of_nought()
    {
        Assert.Null(Trend(60, [], Today));
        Assert.Null(Trend(60, [(Today, 60)], Today));
        Assert.Null(Trend(60, [(Today.AddDays(-31), 10)], Today));
    }

    // ------------------------------------------------------------ parsing

    [Theory]
    [InlineData("regional", Tab.Regional)]
    [InlineData(" Rising ", Tab.Rising)]
    [InlineData("PERFORMANCE", Tab.Performance)]
    [InlineData("category", Tab.Category)]
    [InlineData("global", Tab.Global)]
    [InlineData("anything", Tab.Global)]
    [InlineData(null, Tab.Global)]
    public void The_tab_in_the_address_is_read_leniently_and_defaults_to_global(string? given, Tab tab)
    {
        Assert.Equal(tab, TabOf(given));
        Assert.Equal(tab, TabOf(Key(tab)));
    }

    [Theory]
    [InlineData("rating", RankBy.Rating)]
    [InlineData("Delivery", RankBy.Delivery)]
    [InlineData("WINS", RankBy.Wins)]
    [InlineData("merit", RankBy.Merit)]
    [InlineData("score", RankBy.Merit)]
    [InlineData(null, RankBy.Merit)]
    public void The_rank_by_in_the_address_is_read_leniently_and_defaults_to_merit(string? given, RankBy by)
    {
        Assert.Equal(by, RankByOf(given));
        Assert.Equal(by, RankByOf(Key(by)));
    }
}
