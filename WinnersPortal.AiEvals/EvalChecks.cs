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
        }
    }

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

        if (e.MinCount is not null || e.MaxCount is not null)
        {
            var list = ListName(feature);
            var count = list is not null && root.TryGetProperty(list, out var arr) && arr.ValueKind == JsonValueKind.Array
                ? arr.GetArrayLength()
                : 0;
            if (e.MinCount is { } min && count < min) yield return $"{list ?? "the list"} has {count} items, expected at least {min}";
            if (e.MaxCount is { } max && count > max) yield return $"{list ?? "the list"} has {count} items, expected at most {max}";
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
        _ => null,
    };

    /// <summary>Every string value in the answer, wherever it sits.</summary>
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
