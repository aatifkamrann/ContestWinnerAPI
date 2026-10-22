using System.Text.Json;
using System.Text.Json.Serialization;
using WinnersPortal.Domain;
using WinnersPortal.Services.Ai;
using WinnersPortal.Services.Opportunities;
using WinnersPortal.Services.Profiles;

namespace WinnersPortal.AiEvals;

/// <summary>The prompt a case builds and the canonical input it was built from — the same pair the worker hashes.</summary>
public sealed record EvalJob((string System, string User) Prompt, string CanonicalInput);

/// <summary>
/// The features the golden set covers, by the folder each lives in, and
/// how each folder's input becomes the prompt the portal would send: the
/// same <see cref="AiInputs"/> builder and the same <see cref="AiPrompts"/>
/// wording, so a case scores the prompt as shipped and nothing beside it.
/// </summary>
public static class EvalFeatures
{
    public static readonly IReadOnlyDictionary<string, AiFeature> Slugs = new Dictionary<string, AiFeature>(StringComparer.Ordinal)
    {
        ["categorise"] = AiFeature.CategorySuggestion,
        ["milestones"] = AiFeature.MilestoneExtraction,
        ["profile-review"] = AiFeature.ProfileReview,
        ["entry-digest"] = AiFeature.EntryDigest,
        ["brief-coach"] = AiFeature.BriefCoach,
        ["requirements"] = AiFeature.RequirementsSuggestion,
        ["criteria"] = AiFeature.CriteriaSuggestion,
        ["seo"] = AiFeature.SeoMetadata,
        ["award"] = AiFeature.AwardSuggestion,
        ["work-kinds"] = AiFeature.RecommendedMatching,
        ["profile-summary"] = AiFeature.ProfileSummary,
        ["project-approach"] = AiFeature.ProjectApproach,
        ["application-evaluation"] = AiFeature.ApplicationEvaluation,
        ["progress-narrative"] = AiFeature.ProgressNarrative,
        ["standing-notes"] = AiFeature.StandingNotes,
    };

    public static AiFeature? Parse(string slug) => Slugs.TryGetValue(slug, out var f) ? f : null;

    public static string SlugOf(AiFeature feature) => Slugs.First(p => p.Value == feature).Key;

    public static string Named => string.Join(", ", Slugs.Keys);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static EvalJob Build(AiFeature feature, JsonElement input) => feature switch
    {
        AiFeature.CategorySuggestion or AiFeature.MilestoneExtraction or AiFeature.BriefCoach
            or AiFeature.RequirementsSuggestion or AiFeature.CriteriaSuggestion or AiFeature.SeoMetadata
            or AiFeature.AwardSuggestion => Form(feature, input),
        AiFeature.ProfileReview => Review(input),
        AiFeature.EntryDigest => Digest(input),
        AiFeature.RecommendedMatching => WorkKinds(input),
        AiFeature.ProfileSummary => Summary(input),
        AiFeature.ProjectApproach => Approach(input),
        AiFeature.ApplicationEvaluation => Evaluation(input),
        AiFeature.ProgressNarrative => Narrative(input),
        AiFeature.StandingNotes => Standing(input),
        _ => throw new ArgumentOutOfRangeException(nameof(feature), feature, "The evals have no builder for that feature."),
    };

    /// <summary>The opportunity form as the screen sends it, read the way the endpoint reads it.</summary>
    private static EvalJob Form(AiFeature feature, JsonElement input)
    {
        var form = input.Deserialize<OpportunityFormSnapshot>(Json)
            ?? throw new InvalidOperationException("the form is empty");
        if (AiInputs.Substance(form) < 30)
            throw new InvalidOperationException("the title and brief are too thin for the endpoint to spend a call on");
        // The terms the server puts in for the award suggestion: the minimum
        // award (the setting's default unless the case names one) and the
        // default duration where the deadline is blank.
        if (feature == AiFeature.AwardSuggestion)
            form = form with { DurationDays = form.DurationDays ?? 14, MinimumAwardUsd = AwardFloor(input) };
        return new EvalJob(AiPrompts.ForForm(feature, form), AiInputs.OpportunityForm(form, AiFormReads.Of(feature)));
    }

