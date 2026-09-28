using WinnersPortal.Services.Opportunities;
using WinnersPortal.Domain;
using WinnersPortal.Services.Profiles;

namespace WinnersPortal.Services.Leaderboard;

/// <summary>
/// The public leaderboard's rules: who is listed under each tab, how a
/// "rank by" orders them, what a rank is, and what the trend arrow means.
/// Pure functions over <see cref="Member"/> rows, so the tests can say
/// exactly what the page will say without a database.
///
/// The board is a reading aid like every other number about a person here
/// (see <see cref="Merit"/>): it decides nothing, and it ranks only what the
/// portal itself recorded — the merit score, ratings from clients, dated
/// milestones met, opportunities won. The self-reported fields it reads — the
/// profile's time zone, kinds of work, availability and years — only place
/// a member on a board or inside a Talent-page filter, never order them.
/// </summary>
public static class Leaderboard
{
    /// <summary>How far back the trend arrow looks.</summary>
    public const int TrendDays = 30;

    /// <summary>How recently a member must have joined to be rising talent.</summary>
    public const int RisingDays = 90;

    /// <summary>The most rows a page carries — a leaderboard is a top, not a directory.</summary>
    public const int MaxRows = 100;

    public enum Tab { Global, Regional, Category, Rising, Performance }

    public enum RankBy { Merit, Rating, Delivery, Wins }

    /// <summary>One freelancer as the board reads them, facts only; the figures are derived below.</summary>
    public sealed record Member(
        Guid UserId,
        string Name,
        string? AvatarUrl,
        string? Headline,
        string? Location,
        /// <summary>A key from <see cref="Regions"/>, or null where the profile names no zone the board can place.</summary>
        string? Region,
        /// <summary>The primary category and the secondary ones, as keys.</summary>
        IReadOnlyList<string> Categories,
        int Merit,
        int RatingCount,
        int RatingSum,
        int MilestonesOnTime,
        int MilestonesDated,
        int Wins,
        /// <summary>Today's score less the oldest in the window; null with nothing to compare against.</summary>
        int? Trend,
        DateTimeOffset JoinedAtUtc,
        /// <summary>When they can start, as the profile says; null until they say. Read by the Talent page's filter only.</summary>
        Availability? Availability = null,
        /// <summary>Years of experience, as the profile says; null until they say. Read by the Talent page's filter only.</summary>
        int? YearsExperience = null);

    /// <summary>The star average, or null before the first rating — an unrated member is not a nought.</summary>
    public static double? Rating(Member m) => m.RatingCount == 0 ? null : (double)m.RatingSum / m.RatingCount;

    /// <summary>Dated milestones met on time, as a percentage, or null where no opportunity put a date to one.</summary>
    public static int? Delivery(Member m) =>
        m.MilestonesDated == 0 ? null : (int)Math.Round(100.0 * m.MilestonesOnTime / m.MilestonesDated);

    // ------------------------------------------------------------ regions

    /// <summary>The five regions a member can be placed on, in the order the picker lists them.</summary>
    public static readonly IReadOnlyList<(string Key, string Label)> Regions =
    [
        ("africa", "Africa"),
        ("americas", "Americas"),
        ("asia", "Asia"),
        ("europe", "Europe"),
        ("oceania", "Oceania"),
    ];

    public static string? RegionLabel(string? key) =>
        Regions.FirstOrDefault(r => r.Key == key) is { Key: not null } r ? r.Label : null;

    /// <summary>
    /// The region a time zone sits in. Location on a profile is free text —
    /// "Karachi, Pakistan" for one member, "Lahore" for the next — while the
    /// zone is an IANA name the browser supplied, and its first segment is
    /// the continent. Atlantic and Arctic zones are Europe's islands, the
    /// Indian Ocean's are Asia's, and the Etc zones place nobody.
    /// </summary>
    public static string? RegionOf(string? timeZone)
    {
        var slash = timeZone?.IndexOf('/') ?? -1;
        if (timeZone is null || slash <= 0) return null;
        return timeZone[..slash] switch
        {
            "Africa" => "africa",
            "America" => "americas",
            "Arctic" or "Atlantic" or "Europe" => "europe",
            "Asia" or "Indian" => "asia",
            "Australia" or "Pacific" => "oceania",
            _ => null,
        };
    }

    // ------------------------------------------------------------ parsing

