using System.Globalization;
using WinnersPortal.Services.Opportunities;

namespace WinnersPortal.Services.Profiles;

/// <summary>
/// The merit score: one number for how much a client can lean on what they
/// are reading about an entrant, out of 100.
///
/// Two halves, and the split is the whole design. The portfolio half is
/// self-reported and capped at 25, because anyone can type anything and a
/// score that could be maxed out by an afternoon of writing would be worth
/// nothing. The record half is 75 and is earned on this portal: opportunities
/// entered, milestones met on time, awards won, ratings from the other side
/// of finished deals. Nothing here is a secret formula — every point has a
/// line in the breakdown, and the profile page shows the person their own.
///
/// It picks no winner and caps no award; the client picks the winner by
/// reading the work. It shuts a door only where a client set a minimum merit
/// score (<see cref="Fit"/>), and it orders the leaderboard unless another
/// figure is picked there. It is a reading aid, and it is deliberately hard
/// to move without doing the work it describes.
/// </summary>
public static class Merit
{
    /// <summary>The written half at full house. What a given portal offers can be less — see <see cref="PortfolioAvailable"/>.</summary>
    public const int PortfolioMax = 25;
    public const int RecordMax = 75;
    public const int Max = PortfolioMax + RecordMax;

    /// <summary>How many skills, projects and linked projects the written half counts — the review's levers aim at these.</summary>
    public const int SkillsCounted = 5;
    public const int ProjectsCounted = 6;
    public const int LinksCounted = 4;

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
        new("A headline and an introduction", (p.HasHeadline ? 2 : 0) + (p.HasBio ? 3 : 0), 5,
            "Says what you do before anyone opens your code."),
        // Three is a shape; eight is a list. Rewarding the first few and
        // then stopping keeps this from becoming a keyword dump.
        new("Skills listed", Math.Min(p.Skills, SkillsCounted), SkillsCounted,
            "Up to five count. Naming twenty tools says less than naming five."),
        new("Past work", Math.Min(p.Projects, ProjectsCounted), ProjectsCounted,
            "One point each, for the first six."),
        new("Work anyone can look at", Math.Min(p.ProjectsWithLinks, LinksCounted), LinksCounted,
            "A project with a link or a repository is worth double one without, for the first four."),
        new("Where and when you work", p.HasDetails ? 3 : 0, 3,
            "Location, hours a week, years of experience — what makes a deadline real."),
        // A portal with no GitHub sign-in cannot offer these two to anyone,
        // so they leave the denominator rather than sitting there unearnable —
        // otherwise the completeness bar stops at 90% for everybody, forever.
        // An account that connected before the portal dropped its GitHub App
        // keeps them: the connection is still a fact about that account, and
        // the score must not move under someone for an admin's settings edit.
        new("GitHub connected", p.GithubConnected ? 2 : 0,
            p.GithubOffered || p.GithubConnected ? 2 : 0,
            p.GithubOffered || p.GithubConnected
                ? "Ties this account to a real public identity."
                : "This portal has no GitHub sign-in, so these points are not on offer to anyone here."),
    ];

    public static IReadOnlyList<Part> RecordParts(Record r)
    {
        // Volume stops paying at ten: entering is cheap, and a score that
        // kept paying for it would reward spraying entries at everything.
        var entered = Math.Min(r.Entries * 2, 20);
        // The share of dated milestones met on time, scaled to thirty: ten
        // of ten is the full thirty, eight of ten is twenty-four. A dated
        // milestone is counted once it is claimed or its date has passed
        // (MeritReader), so entering a new opportunity costs nothing here.
        var punctuality = r.MilestonesDated == 0
            ? 0
            : (int)Math.Round(30.0 * r.MilestonesOnTime / r.MilestonesDated, MidpointRounding.AwayFromZero);
        var wins = Math.Min(r.Wins * 5, 20);
        // The stars themselves: the average, to the nearest whole point.
        var average = r.RatingCount == 0 ? 0 : (double)r.RatingSum / r.RatingCount;
        var ratings = (int)Math.Round(average, MidpointRounding.AwayFromZero);

        return
        [
            new("Opportunities entered", entered, 20,
                r.Entries == 0
                    ? "Nothing yet — each entry is worth two."
                    : "Two each, twenty at ten or more; entering is cheap, so this flattens quickly."),
            new("Milestones met on time", punctuality, 30,
                r.MilestonesDated == 0
                    ? "No dated milestone has come due or been claimed yet."
                    : $"{r.MilestonesOnTime} of {r.MilestonesDated} dated milestones claimed on time."),
            new("Opportunities won", wins, 20, "Five each, for the first four."),
            new("Ratings from clients", ratings, 5,
                r.RatingCount == 0
                    ? "Ratings open when an award you won is marked paid."
                    : $"{average.ToString("0.#", CultureInfo.InvariantCulture)} stars on average from "
                        + $"{r.RatingCount} rating{(r.RatingCount == 1 ? "" : "s")}, to the nearest whole point."),
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
