using WinnersPortal.Services.Auth;
using WinnersPortal.Domain;
using WinnersPortal.Services.Profiles;

namespace WinnersPortal.Services.Opportunities;

/// <summary>
/// One freelancer read against one opportunity: may they enter, and is it
/// worth their week. Pure, so the browse page, the opportunity page and the
/// entry door cannot disagree about either answer.
///
/// Two different questions with two different weights. <see cref="CanEnter"/>
/// is the door — the merit minimum and the required skills are terms the
/// client set, and the API refuses an entry short of them. <see cref="Recommended"/>
/// adds the softer signal, whether the opportunity's category is one this
/// person works in — a model's reading of their skills, and the only part
/// of this file that is not arithmetic; a false there costs nothing but a colour on
/// the browse page, because a strong React developer entering a "data"
/// opportunity that lists React is not a mistake anyone should be stopped from.
/// </summary>
public sealed record Fit(
    bool CanEnter,
    bool Recommended,
    bool MeritOk,
    bool SkillsOk,
    bool CategoryOk,
    int MeritScore,
    int MinMerit,
    IReadOnlyList<string> MissingSkills,
    /// <summary>Every reason the answer is not a plain yes, in the entrant's own words.</summary>
    IReadOnlyList<string> Reasons,
    /// <summary>
    /// How well this person matches the brief, 0–100: the weighted average
    /// of the assessment's lines (<see cref="Assessment"/>) — expertise in
    /// the required skills, relevant past work, the on-time record here,
    /// and availability against the timeline. Nought when it is outside
    /// their kind of work, whatever the lines say; 100 when nothing is
    /// asked for and nothing is known.
    /// </summary>
    int Match,
    /// <summary>The assessment's lines, in the order the page shows them.</summary>
    IReadOnlyList<Factor> Factors,
    /// <summary>One sentence for the weakest line, or null when none is weak enough to need one.</summary>
    string? Risk)
{
    /// <summary>"fit", "outside" (may enter, but not their kind of work) or "blocked".</summary>
    public string Verdict => !CanEnter ? "blocked" : Recommended ? "fit" : "outside";

    /// <summary>
    /// The word on the ring: "strong" from 80, "good" from 60, "moderate"
    /// below that, and "outside" for an opportunity outside their kind of work,
    /// whatever the number. It is not the door: a profile short of one
    /// skill in five can read strong and still be turned away, and the
    /// reasons beside the ring say so.
    /// </summary>
    public string MatchBand => !CategoryOk ? "outside"
        : Match >= 80 ? "strong"
        : Match >= 60 ? "good"
        : "moderate";
}

public static class OpportunityFit
{
    public const int MaxSkills = 15;
    public const int MaxSkillName = OpportunitySkill.MaxName;

    /// <summary>The comparison form of a skill name: folded and lower-cased.</summary>
    public static string Key(string? name) => ProfileRules.Fold(name).ToLowerInvariant();

    /// <summary>
    /// The list as stored: tidied, blanks dropped. Null with a message when
    /// it breaks a rule the client should hear about — a skill said twice
    /// is refused rather than folded, for the same reason a profile refuses
    /// it: there is a row to point at.
    /// </summary>
    public static IReadOnlyList<string>? CleanSkills(IEnumerable<string?>? names, out string? problem)
    {
        problem = null;
        var list = (names ?? [])
            .Select(n => ProfileRules.Fold(n))
            .Where(n => n.Length > 0)
            .ToList();
        if (list.Count > MaxSkills)
        {
            problem = $"At most {MaxSkills} required skills — the list is the door, not the brief.";
            return null;
        }
        if (list.Any(n => n.Length > MaxSkillName))
        {
            problem = $"Keep each skill under {MaxSkillName} characters.";
            return null;
        }
        if (ProfileRules.FirstDuplicate(list) is { } twice)
        {
            problem = $"“{twice}” is in the required skills twice — one row each.";
            return null;
        }
        return list;
    }

    public static string? MeritProblem(int? minMerit) =>
        minMerit is < 0 or > Merit.Max
            ? $"The minimum merit score is a number from 0 to {Merit.Max}; zero means no minimum."
            : null;

