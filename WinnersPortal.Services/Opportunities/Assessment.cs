using WinnersPortal.Services.Auth;
using WinnersPortal.Services.Profiles;
using WinnersPortal.Domain;

namespace WinnersPortal.Services.Opportunities;

/// <summary>
/// One line of the assessment: what was read, a number out of 100, and the
/// sentence under it. A null score is a line the portal cannot read yet —
/// no dated milestone has come due, nothing said about availability — and
/// it leaves the average rather than counting as nought, so two members are
/// compared on what is actually known about both.
/// </summary>
public sealed record Factor(string Key, string Label, int? Score, string Detail);

/// <summary>
/// One past project as the assessment reads it: its kind of work, and its
/// words in comparison form (see <see cref="Assessment.Words"/>).
/// </summary>
public sealed record ProjectFacts(string? Category, string Text)
{
    /// <summary>A stored project's facts: all of its own words, compared as one text.</summary>
    public static ProjectFacts Of(
        string? category, string? title, string? description, string? outcome, string? role, string? tech) =>
        new(category, Assessment.Words(title, description, outcome, role, tech));
}

/// <summary>
/// One past project as a screen shows it beside an opportunity: the name and the
/// outcome to show, and the facts to judge it by.
/// </summary>
public sealed record PastWork(string Title, string? Outcome, ProjectFacts Facts);

/// <summary>
/// What the assessment reads about one freelancer beyond the door's facts:
/// their skills with a level and years each, their past work, when and how
/// much they can work, their on-time record here, and how many opportunities
/// they are already building for.
/// </summary>
public sealed record ViewerFacts(
    IReadOnlyDictionary<string, (SkillLevel Level, int Years)> Skills,
    IReadOnlyList<ProjectFacts> Projects,
    Availability? Availability,
    int? HoursPerWeek,
    /// <summary>Dated milestones on their entries that have come due or been claimed.</summary>
    int MilestonesDue,
    int MilestonesOnTime,
    /// <summary>Open opportunities they are already entered in.</summary>
    int OpportunitiesInHand)
{
    /// <summary>
    /// Skill names alone, each at the middle level because a bare name says
    /// nothing about depth — what a caller with only the keys hands in.
    /// </summary>
    public static ViewerFacts OfSkillKeys(IEnumerable<string> keys) => new(
        keys.Distinct(StringComparer.Ordinal).ToDictionary(k => k, _ => (SkillLevel.Intermediate, 0), StringComparer.Ordinal),
        [], null, null, 0, 0, 0);
}

/// <summary>
/// The assessment on the opportunity page's entry box: four lines, each a
/// number out of 100 with the reason under it, their weighted average as
/// the match, and one sentence for the weakest of them. All of it is
/// arithmetic over what the profile says and what the portal has watched
/// happen — no model is asked — so the page can show every line and a
/// member can see exactly which one to move.
///
/// The lines: expertise in the required skills (the level and years on the
/// profile for each, nought for one not listed), relevant project history
/// (past work filed under this kind of work or mentioning a required
/// skill), the on-time delivery record here, and availability against the
/// timeline (when they can start, hours a week, and how many opportunities they
/// are already in). Skills weigh twice the rest, because they are what the
/// client asked for by name.
/// </summary>
public static class Assessment
{
    public const int SkillsWeight = 40;
    public const int ProjectsWeight = 20;
    public const int RecordWeight = 20;
    public const int AvailabilityWeight = 20;

    /// <summary>A line at or above this needs no sentence of advice.</summary>
    public const int StrongLine = 85;

    public sealed record Result(IReadOnlyList<Factor> Factors, int Match, string? Risk);

    private static readonly char[] Separators = [' ', ',', ';', '/', '(', ')', '[', ']', '"', '\n', '\r', '\t'];