    /// <summary>The tab a query string names; anything else is the global board.</summary>
    public static Tab TabOf(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "regional" => Tab.Regional,
        "category" => Tab.Category,
        "rising" => Tab.Rising,
        "performance" => Tab.Performance,
        _ => Tab.Global,
    };

    /// <summary>The ordering a query string names; anything else is the merit score.</summary>
    public static RankBy RankByOf(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "rating" => RankBy.Rating,
        "delivery" => RankBy.Delivery,
        "wins" => RankBy.Wins,
        _ => RankBy.Merit,
    };

    public static string Key(Tab tab) => tab.ToString().ToLowerInvariant();

    public static string Key(RankBy by) => by.ToString().ToLowerInvariant();

    // ------------------------------------------------------------ the tabs

    /// <summary>
    /// Whether a member is on a tab. Global is everybody; Regional and
    /// Category are those placed on the chosen one; Rising is whoever
    /// joined within <see cref="RisingDays"/>; Performance is whoever a
    /// client has passed a verdict on — a win or a rating — so that it
    /// lists proven delivery rather than a well-written profile.
    /// </summary>
    public static bool Listed(Member m, Tab tab, string? region, string? category, DateTimeOffset now) => tab switch
    {
        Tab.Regional => region is not null && m.Region == region,
        Tab.Category => category is not null && m.Categories.Contains(category, StringComparer.Ordinal),
        Tab.Rising => m.JoinedAtUtc >= now.AddDays(-RisingDays),
        Tab.Performance => m.Wins > 0 || m.RatingCount > 0,
        _ => true,
    };

    // ------------------------------------------------------------ ordering

    /// <summary>
    /// The figure a rank-by orders on. Null where the portal has not read
    /// one — no rating yet, no dated milestone faced — which sorts below
    /// every real figure rather than as a nought that would tie with a
    /// member who scored nought.
    /// </summary>
    public static double? KeyOf(Member m, RankBy by) => by switch
    {
        RankBy.Rating => Rating(m),
        RankBy.Delivery => Delivery(m),
        RankBy.Wins => m.Wins,
        _ => m.Merit,
    };

    private static double Sortable(Member m, RankBy by) => KeyOf(m, by) ?? double.NegativeInfinity;

    /// <summary>
    /// Best first. Among equals on the chosen figure, the one with more
    /// behind it — more ratings behind an average, more dated milestones
    /// behind a percentage — then the higher merit, the more wins, and
    /// the name, so the order is the same on every read.
    /// </summary>
    public static IReadOnlyList<Member> Order(IEnumerable<Member> members, RankBy by) =>
        members
            .OrderByDescending(m => Sortable(m, by))
            .ThenByDescending(m => by switch
            {
                RankBy.Rating => m.RatingCount,
                RankBy.Delivery => m.MilestonesDated,
                _ => 0,
            })
            .ThenByDescending(m => m.Merit)
            .ThenByDescending(m => m.Wins)
            .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.UserId)
            .ToList();

    /// <summary>
    /// Competition rank on the chosen figure: one more than the number
    /// with a higher one, so equals share a rank and the next takes the
    /// place after them (#1, #1, #3) — the rule the Welcome screen's rank
    /// has always used (<see cref="Welcome.Rank"/>). A member the figure has
    /// not been read for has no rank on it: a dash, not a shared last place,
    /// and not a shared first one on a board nobody has been rated on yet.
    /// </summary>
    public static int? Rank(Member m, IReadOnlyCollection<Member> among, RankBy by)
    {
        var mine = KeyOf(m, by);
        if (mine is null) return null;
        return 1 + among.Count(o => Sortable(o, by) > mine.Value);
    }

    // ------------------------------------------------------------ the trend

    /// <summary>
    /// Today's score against the oldest snapshot inside the window and
    /// before today — the change over up to <see cref="TrendDays"/> days.
    /// Null where there is no earlier day to compare with: a member new
    /// to the board has no trend, not a trend of nought.
    /// </summary>
    public static int? Trend(int today, IEnumerable<(DateOnly Day, int Score)> history, DateOnly todayDay)
    {
        var floor = todayDay.AddDays(-TrendDays);
        (DateOnly Day, int Score)? baseline = null;
        foreach (var h in history)
        {
            if (h.Day < floor || h.Day >= todayDay) continue;
            if (baseline is null || h.Day < baseline.Value.Day) baseline = h;
        }
        return baseline is null ? null : today - baseline.Value.Score;
    }
}