    /// <summary>
    /// The judgement. <paramref name="workKinds"/> is the model’s reading of
    /// which categories this freelancer works in — <b>null</b> when there is no
    /// reading at all, because AI is off or no provider key is saved. Null is
    /// not "nothing matches": with nothing to judge the affinity by, the
    /// affinity is not judged, and every opportunity they can enter is one they
    /// are recommended. <paramref name="profileSkillKeys"/> is their skills in
    /// <see cref="Key"/> form. <paramref name="facts"/> is what the assessment
    /// reads beyond the door — null stands for the skill keys alone at the
    /// middle level, nothing else known — and the start and the deadline are the
    /// timeline it reads availability against.
    /// </summary>
    public static Fit Judge(
        int minMerit,
        IReadOnlyList<string> requiredSkills,
        string? category,
        int meritScore,
        IReadOnlySet<string> profileSkillKeys,
        IReadOnlySet<string>? workKinds,
        ViewerFacts? facts = null,
        DateTimeOffset? startsAtUtc = null,
        DateTimeOffset? deadlineUtc = null,
        DateTimeOffset? nowUtc = null)
    {
        var meritOk = meritScore >= minMerit;
        var missing = requiredSkills.Where(s => !profileSkillKeys.Contains(Key(s))).ToList();
        var skillsOk = missing.Count == 0;
        // An opportunity that names skills is judged on them: covering the list
        // is a stronger fact than any reading of what somebody usually does.
        // One that names none falls back to the category — and one with no
        // category, or "something else", cannot be outside anybody's work.
        var found = OpportunityCategories.Find(category);
        var categoryOk = workKinds is null
            || requiredSkills.Count > 0
            || found is null
            || !OpportunityCategories.Judgeable(found)
            || workKinds.Contains(found.Key);

        // The match: the assessment's lines, averaged. The category is not
        // a line — it is the whole reading, and an opportunity outside
        // somebody's kind of work matches nought however the lines read.
        var read = Assessment.Read(
            requiredSkills, category, categoryOk,
            facts ?? ViewerFacts.OfSkillKeys(profileSkillKeys),
            startsAtUtc, deadlineUtc, nowUtc ?? DateTimeOffset.UtcNow);

        var reasons = new List<string>();
        if (!meritOk)
            reasons.Add(minMerit >= Merit.Max
                ? $"Needs the full merit score of {Merit.Max} — yours is {meritScore}."
                : $"Needs a merit score of {minMerit} — yours is {meritScore}.");
        if (!skillsOk)
            reasons.Add(missing.Count == 1
                ? $"Your profile does not list {missing[0]} — the client requires it."
                : $"Your profile does not list {Join(missing)} — the client requires them.");
        if (!categoryOk && found is not null)
            reasons.Add($"Nothing on your profile falls under {found.Label} — you can still enter, but it is not your usual work.");

        return new Fit(
            CanEnter: meritOk && skillsOk,
            Recommended: meritOk && skillsOk && categoryOk,
            MeritOk: meritOk,
            SkillsOk: skillsOk,
            CategoryOk: categoryOk,
            MeritScore: meritScore,
            MinMerit: minMerit,
            MissingSkills: missing,
            Reasons: reasons,
            Match: read.Match,
            Factors: read.Factors,
            Risk: read.Risk);
    }

    /// <summary>What the entry door says to somebody it turns away — the hard reasons only.</summary>
    public static string? EntryProblem(Fit fit)
    {
        if (fit.CanEnter) return null;
        var hard = new List<string>();
        if (!fit.MeritOk) hard.Add($"this opportunity needs a merit score of {fit.MinMerit} and yours is {fit.MeritScore}");
        if (!fit.SkillsOk) hard.Add($"it requires {Join(fit.MissingSkills)}, which your profile does not list");
        return "You cannot enter: " + string.Join("; and ", hard)
            + ". Add what is missing to your profile — the score moves with what you write and what you have done here.";
    }

    /// <summary>"React", "React and Postgres", "React, Postgres and Docker".</summary>
    internal static string Join(IReadOnlyList<string> items) => items.Count switch
    {
        0 => string.Empty,
        1 => items[0],
        2 => $"{items[0]} and {items[1]}",
        _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1],
    };
}