    /// <summary>
    /// The reading. <paramref name="categoryOk"/> is the door's word on
    /// whether this is their kind of work at all — outside it, the match is
    /// nought whatever the lines say, as it always was.
    /// </summary>
    public static Result Read(
        IReadOnlyList<string> requiredSkills,
        string? category,
        bool categoryOk,
        ViewerFacts f,
        DateTimeOffset? startsAtUtc,
        DateTimeOffset? deadlineUtc,
        DateTimeOffset nowUtc)
    {
        var weeks = TimelineWeeks(startsAtUtc ?? nowUtc, deadlineUtc);
        var skills = SkillsLine(requiredSkills, f, out var weakest);
        var projects = ProjectsLine(requiredSkills, category, f, out var relevant);
        var record = RecordLine(f);
        var availability = AvailabilityLine(f, weeks);

        var weighted = new[]
        {
            (Factor: skills, Weight: SkillsWeight),
            (Factor: projects, Weight: ProjectsWeight),
            (Factor: record, Weight: RecordWeight),
            (Factor: availability, Weight: AvailabilityWeight),
        };
        var scored = weighted.Where(x => x.Factor.Score is not null).ToList();
        // Nothing asked for and nothing known is nothing short of.
        var match = !categoryOk ? 0
            : scored.Count == 0 ? 100
            : (int)Math.Round(
                (double)scored.Sum(x => x.Weight * x.Factor.Score!.Value) / scored.Sum(x => x.Weight),
                MidpointRounding.AwayFromZero);

        var lowest = scored.Count == 0 ? null : scored.MinBy(x => x.Factor.Score)!.Factor;
        var risk = lowest is null || lowest.Score >= StrongLine
            ? null
            : RiskOf(lowest, weakest, relevant, f, weeks);

        return new Result(weighted.Select(x => x.Factor).ToList(), match, risk);
    }

    /// <summary>Whole weeks from one moment to the deadline, rounded up; null with no deadline or one already passed.</summary>
    public static int? TimelineWeeks(DateTimeOffset fromUtc, DateTimeOffset? deadlineUtc)
    {
        if (deadlineUtc is null) return null;
        var days = (deadlineUtc.Value - fromUtc).TotalDays;
        return days <= 0 ? null : Math.Max(1, (int)Math.Ceiling(days / 7));
    }

    /// <summary>
    /// Free text in comparison form: folded, lower-cased, one space between
    /// words and one at each end, so a skill can be looked for as whole
    /// words — "c" must not be found inside "react".
    /// </summary>
    public static string Words(params string?[] texts) =>
        " " + string.Join(" ", string.Join(" ", texts.Where(t => !string.IsNullOrWhiteSpace(t)))
            .ToLowerInvariant()
            .Split(Separators, StringSplitOptions.RemoveEmptyEntries)) + " ";

    /// <summary>The level's worth, with up to five years on top: expert is the ceiling.</summary>
    public static int SkillScore(SkillLevel level, int years) =>
        Math.Min(100, level switch
        {
            SkillLevel.Beginner => 55,
            SkillLevel.Intermediate => 75,
            SkillLevel.Advanced => 90,
            _ => 100,
        } + Math.Min(Math.Max(years, 0), 5) * 2);

    // ------------------------------------------------------------ the lines

    private static Factor SkillsLine(
        IReadOnlyList<string> required, ViewerFacts f, out (string Name, SkillLevel? Level) weakest)
    {
        weakest = default;
        if (required.Count == 0)
            return new("skills", "Expertise in the required skills", null, "The brief names no required skills.");

        var rows = required.Select(name =>
        {
            var has = f.Skills.TryGetValue(OpportunityFit.Key(name), out var s);
            return (Name: name, Level: has ? s.Level : (SkillLevel?)null, s.Years, Score: has ? SkillScore(s.Level, s.Years) : 0);
        }).ToList();
        var score = (int)Math.Round(rows.Average(r => r.Score), MidpointRounding.AwayFromZero);
        var low = rows.MinBy(r => r.Score);
        weakest = (low.Name, low.Level);

        var listed = rows.Where(r => r.Level is not null).ToList();
        var missing = rows.Where(r => r.Level is null).Select(r => r.Name).ToList();
        string detail;
        if (listed.Count == 0)
            detail = required.Count == 1
                ? $"{required[0]} is not on your profile."
                : "None of the required skills is on your profile.";
        else
        {
            var parts = listed.Select(r => required.Count == 1
                ? $"{LevelWord(r.Level!.Value)}{Years(r.Years)} on your profile"
                : $"{r.Name} {LevelWord(r.Level!.Value)}{Years(r.Years)}");
            detail = Capitalise(string.Join("; ", parts))
                + (missing.Count == 0 ? "." : $"; {OpportunityFit.Join(missing)} not listed.");
        }
        var label = required.Count == 1 ? $"{required[0]} expertise" : "Expertise in the required skills";
        return new("skills", label, score, detail);
    }