    /// <summary>The facts the review reads off a profile, with the improvements the arithmetic already decided.</summary>
    private static EvalJob Review(JsonElement input)
    {
        var r = input.Deserialize<ReviewInput>(Json) ?? throw new InvalidOperationException("the profile is empty");
        var facts = new ProfileReview.Facts(
            r.Headline, r.HasAbout, r.PrimaryCategory, r.OtherCategories ?? [],
            (r.Skills ?? []).Select(s => (s.Name, s.Level, s.Years)).ToList(),
            r.Projects, r.ProjectsWithLinks, r.ProjectKinds ?? [], r.HasAvailability, r.HoursPerWeek, r.YearsExperience,
            r.HasPayout, r.StrengthPercent, r.StepsNotDone ?? [], r.OpenOpportunities, r.OpenOpportunitiesEnterable,
            (r.Improvements ?? []).Select(c => new ProfileReview.Candidate(c.Id, c.Change, c.Because, c.Gain)).ToList());
        var json = AiInputs.ProfileReview(facts);
        return new EvalJob(AiPrompts.ProfileReview(json), json);
    }

    /// <summary>The facts the digest job gathers about a frozen entry, and the sendCode switch enforced where the input is built.</summary>
    private static EvalJob Digest(JsonElement input)
    {
        var d = input.Deserialize<DigestInput>(Json) ?? throw new InvalidOperationException("the entry is empty");
        if (d.Facts is null) throw new InvalidOperationException("facts is missing");
        var json = AiInputs.Digest(d.Facts, d.TreePaths, d.Readme, d.SendCode);
        return new EvalJob(AiPrompts.Digest(json), json);
    }

    /// <summary>One freelancer's headline and skills, as the Recommended reading sends them, beside the category list.</summary>
    private static EvalJob WorkKinds(JsonElement input)
    {
        var w = input.Deserialize<WorkKindsInput>(Json) ?? throw new InvalidOperationException("the profile is empty");
        var taxonomy = AiInputs.Taxonomy();
        var profile = AiInputs.WorkKinds(w.Headline, (w.Skills ?? []).Select(s => (s.Name, s.Level, s.Years)));
        return new EvalJob(AiPrompts.WorkKinds(taxonomy, profile), taxonomy + profile);
    }

