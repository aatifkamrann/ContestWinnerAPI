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
        AiFeature.CategorySuggestion or AiFeature.MilestoneExtraction => Form(feature, input),
        AiFeature.ProfileReview => Review(input),
        AiFeature.EntryDigest => Digest(input),
        _ => throw new ArgumentOutOfRangeException(nameof(feature), feature, "The evals have no builder for that feature."),
    };

    /// <summary>The opportunity form as the screen sends it, read the way the endpoint reads it.</summary>
    private static EvalJob Form(AiFeature feature, JsonElement input)
    {
        var form = input.Deserialize<OpportunityFormSnapshot>(Json)
            ?? throw new InvalidOperationException("the form is empty");
        if (AiInputs.Substance(form) < 30)
            throw new InvalidOperationException("the title and brief are too thin for the endpoint to spend a call on");
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

    /// <summary>The improvement ids a review case hands the model — the ones its answer must copy back.</summary>
    /// <summary>
    /// The total a milestone draft must split: the form's award where the
    /// form is paid by milestone and names one; null otherwise, when the
    /// draft must carry no amounts at all.
    /// </summary>
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
}