    /// <summary>
    /// Whether a past project counts towards a brief: filed under its kind of
    /// work, or mentioning a skill it requires. Null when the brief names
    /// neither, so there is nothing to match past work against. The project
    /// line counts with it and the welcome screen picks the work it shows
    /// under the strongest opportunity with it, so the two cannot disagree.
    /// </summary>
    public static Func<ProjectFacts, bool>? RelevantTo(IReadOnlyList<string> required, string? category)
    {
        var key = OpportunityCategories.Find(category)?.Key;
        var skillWords = required.Select(s => Words(s)).ToList();
        if (key is null && skillWords.Count == 0) return null;
        return p => (key is not null && string.Equals(p.Category, key, StringComparison.OrdinalIgnoreCase))
            || skillWords.Any(w => p.Text.Contains(w, StringComparison.Ordinal));
    }

    /// <summary>
    /// The past work to show beside an opportunity: the first project, in the
    /// member's own order, that <see cref="RelevantTo"/> counts and that says
    /// what came of it. Null when none does; a fitting project with no
    /// outcome is already counted in the project line and has nothing to
    /// add. The welcome screen shows a member theirs beside the strongest
    /// opportunity, and a client's entrant rows show each entrant's.
    /// </summary>
    public static PastWork? FittingWork(
        IEnumerable<PastWork> portfolio, IReadOnlyList<string> requiredSkills, string? category)
    {
        var fits = RelevantTo(requiredSkills, category);
        return fits is null
            ? null
            : portfolio.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.Outcome) && fits(p.Facts));
    }

    private static Factor ProjectsLine(
        IReadOnlyList<string> required, string? category, ViewerFacts f, out int relevant)
    {
        const string label = "Relevant project history";
        relevant = 0;
        // A brief that names neither a kind of work nor a skill gives
        // nothing to match past work against, so the line is not read.
        var fits = RelevantTo(required, category);
        if (fits is null)
            return new("projects", label, null, "The brief names no kind of work to match your past work against.");
        var n = f.Projects.Count;
        if (n == 0) return new("projects", label, 0, "No past work on your profile yet.");

        relevant = f.Projects.Count(fits);
        var score = relevant switch { 0 => 0, 1 => 60, 2 => 80, _ => 100 };
        var projects = n == 1 ? "project" : "projects";
        var detail = relevant == 0
            ? $"None of your {n} {projects} mentions this kind of work."
            : $"{relevant} of your {n} {projects} use{(relevant == 1 ? "s" : "")} what this brief asks for.";
        return new("projects", label, score, detail);
    }

    private static Factor RecordLine(ViewerFacts f)
    {
        const string label = "On-time delivery record";
        if (f.MilestonesDue == 0)
            return new("record", label, null,
                "No dated milestone has come due for you yet — the record starts with your first.");
        var score = (int)Math.Round(100.0 * f.MilestonesOnTime / f.MilestonesDue, MidpointRounding.AwayFromZero);
        return new("record", label, score,
            $"{f.MilestonesOnTime} of {f.MilestonesDue} dated milestone{(f.MilestonesDue == 1 ? "" : "s")} claimed on time.");
    }

    private static Factor AvailabilityLine(ViewerFacts f, int? weeks)
    {
        const string label = "Availability in timeline";
        if (f.Availability is null && f.HoursPerWeek is null)
            return new("availability", label, null, "Say when you can start and your hours a week on your profile.");

        double score = f.Availability switch
        {
            Availability.Now => 100,
            Availability.WithinOneWeek => 85,
            Availability.WithinTwoWeeks => 70,
            Availability.Unavailable => 15,
            _ => 80,
        };
        // A late start costs more of a short timeline than of a long one.
        if (weeks is { } w)
        {
            if (f.Availability == Availability.WithinOneWeek && w <= 2) score -= 20;
            if (f.Availability == Availability.WithinTwoWeeks) score -= w <= 2 ? 40 : w <= 4 ? 20 : 0;
        }
        score *= f.HoursPerWeek switch
        {
            null => 0.9,
            >= 30 => 1.0,
            >= 20 => 0.9,
            >= 10 => 0.75,
            _ => 0.55,
        };
        score -= 8 * f.OpportunitiesInHand;
        var n = (int)Math.Round(Math.Clamp(score, 5, 100), MidpointRounding.AwayFromZero);

        var parts = new List<string>();
        if (f.Availability is { } a) parts.Add(AvailabilityWord(a));
        if (f.HoursPerWeek is { } h) parts.Add($"{h} hours a week");
        var detail = string.Join(", ", parts)
            + (weeks is { } wk ? $" for a {wk}-week timeline" : "")
            + (f.OpportunitiesInHand > 0
                ? $"; {f.OpportunitiesInHand} other {(f.OpportunitiesInHand == 1 ? "opportunity" : "opportunities")} in hand."
                : ".");
        return new("availability", label, n, Capitalise(detail));
    }

    // ------------------------------------------------------------- the risk

    private static string RiskOf(
        Factor lowest, (string Name, SkillLevel? Level) weakest, int relevant, ViewerFacts f, int? weeks)
    {
        var timeline = weeks is { } w ? $"a {w}-week timeline" : "this timeline";
        return lowest.Key switch
        {
            "skills" => weakest.Level is null
                ? $"{weakest.Name} is not on your profile, and the client requires it."
                : $"Your {weakest.Name} is listed as {LevelWord(weakest.Level.Value)}, lighter than the brief asks for; "
                    + "say what you have built with it in your note.",
            "projects" => relevant == 0
                ? "Nothing on your profile shows this kind of work; describe a relevant project in your note."
                : $"Only {relevant} of your projects show{(relevant == 1 ? "s" : "")} this kind of work; "
                    + "name what you built there in your note.",
            "record" => $"Your on-time record is {lowest.Score}%; a milestone plan in your note will reassure the client.",
            _ => f.Availability == Availability.Unavailable
                ? "Your profile says you are not taking work on; update it before you apply."
                : f.HoursPerWeek is { } h and < 20
                    ? $"{h} hours a week is thin for {timeline}; confirm your hours in your note."
                    : f.Availability is Availability.WithinOneWeek or Availability.WithinTwoWeeks
                        ? $"Starting {(f.Availability == Availability.WithinOneWeek ? "within a week" : "within two weeks")} "
                            + $"eats into {timeline}; say when you can begin in your note."
                        : f.OpportunitiesInHand > 0
                            ? $"{f.OpportunitiesInHand} other {(f.OpportunitiesInHand == 1 ? "opportunity" : "opportunities")} in hand will compete "
                                + "for the same weeks; say how this fits in, in your note."
                            : "Your availability looks thin for this timeline; confirm your hours in your note.",
        };
    }

    // ------------------------------------------------------------ the words

    private static string LevelWord(SkillLevel level) => level switch
    {
        SkillLevel.Beginner => "beginner",
        SkillLevel.Intermediate => "intermediate",
        SkillLevel.Advanced => "advanced",
        _ => "expert",
    };

    private static string AvailabilityWord(Availability a) => a switch
    {
        Availability.Now => "available now",
        Availability.WithinOneWeek => "free within a week",
        Availability.WithinTwoWeeks => "free within two weeks",
        _ => "not taking work on",
    };

    private static string Years(int years) => years switch { <= 0 => "", 1 => ", 1 year", _ => $", {years} years" };

    private static string Capitalise(string s) =>
        s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