    /// <summary>The profile form as the About drafter reads it, with the member's side of the market; the endpoint's floor applies.</summary>
    private static EvalJob Summary(JsonElement input)
    {
        var r = input.Deserialize<ProfileSummaryRequest>(Json) ?? throw new InvalidOperationException("the profile is empty");
        var role = input.TryGetProperty("role", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var (json, substance) = AiInputs.ProfileSummary(r, role);
        // AiService.MinTextToRead: below it the endpoint declines to spend a call.
        if (substance < 30)
            throw new InvalidOperationException("the profile is too thin for the endpoint to spend a call on");
        return new EvalJob(AiPrompts.ProfileSummary(json), json);
    }

    /// <summary>The brief and its terms, and the freelancer's skills and fitting past work, as the approach drafter sends them.</summary>
    private static EvalJob Approach(JsonElement input)
    {
        var a = input.Deserialize<ApproachInput>(Json) ?? throw new InvalidOperationException("the application is empty");
        var o = a.Opportunity ?? throw new InvalidOperationException("opportunity is missing");
        var f = a.Freelancer ?? new ApproachFreelancer(null, [], []);
        var json = AiInputs.ProjectApproach(
            o.Title ?? "", o.Brief ?? "", o.KindOfWork, o.RequiredSkills ?? [], o.Milestones ?? [],
            (o.Requirements ?? []).Select(r => (r.Title ?? "", r.Detail ?? "")).ToList(), o.Weeks,
            f.Headline, (f.Skills ?? []).Select(s => (s.Name, s.Level, s.Years)).ToList(),
            (f.RelevantProjects ?? []).Select(p => (p.Title, p.Tech, p.Outcome)).ToList(),
            a.Draft);
        return new EvalJob(AiPrompts.ProjectApproach(json), json);
    }

    /// <summary>The application as filed, with the portal's evaluation lines the model is asked to word.</summary>
    private static EvalJob Evaluation(JsonElement input)
    {
        var e = input.Deserialize<EvaluationInput>(Json) ?? throw new InvalidOperationException("the application is empty");
        var o = e.Opportunity ?? throw new InvalidOperationException("opportunity is missing");
        var ev = e.Evaluation ?? throw new InvalidOperationException("evaluation is missing");
        var app = e.Application ?? throw new InvalidOperationException("application is missing");
        var json = AiInputs.ApplicationEvaluation(
            o.Title ?? "", o.Brief ?? "", o.KindOfWork, o.RequiredSkills ?? [],
            new Evaluation(ev.MatchPercent, ev.Band ?? "Unread", ev.Strengths ?? [], ev.Risks ?? []),
            app.Summary ?? "", app.Approach ?? "", app.Portfolio ?? [], app.Commitment ?? "", app.HoursPerWeek,
            app.ClaimedAdvantages ?? []);
        return new EvalJob(AiPrompts.ApplicationEvaluation(json), json);
    }

    /// <summary>The board's last day, entrants under labels as the narrative job sends them; names never travel.</summary>
    private static EvalJob Narrative(JsonElement input)
    {
        var n = input.Deserialize<NarrativeInput>(Json) ?? throw new InvalidOperationException("the board is empty");
        var (json, _) = AiInputs.Narrative(n.OpportunityTitle ?? "", n.Milestones ?? [], n.Entrants ?? [], n.NowUtc);
        return new EvalJob(AiPrompts.Narrative(json), json);
    }

    /// <summary>The board's rows with their computed standing, entrants under labels in rank order, as the standing job sends them.</summary>
    private static EvalJob Standing(JsonElement input)
    {
        var s = input.Deserialize<StandingInput>(Json) ?? throw new InvalidOperationException("the board is empty");
        if (s.Entrants is not { Count: > 0 }) throw new InvalidOperationException("the board has no entrants to read");
        var (json, _) = AiInputs.Standing(
            s.OpportunityTitle ?? "", s.Status ?? "open", s.Delivery ?? "repository", s.DeadlineUtc,
            s.Milestones ?? [], s.Entrants, s.NowUtc);
        return new EvalJob(AiPrompts.Standing(json), json);
    }

    /// <summary>How many entrant labels (E1, E2 …) a narrative or standing case hands the model.</summary>
    public static int EntrantLabels(AiFeature feature, JsonElement input) => feature switch
    {
        AiFeature.ProgressNarrative => input.Deserialize<NarrativeInput>(Json)?.Entrants?.Count ?? 0,
        AiFeature.StandingNotes => Math.Min(AiInputs.MaxStandingEntrants, input.Deserialize<StandingInput>(Json)?.Entrants?.Count ?? 0),
        _ => 0,
    };

    /// <summary>The entry ids a standing case hands the model — the ones its notes must answer to.</summary>
    public static IReadOnlyList<string> StandingIds(JsonElement input) =>
        (input.Deserialize<StandingInput>(Json)?.Entrants ?? [])
            .OrderBy(e => e.Rank).Take(AiInputs.MaxStandingEntrants)
            .Select(e => e.EntryId.ToString()).ToList();

    /// <summary>The strength and risk ids an evaluation case hands the model.</summary>
    public static (IReadOnlyList<string> Strengths, IReadOnlyList<string> Risks) EvaluationIds(JsonElement input)
    {
        var ev = input.Deserialize<EvaluationInput>(Json)?.Evaluation;
        return ((ev?.Strengths ?? []).Select(l => l.Id).ToList(), (ev?.Risks ?? []).Select(l => l.Id).ToList());
    }

    /// <summary>The improvement ids a review case hands the model — the ones its answer must copy back.</summary>
    /// <summary>
    /// The total a milestone draft must split: the form's award where the
    /// form is paid by milestone and names one; null otherwise, when the
    /// draft must carry no amounts at all.
    /// </summary>
    /// <summary>The portal's minimum award a case is run under: its own <c>portalMinimumAwardUsd</c>, else the setting's default.</summary>
    public static decimal AwardFloor(JsonElement input) =>
        input.TryGetProperty("portalMinimumAwardUsd", out var floor) && floor.ValueKind == JsonValueKind.Number ? floor.GetDecimal() : 100;

    public static decimal? MilestoneBudget(JsonElement input)
    {
        var form = input.Deserialize<OpportunityFormSnapshot>(Json);
        return form is not null && MilestonePay.ParseKind(form.Kind) == OpportunityKind.Milestones && form.AwardAmount is > 0
            ? form.AwardAmount
            : null;
    }

    public static IReadOnlyList<string> ReviewIds(JsonElement input)
    {
        var r = input.Deserialize<ReviewInput>(Json);
        return (r?.Improvements ?? []).Take(ProfileReview.MaxLines).Select(c => c.Id).ToList();
    }

    public sealed record ReviewInput(
        string? Headline,
        bool HasAbout,
        string? PrimaryCategory,
        List<string>? OtherCategories,
        List<ReviewSkill>? Skills,
        int Projects,
        int ProjectsWithLinks,
        List<string>? ProjectKinds,
        bool HasAvailability,
        int? HoursPerWeek,
        int? YearsExperience,
        bool HasPayout,
        int StrengthPercent,
        List<string>? StepsNotDone,
        int OpenOpportunities,
        int OpenOpportunitiesEnterable,
        List<ReviewCandidate>? Improvements);

    public sealed record ReviewSkill(string Name, string Level, int Years);

    public sealed record ReviewCandidate(string Id, string Change, string Because, int Gain);

    public sealed record DigestInput(AiInputs.DigestFacts? Facts, List<string>? TreePaths, string? Readme, bool SendCode);

    public sealed record WorkKindsInput(string? Headline, List<ReviewSkill>? Skills);

    public sealed record ApproachInput(ApproachOpportunity? Opportunity, ApproachFreelancer? Freelancer, string? Draft);

    public sealed record ApproachOpportunity(
        string? Title, string? Brief, string? KindOfWork, List<string>? RequiredSkills, List<string>? Milestones,
        List<FormRequirement>? Requirements, int? Weeks);

    public sealed record ApproachFreelancer(string? Headline, List<ReviewSkill>? Skills, List<ApproachProject>? RelevantProjects);

    public sealed record ApproachProject(string Title, string? Tech, string? Outcome);

    public sealed record EvaluationInput(EvaluationOpportunity? Opportunity, EvaluationLines? Evaluation, EvaluationApplication? Application);

    public sealed record EvaluationOpportunity(string? Title, string? Brief, string? KindOfWork, List<string>? RequiredSkills);

    public sealed record EvaluationLines(int MatchPercent, string? Band, List<EvaluationLine>? Strengths, List<EvaluationLine>? Risks);

    public sealed record EvaluationApplication(
        string? Summary, string? Approach, List<PortfolioItem>? Portfolio, string? Commitment, int HoursPerWeek,
        List<string>? ClaimedAdvantages);

    public sealed record NarrativeInput(
        string? OpportunityTitle, DateTimeOffset NowUtc, List<AiInputs.NarrativeMilestone>? Milestones,
        List<AiInputs.NarrativeEntrant>? Entrants);

    public sealed record StandingInput(
        string? OpportunityTitle, string? Status, string? Delivery, DateTimeOffset? DeadlineUtc, DateTimeOffset NowUtc,
        List<AiInputs.NarrativeMilestone>? Milestones, List<AiInputs.StandingEntrant>? Entrants);
}
