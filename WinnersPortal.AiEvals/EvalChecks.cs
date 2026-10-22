using System.Text.Json;
using System.Text.RegularExpressions;
using WinnersPortal.Domain;
using WinnersPortal.Services.Ai;

namespace WinnersPortal.AiEvals;

/// <summary>
/// What is checked without a model: the validator every answer must pass,
/// the feature's own contract (ids copied back, a focus list of the right
/// length), and the case's expectations. Pure over the canonical output,
/// so the checks are pinned by tests and a run reports exactly what they
/// say.
/// </summary>
public static class EvalChecks
{
    /// <summary>
    /// Every failure in one answer, in plain words; empty means the answer
    /// passed everything but the judge. The raw model text goes through
    /// <see cref="AiOutputs.Validate"/> first, as it would in the portal —
    /// an answer the validator refuses fails here and nothing else is read.
    /// </summary>
    public static (string? Canonical, IReadOnlyList<string> Failures) Run(EvalCase c, string modelText)
    {
        var canonical = AiOutputs.Validate(c.Feature, modelText, out var error);
        if (canonical is null) return (null, [$"validator: {error}"]);

        var failures = new List<string>();
        using var doc = JsonDocument.Parse(canonical);
        var root = doc.RootElement;
        failures.AddRange(BuiltIn(c, root));
        failures.AddRange(Expected(c.Expect, c.Feature, root));
        return (canonical, failures);
    }

