using WinnersPortal.Services.Opportunities;

namespace WinnersPortal.Services.Profiles;

/// <summary>
/// The merit score: one number for how much a client can lean on what they
/// are reading about an entrant, out of 100.
///
/// Two halves, and the split is the whole design. The portfolio half is
/// self-reported and capped at 30, because anyone can type anything and a
/// score that could be maxed out by an afternoon of writing would be worth
/// nothing. The record half is 70 and is earned on this portal: opportunities
/// entered, milestones met on time, awards won, ratings from the other side
/// of finished deals. Nothing here is a secret formula — every point has a
/// line in the breakdown, and the profile page shows the person their own.
///
/// It decides nothing. It does not gate entry, cap an award, order the
/// board, or pick a winner; the client picks the winner by reading the code.
/// It is a reading aid, and it is deliberately hard to move without doing
/// the work it describes.
/// </summary>
public static class Merit
{
    /// <summary>The written half at full house. What a given portal offers can be less — see <see cref="PortfolioAvailable"/>.</summary>
    public const int PortfolioMax = 30;
    public const int RecordMax = 70;
    public const int Max = PortfolioMax + RecordMax;

    /// <summary>Everything self-reported that the score reads.</summary>
    public sealed record Portfolio(
        bool HasHeadline,
        bool HasBio,
        int Skills,
        int Projects,
        int ProjectsWithLinks,
        bool HasDetails,
        bool GithubConnected,
        /// <summary>Whether this portal offers a GitHub sign-in at all — see <see cref="PortfolioParts"/>.</summary>
        bool GithubOffered);

    /// <summary>Everything the portal itself watched happen.</summary>
    public sealed record Record(
        int Entries,
        int Wins,
        int MilestonesOnTime,
        int MilestonesDated,
        int RatingCount,
        int RatingSum);

    /// <summary>One line of the breakdown: what was earned, and out of what.</summary>
    public sealed record Part(string Label, int Earned, int Available, string Detail);

    public static IReadOnlyList<Part> PortfolioParts(Portfolio p) =>
    [
        new("A headline and an introduction", (p.HasHeadline ? 3 : 0) + (p.HasBio ? 4 : 0), 7,
            "Says what you do before anyone opens your code."),
        // Three is a shape; eight is a list. Rewarding the first few and
        // then stopping keeps this from becoming a keyword dump.
        new("Skills listed", Math.Min(p.Skills, 6), 6,
            "Up to six count. Naming twenty tools says less than naming six."),
        new("Past work", Math.Min(p.Projects * 2, 6), 6,
            "Two points each, for the first three."),
        new("Work anyone can look at", Math.Min(p.ProjectsWithLinks * 2, 4), 4,
            "A project with a link or a repository is worth double one without."),
        new("Where and when you work", p.HasDetails ? 4 : 0, 4,
            "Location, hours a week, years of experience — what makes a deadline real."),
        // A portal with no GitHub sign-in cannot offer these three to anyone,
        // so they leave the denominator rather than sitting there unearnable —
        // otherwise the completeness bar stops at 90% for everybody, forever.
        // An account that connected before the portal dropped its GitHub App
        // keeps them: the connection is still a fact about that account, and
        // the score must not move under someone for an admin's settings edit.
        new("GitHub connected", p.GithubConnected ? 3 : 0,
            p.GithubOffered || p.GithubConnected ? 3 : 0,
            p.GithubOffered || p.GithubConnected
                ? "Ties this account to a real public identity."
                : "This portal has no GitHub sign-in, so these points are not on offer to anyone here."),
    ];

    public static IReadOnlyList<Part> RecordParts(Record r)
    {
        // Diminishing returns on volume: entering is cheap, and a score that
        // paid linearly for it would reward spraying entries at everything.
        var entered = r.Entries switch { 0 => 0, 1 => 5, 2 or 3 => 9, 4 or 5 => 12, _ => 15 };
        var punctuality = r.MilestonesDated == 0
            ? 0
            : (int)Math.Round(20.0 * r.MilestonesOnTime / r.MilestonesDated);
        var wins = Math.Min(r.Wins * 10, 20);
        // A single five-star rating is not a reputation. The average carries
        // the quality, the count carries the confidence, and both are needed.
        var ratings = r.RatingCount == 0
            ? 0
            : (int)Math.Round(15.0 * ((double)r.RatingSum / r.RatingCount / 5.0)
                * Math.Min(r.RatingCount, 5) / 5.0);

        return
        [
            new("Opportunities entered", entered, 15,
                r.MilestonesDated == 0 && r.Entries == 0
                    ? "Nothing yet — the first entry is worth five."
                    : "Fifteen at six or more; entering is cheap, so this flattens quickly."),
            new("Milestones met on time", punctuality, 20,
                r.MilestonesDated == 0
                    ? "No opportunity you entered has put dates on its milestones yet."
                    : $"{r.MilestonesOnTime} of {r.MilestonesDated} dated milestones claimed on time."),
            new("Opportunities won", wins, 20, "Ten each, for the first two."),
            new("Ratings from clients", ratings, 15,
                r.RatingCount == 0
                    ? "Ratings open when an award you won is marked paid."
                    : $"{r.RatingCount} rating{(r.RatingCount == 1 ? "" : "s")}, and the count matters "
                        + "as much as the stars until there are five."),
        ];
    }

    public static int PortfolioScore(Portfolio p) => PortfolioParts(p).Sum(x => x.Earned);

    /// <summary>
    /// What the written half is out of <em>here</em>. <see cref="PortfolioMax"/>
    /// is the full house; a part this portal cannot offer leaves both sides of
    /// the fraction, so the ceiling stays reachable.
    /// </summary>
    public static int PortfolioAvailable(Portfolio p) => PortfolioParts(p).Sum(x => x.Available);

    /// <summary>The whole score's ceiling for this person, on this portal.</summary>
    public static int MaxFor(Portfolio p) => PortfolioAvailable(p) + RecordMax;

    public static int RecordScore(Record r) => RecordParts(r).Sum(x => x.Earned);

    public static int Score(Portfolio p, Record r) => PortfolioScore(p) + RecordScore(r);

    /// <summary>
    /// How much of the self-reported half is filled in, as a percentage —
    /// the number the "finish your profile" nudge counts down. Deliberately
    /// the portfolio only: nobody can complete their track record today.
    /// </summary>
    public static int Completeness(Portfolio p) =>
        (int)Math.Round(100.0 * PortfolioScore(p) / PortfolioAvailable(p));

    /// <summary>
    /// A word for the number, so a client reading a list does not have to
    /// hold a scale in their head. The bands are wide on purpose.
    /// </summary>
    public static string Band(int score) => score switch
    {
        >= 75 => "proven",
        >= 50 => "established",
        >= 25 => "building",
        _ => "new here",
    };
}
