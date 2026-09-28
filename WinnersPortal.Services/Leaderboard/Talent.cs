using WinnersPortal.Services.Opportunities;
using WinnersPortal.Services.Profiles;
using WinnersPortal.Domain;
using static WinnersPortal.Services.Leaderboard.Leaderboard;

namespace WinnersPortal.Services.Leaderboard;

/// <summary>
/// The public Talent page's rules: which members a set of filters keeps,
/// and who fills each of its four panels — the highest merit, the fastest
/// climbing, the leader of each kind of work, and the most reliable. Pure
/// functions over the leaderboard's <see cref="Member"/> rows, so the page
/// and the board can never disagree about a figure.
///
/// Like the board, the page ranks only what the portal recorded. The
/// filters are the one place it reads what a member wrote about themselves
/// — a kind of work, a time zone, when they can start, years of experience
/// — and only to narrow the field, never to order it.
/// </summary>
public static class Talent
{
    /// <summary>How many cards a panel carries: one row, and "View all" for the rest.</summary>
    public const int PanelSize = 4;

    /// <summary>The merit floors a filter may ask for — the bottoms of the score's bands (<see cref="Profiles.Merit.Band"/>).</summary>
    public static readonly IReadOnlyList<int> MeritFloors = [75, 50, 25];

    /// <summary>The years of experience a filter may ask for at least.</summary>
    public static readonly IReadOnlyList<int> ExperienceFloors = [1, 3, 5, 10];

    /// <summary>The opportunity wins a filter may ask for at least.</summary>
    public static readonly IReadOnlyList<int> WinFloors = [1, 3, 5, 10];

    /// <summary>
    /// What the page has been asked to narrow to. Null on any part is "any",
    /// so the empty filter keeps everybody the board would list.
    /// </summary>
    public sealed record Filter(
        string? Category,
        string? Region,
        int? MinMerit,
        Availability? Availability,
        int? MinYears,
        int? MinWins)
    {
        public static readonly Filter None = new(null, null, null, null, null, null);

        public bool IsEmpty => this == None;
    }

    /// <summary>A floor a query string names, if it is one the page offers; anything else is "any".</summary>
    public static int? FloorOf(string? s, IReadOnlyList<int> offered) =>
        int.TryParse(s?.Trim(), out var n) && offered.Contains(n) ? n : null;

    /// <summary>
    /// Whether a member is inside the filter. A fact the profile has not
    /// given — no years of experience, no word on availability — cannot
    /// satisfy a filter that asks for it: "5+ years" lists members who
    /// said so, not members who said nothing.
    /// </summary>
    public static bool Matches(Member m, Filter f) =>
        (f.Category is null || m.Categories.Contains(f.Category, StringComparer.Ordinal))
        && (f.Region is null || m.Region == f.Region)
        && (f.MinMerit is null || m.Merit >= f.MinMerit)
        && (f.Availability is null || m.Availability == f.Availability)
        && (f.MinYears is null || m.YearsExperience >= f.MinYears)
        && (f.MinWins is null || m.Wins >= f.MinWins);

    /// <summary>
    /// The tick beside a name: the portal itself has verified this member's
    /// work — an opportunity won here or a rating from a client after a paid
    /// award. What a profile says about itself is never verified; what the
    /// record says was stamped by a client.
    /// </summary>
    public static bool Verified(Member m) => m.Wins > 0 || m.RatingCount > 0;

    // ------------------------------------------------------------ the panels

    /// <summary>Top Talent: the highest merit scores, in the board's order.</summary>
    public static IReadOnlyList<Member> Top(IEnumerable<Member> members) =>
        Order(members, RankBy.Merit).Take(PanelSize).ToList();

    /// <summary>
    /// Rising Talent: whose merit score climbed most over the trend window
    /// (<see cref="TrendDays"/>). Only a real climb counts — a score that
    /// held, fell, or has no earlier day to compare with is not rising —
    /// and among equal climbs the higher score comes first.
    /// </summary>
    public static IReadOnlyList<Member> Rising(IEnumerable<Member> members) =>
        Order(members.Where(m => m.Trend > 0), RankBy.Merit)
            .OrderByDescending(m => m.Trend)
            .Take(PanelSize)
            .ToList();

    /// <summary>
    /// Category Leaders: the top merit score among the members who named
    /// each kind of work, one card per kind that has somebody, strongest
    /// leader first. The kinds are passed in the order the portal lists
    /// them, which settles a tie between two leaders on the same score.
    /// </summary>
    public static IReadOnlyList<(string Category, Member Leader)> Leaders(
        IEnumerable<Member> members, IEnumerable<string> categories)
    {
        var all = members.ToList();
        return categories
            .Select(c => (Category: c, Leader: Order(all.Where(m => m.Categories.Contains(c, StringComparer.Ordinal)), RankBy.Merit).FirstOrDefault()))
            .Where(x => x.Leader is not null)
            .Select(x => (x.Category, x.Leader!))
            .OrderByDescending(x => x.Item2.Merit)
            .Take(PanelSize)
            .ToList();
    }

    /// <summary>
    /// Delivery Champions: the best on-time records, in the board's order
    /// for delivery — more dated milestones first among equal shares. A
    /// member who has faced no dated milestone has no record to lead on.
    /// </summary>
    public static IReadOnlyList<Member> Champions(IEnumerable<Member> members) =>
        Order(members.Where(m => Delivery(m) is not null), RankBy.Delivery).Take(PanelSize).ToList();
}