    /// <summary>The feature's own contract — what the prompt promises regardless of the case.</summary>
    public static IEnumerable<string> BuiltIn(EvalCase c, JsonElement root)
    {
        switch (c.Feature)
        {
            case AiFeature.ProfileReview:
            {
                var expected = EvalFeatures.ReviewIds(c.Input);
                var got = Ids(root, "improvements");
                foreach (var id in expected.Where(id => !got.Contains(id)))
                    yield return $"improvement {id} was handed in and came back without words";
                foreach (var id in got.Where(id => !expected.Contains(id)))
                    yield return $"improvement {id} was not handed in — an id was invented";
                if (expected.Count > 0 && Texts(root.GetProperty("improvements")).Any(t => Regex.IsMatch(t, @"\d")))
                    yield return "an improvement's words carry a figure; the figures are the portal's arithmetic";
                break;
            }
            case AiFeature.MilestoneExtraction:
            {
                // Paid by milestone, every milestone carries a share and the
                // shares are exactly the client's total; otherwise none does.
                var budget = EvalFeatures.MilestoneBudget(c.Input);
                var amounts = root.GetProperty("milestones").EnumerateArray()
                    .Select(m => m.TryGetProperty("amount", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetDecimal() : (decimal?)null)
                    .ToList();
                if (budget is null && amounts.Any(a => a is not null))
                    yield return "a milestone carries an amount, but the form names no total to split";
                if (budget is { } total)
                {
                    if (amounts.Any(a => a is null))
                        yield return "a milestone has no amount, though the opportunity is paid by milestone";
                    else if (amounts.Sum(a => a!.Value) != total)
                        yield return $"the amounts add up to {amounts.Sum(a => a!.Value):0.##}, not the total {total:0.##}";
                }
                break;
            }
            case AiFeature.EntryDigest:
            {
                var focus = root.TryGetProperty("reviewFocus", out var f) && f.ValueKind == JsonValueKind.Array ? f.GetArrayLength() : 0;
                if (focus is < 2 or > 4) yield return $"reviewFocus has {focus} starting points; the prompt asks for 2 to 4";
                break;
            }
            case AiFeature.BriefCoach:
            {
                var flags = root.GetProperty("flags").GetArrayLength();
                if (flags > 8) yield return $"flags has {flags} items; the prompt allows at most 8";
                break;
            }
            case AiFeature.RequirementsSuggestion:
                foreach (var r in root.GetProperty("requirements").EnumerateArray())
                {
                    var title = Str(r, "title") ?? "";
                    if (Words(title) > 3) yield return $"requirement \"{title}\" is titled in {Words(title)} words; the prompt asks for one to three";
                    if ((Str(r, "detail") ?? "").Length >= 120) yield return $"requirement \"{title}\" has a detail of 120 characters or more";
                }
                break;
            case AiFeature.CriteriaSuggestion:
                foreach (var r in root.GetProperty("criteria").EnumerateArray())
                {
                    var title = Str(r, "title") ?? "";
                    if (Words(title) > 4) yield return $"criterion \"{title}\" is titled in {Words(title)} words; the prompt asks for one to four";
                    if ((Str(r, "description") ?? "").Length >= 120) yield return $"criterion \"{title}\" has a description of 120 characters or more";
                }
                break;
            case AiFeature.AwardSuggestion:
            {
                // What the portal holds the answer to besides the gate: the
                // minimum award, one sentence on the work, each reason a line.
                var floor = EvalFeatures.AwardFloor(c.Input);
                var low = root.GetProperty("low").GetProperty("amountUsd").GetDecimal();
                if (low < floor) yield return $"the low figure is {low:0}, under the portal's minimum award of {floor:0}";
                var effort = Str(root, "effort") ?? "";
                if (Sentences(effort) > 1) yield return $"effort has {Sentences(effort)} sentences; the prompt asks for one";
                foreach (var tier in AiOutputs.AwardTiers)
                {
                    var why = Str(root.GetProperty(tier), "why");
                    if (why is null) yield return $"the {tier} figure came back without a reason";
                    else if (why.Length >= 140) yield return $"the {tier} reason is {why.Length} characters; the prompt asks for under 140";
                }
                break;
            }
            case AiFeature.SeoMetadata:
            {
                var title = Str(root, "title") ?? "";
                var description = Str(root, "description") ?? "";
                if (title.Length > 60) yield return $"the title is {title.Length} characters; the prompt allows 60";
                if (description.Length > 155) yield return $"the description is {description.Length} characters; the prompt allows 155";
                break;
            }
            case AiFeature.ProfileSummary:
            {
                var summary = Str(root, "summary") ?? "";
                if (summary.Length >= 600) yield return $"the summary is {summary.Length} characters; the prompt asks for under 600";
                var sentences = Sentences(summary);
                if (sentences is < 2 or > 4) yield return $"the summary has {sentences} sentences; the prompt asks for 2 to 4";
                break;
            }
            case AiFeature.ProjectApproach:
            {
                var approach = Str(root, "approach") ?? "";
                if (approach.Length is < 400 or > 900) yield return $"the approach is {approach.Length} characters; the prompt asks for 400 to 900";
                if (Regex.IsMatch(approach, @"(?m)^\s*#")) yield return "the approach carries a heading; the prompt asks for none";
                break;
            }
            case AiFeature.ApplicationEvaluation:
            {
                var (strengths, risks) = EvalFeatures.EvaluationIds(c.Input);
                foreach (var f in CopiedBack("strength", strengths, Ids(root, "strengths"))) yield return f;
                foreach (var f in CopiedBack("risk", risks, Ids(root, "risks"))) yield return f;
                if (Texts(root).Any(t => Regex.IsMatch(t, @"\d|%")))
                    yield return "the evaluation's words carry a figure; the figures are the portal's arithmetic";
                if (Str(root, "note") is null) yield return "the evaluation has no note";
                break;
            }
            case AiFeature.ProgressNarrative:
            {
                foreach (var f in Labels(c, root)) yield return f;
                var sentences = Sentences(Str(root, "narrative") ?? "");
                if (sentences is < 2 or > 4) yield return $"the narrative has {sentences} sentences; the prompt asks for 2 to 4";
                break;
            }
            case AiFeature.StandingNotes:
            {
                foreach (var f in Labels(c, root)) yield return f;
                var rows = root.GetProperty("entrants").EnumerateArray().ToList();
                var got = rows.Select(r => Str(r, "entryId") ?? "").ToList();
                foreach (var f in CopiedBack("entrant", EvalFeatures.StandingIds(c.Input), got.ToHashSet(StringComparer.OrdinalIgnoreCase)))
                    yield return f;
                foreach (var id in got.GroupBy(i => i, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                    yield return $"entrant {id.Key} has {id.Count()} notes; the prompt asks for one each";
                foreach (var r in rows.Where(r => !r.TryGetProperty("check", out var k) || k.GetArrayLength() == 0))
                    yield return $"entrant {Str(r, "entryId")} has nothing to check; the prompt asks for 1 to 3";
                if (Str(root, "summary") is null) yield return "the notes have no summary of the board";
                break;
            }
        }
    }

    /// <summary>Every id handed in comes back, and none comes back that was not.</summary>
    private static IEnumerable<string> CopiedBack(string what, IReadOnlyList<string> expected, HashSet<string> got)
    {
        foreach (var id in expected.Where(id => !got.Contains(id)))
            yield return $"{what} {id} was handed in and came back without words";
        foreach (var id in got.Where(id => !expected.Contains(id, StringComparer.OrdinalIgnoreCase)))
            yield return $"{what} {id} was not handed in — an id was invented";
    }

    private static readonly Regex EntrantLabel = new(@"(?<![\p{L}\p{N}])E[1-9]\d{0,2}(?![\p{L}\p{N}])");

    /// <summary>The model may name entrants only by the labels it was handed; E9 on a board of three is an invented entrant.</summary>
    private static IEnumerable<string> Labels(EvalCase c, JsonElement root)
    {
        var handed = EvalFeatures.EntrantLabels(c.Feature, c.Input);
        foreach (var label in Texts(root).SelectMany(t => EntrantLabel.Matches(t)).Select(m => m.Value).Distinct())
            if (int.Parse(label[1..]) > handed)
                yield return $"entrant {label} was not handed in — the board has {handed}";
    }

    /// <summary>
    /// Sentences as a reader counts them: an end of sentence followed by a
    /// capital or the end of the text, plus a last one left without a stop.
    /// "e.g. delivery" and "Next.js" end nothing.
    /// </summary>
    public static int Sentences(string text)
    {
        var t = text.Trim();
        var ends = Regex.Matches(t, @"[.!?](?=\s+[\p{Lu}\p{N}""“(]|[""”)]*$)").Count;
        return ends + (t.Length > 0 && !Regex.IsMatch(t, @"[.!?][""”)]*$") ? 1 : 0);
    }

    private static int Words(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>The case's expectations against the answer.</summary>
    public static IEnumerable<string> Expected(Expectation e, AiFeature feature, JsonElement root)
    {
        if (e.Category is not null)
        {
            var got = Str(root, "category");
            if (!string.Equals(got, e.Category, StringComparison.Ordinal))
                yield return $"category is {got ?? "missing"}, expected {e.Category}";
        }
        if (e.SubcategoryGiven)
        {
            var got = Str(root, "subcategory");
            if (!string.Equals(got, e.Subcategory, StringComparison.Ordinal))
                yield return $"subcategory is {got ?? "none"}, expected {e.Subcategory ?? "none"}";
        }
        if (e.Confident is { } confident)
        {
            var got = root.TryGetProperty("confident", out var cv) && cv.ValueKind == JsonValueKind.True;
            if (got != confident) yield return $"confident is {got.ToString().ToLowerInvariant()}, expected {confident.ToString().ToLowerInvariant()}";
        }

        if (e.Categories.Count > 0 || e.NotCategories.Count > 0)
        {
            var got = root.TryGetProperty("categories", out var cats) && cats.ValueKind == JsonValueKind.Array
                ? cats.EnumerateArray().Select(v => v.GetString() ?? "").ToHashSet(StringComparer.Ordinal)
                : [];
            foreach (var key in e.Categories.Where(k => !got.Contains(k)))
                yield return $"categories leaves out {key}";
            foreach (var key in e.NotCategories.Where(got.Contains))
                yield return $"categories names {key}, which this profile does not cover";
        }

        if (e.MinCount is not null || e.MaxCount is not null)
        {
            var list = ListName(feature);
            var count = list is not null && root.TryGetProperty(list, out var arr) && arr.ValueKind == JsonValueKind.Array
                ? arr.GetArrayLength()
                : 0;
            if (e.MinCount is { } min && count < min) yield return $"{list ?? "the list"} has {count} items, expected at least {min}";
            if (e.MaxCount is { } max && count > max) yield return $"{list ?? "the list"} has {count} items, expected at most {max}";
        }

        if (e.CompetitiveBetween is { } range && root.TryGetProperty("competitive", out var competitive)
            && competitive.ValueKind == JsonValueKind.Object && competitive.TryGetProperty("amountUsd", out var amount))
        {
            var got = amount.GetDecimal();
            if (got < range.Low || got > range.High)
                yield return $"the competitive figure is {got:0}, expected {range.Low:0} to {range.High:0}";
        }

        var texts = Texts(root).ToList();
        foreach (var group in e.MentionsAny)
            if (!texts.Any(t => group.Any(w => ContainsWord(t, w))))
                yield return $"nothing in the answer mentions {string.Join(" / ", group)}";
        foreach (var word in e.ForbidWords)
        {
            var hit = texts.FirstOrDefault(t => ContainsWord(t, word));
            if (hit is not null) yield return $"the answer says \"{word}\": {Excerpt(hit)}";
        }
        if (e.ForbidPattern is not null)
        {
            var re = new Regex(e.ForbidPattern, RegexOptions.IgnoreCase);
            var hit = texts.FirstOrDefault(t => re.IsMatch(t));
            if (hit is not null) yield return $"the answer matches /{e.ForbidPattern}/: {Excerpt(hit)}";
        }
    }

    /// <summary>The list a count bounds, per feature.</summary>
    public static string? ListName(AiFeature feature) => feature switch
    {
        AiFeature.MilestoneExtraction => "milestones",
        AiFeature.ProfileReview => "improvements",
        AiFeature.EntryDigest => "reviewFocus",
        AiFeature.BriefCoach => "flags",
        AiFeature.RequirementsSuggestion => "requirements",
        AiFeature.CriteriaSuggestion => "criteria",
        AiFeature.RecommendedMatching => "categories",
        AiFeature.StandingNotes => "entrants",
        _ => null,
    };

    /// <summary>
    /// Every string value in the answer, wherever it sits, except the ids it
    /// copies back: an id is the portal's own label, not the model's words,
    /// so "about" or "skill:docker" must never satisfy a word check.
    /// </summary>
    public static IEnumerable<string> Texts(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.String:
                yield return e.GetString() ?? "";
                break;
            case JsonValueKind.Array:
                foreach (var item in e.EnumerateArray())
                    foreach (var t in Texts(item)) yield return t;
                break;
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                    if (p.Name is not ("id" or "entryId"))
                        foreach (var t in Texts(p.Value)) yield return t;
                break;
        }
    }

    /// <summary>
    /// A whole word, case-insensitive; a phrase matches as a phrase. The
    /// boundary is asked for only where the word itself starts or ends in
    /// a letter or digit — "%" and "$" sit against a number by nature.
    /// </summary>
    public static bool ContainsWord(string text, string word)
    {
        var pattern = Regex.Escape(word);
        if (char.IsLetterOrDigit(word[0])) pattern = @"(?<![\p{L}\p{N}])" + pattern;
        if (char.IsLetterOrDigit(word[^1])) pattern += @"(?![\p{L}\p{N}])";
        return Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase);
    }

    private static string? Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static HashSet<string> Ids(JsonElement root, string list)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (root.TryGetProperty(list, out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var item in arr.EnumerateArray())
                if (Str(item, "id") is { } id) ids.Add(id);
        return ids;
    }

    private static string Excerpt(string text) =>
        "“" + (text.Length <= 90 ? text : text[..90].TrimEnd() + "…") + "”";
}
