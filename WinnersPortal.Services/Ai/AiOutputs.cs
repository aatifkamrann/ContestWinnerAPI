using WinnersPortal.Services.Opportunities;
using WinnersPortal.Services.Profiles;
using WinnersPortal.Domain;
using System.Text.Json;
using WinnersPortal.Services.Settings;

namespace WinnersPortal.Services.Ai;

/// <summary>
/// The gate between a model's answer and anything a person sees. Each
/// feature's output is parsed against its contract, coerced where harmless
/// (clipped lengths, defaulted severities), rejected where not — and only
/// the canonical re-serialisation is ever stored. Raw model text never
/// reaches the database or the browser.
/// </summary>
public static class AiOutputs
{
    /// <summary>Canonical JSON on success; null with a plain-words error otherwise.</summary>
    public static string? Validate(AiFeature feature, string modelText, out string? error)
    {
        error = null;
        var json = AiRules.ExtractJson(modelText);
        if (json is null)
        {
            error = "The model's answer contained no JSON object.";
            return null;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            error = "The model's answer was not valid JSON.";
            return null;
        }

        using (doc)
        {
            try
            {
                return feature switch
                {
                    AiFeature.MilestoneExtraction => Milestones(doc.RootElement, out error),
                    AiFeature.BriefCoach => Coach(doc.RootElement, out error),
                    AiFeature.EntryDigest => Digest(doc.RootElement, out error),
                    AiFeature.ProgressNarrative => Narrative(doc.RootElement, out error),
                    AiFeature.SeoMetadata => Seo(doc.RootElement, out error),
                    AiFeature.StandingNotes => StandingNotes(doc.RootElement, out error),
                    AiFeature.CategorySuggestion => Categorise(doc.RootElement, out error),
                    AiFeature.RecommendedMatching => WorkKinds(doc.RootElement, out error),
                    AiFeature.ProfileSummary => ProfileSummary(doc.RootElement, out error),
                    AiFeature.ProfileReview => ProfileReview(doc.RootElement, out error),
                    AiFeature.ProjectApproach => ProjectApproach(doc.RootElement, out error),
                    AiFeature.ApplicationEvaluation => ApplicationEvaluation(doc.RootElement, out error),
                    AiFeature.RequirementsSuggestion => Requirements(doc.RootElement, out error),
                    AiFeature.CriteriaSuggestion => Criteria(doc.RootElement, out error),
                    _ => Fail("This feature has no provider output.", out error),
                };
            }
            catch (InvalidOperationException)
            {
                error = "The model's answer did not match the expected shape.";
                return null;
            }
        }
    }

    private static string? Fail(string message, out string? error)
    {
        error = message;
        return null;
    }

    private static string? Milestones(JsonElement root, out string? error)
    {
        error = null;
        var items = new List<object>();
        if (root.TryGetProperty("milestones", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in arr.EnumerateArray())
            {
                var title = Str(m, "title");
                if (string.IsNullOrWhiteSpace(title)) continue;
                items.Add(new
                {
                    title = AiRules.Clip(title, 200),
                    description = NullIfEmpty(AiRules.Clip(Str(m, "description"), 300)),
                });
                if (items.Count == 20) break;
            }
        }
        if (items.Count == 0) return Fail("The model proposed no usable milestones.", out error);
        return JsonSerializer.Serialize(new { milestones = items });
    }

    private static string? Coach(JsonElement root, out string? error)
    {
        error = null;
        var items = new List<object>();
        if (root.TryGetProperty("flags", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in arr.EnumerateArray())
            {
                var issue = Str(f, "issue");
                if (string.IsNullOrWhiteSpace(issue)) continue;
                var severity = Str(f, "severity");
                items.Add(new
                {
                    severity = severity == "warn" ? "warn" : "info",
                    issue = AiRules.Clip(issue, 300),
                    suggestion = NullIfEmpty(AiRules.Clip(Str(f, "suggestion"), 400)),
                });
                if (items.Count == 12) break;
            }
        }
        // Zero flags is a legitimate verdict: the brief reads clean.
        return JsonSerializer.Serialize(new { flags = items });
    }

    private static string? Digest(JsonElement root, out string? error)
    {
        error = null;
        var summary = Str(root, "summary");
        if (string.IsNullOrWhiteSpace(summary))
            return Fail("The digest came back without a summary.", out error);
        return JsonSerializer.Serialize(new
        {
            summary = AiRules.Clip(summary, 800),
            stack = StrList(root, "stack", 12, 60),
            milestoneCoverage = NullIfEmpty(AiRules.Clip(Str(root, "milestoneCoverage"), 400)),
            quality = NullIfEmpty(AiRules.Clip(Str(root, "quality"), 400)),
            reviewFocus = StrList(root, "reviewFocus", 6, 200),
        });
    }

    private static string? Narrative(JsonElement root, out string? error)
    {
        error = null;
        var narrative = Str(root, "narrative");
        if (string.IsNullOrWhiteSpace(narrative))
            return Fail("The narrative came back empty.", out error);
        return JsonSerializer.Serialize(new { narrative = AiRules.Clip(narrative, 900) });
    }

    private static string? Seo(JsonElement root, out string? error)
    {
        error = null;
        var title = Str(root, "title");
        var description = Str(root, "description");
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(description))
            return Fail("The metadata draft needs both a title and a description.", out error);
        // Hard cuts, no marker: these land verbatim in fields with the same caps.
        title = title.Trim();
        description = description.Trim();
        return JsonSerializer.Serialize(new
        {
            title = title.Length <= 80 ? title : title[..80].TrimEnd(),
            description = description.Length <= 200 ? description : description[..200].TrimEnd(),
        });
    }

    private static string? StandingNotes(JsonElement root, out string? error)
    {
        error = null;
        var items = new List<object>();
        if (root.TryGetProperty("entrants", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in arr.EnumerateArray())
            {
                // A note that names no real entry cannot be shown against
                // one — the page joins notes to rows by this id.
                var note = Str(e, "note");
                if (!Guid.TryParse(Str(e, "entryId"), out var entryId) || string.IsNullOrWhiteSpace(note)) continue;
                items.Add(new
                {
                    entryId,
                    note = AiRules.Clip(note, 600),
                    check = StrList(e, "check", 3, 160),
                });
                if (items.Count == 60) break;
            }
        }
        var summary = NullIfEmpty(AiRules.Clip(Str(root, "summary"), 700));
        if (items.Count == 0 && summary is null)
            return Fail("The notes came back without a single readable row.", out error);
        return JsonSerializer.Serialize(new { summary, entrants = items });
    }


    /// <summary>
    /// A category the portal does not have is not a near miss to be mapped —
    /// it is a wrong answer, and the client is better served by no suggestion
    /// than by one that puts their opportunity where nobody browses.
    /// </summary>
    private static string? Categorise(JsonElement root, out string? error)
    {
        error = null;
        var key = Opportunities.OpportunityCategories.CleanKey(Str(root, "category"));
        var category = Opportunities.OpportunityCategories.Find(key);
        if (category is null)
            return Fail("The model named a category this portal does not have.", out error);

        // A subcategory under the wrong parent is dropped rather than
        // refused: the category it chose is still worth offering.
        var sub = Opportunities.OpportunityCategories.FindSub(
            category, Opportunities.OpportunityCategories.CleanKey(Str(root, "subcategory")));

        return JsonSerializer.Serialize(new
        {
            category = category.Key,
            subcategory = sub?.Key,
            because = NullIfEmpty(AiRules.Clip(Str(root, "because"), 160)),
            confident = !root.TryGetProperty("confident", out var c) || c.ValueKind != JsonValueKind.False,
        });
    }

    /// <summary>
    /// The kinds one freelancer works in. Unknown keys are dropped, not
    /// refused: a reading that names four real categories and one invented
    /// one is still four useful facts. An empty list survives — a profile
    /// with nothing on it genuinely covers nothing.
    /// </summary>
    private static string? WorkKinds(JsonElement root, out string? error)
    {
        error = null;
        if (!root.TryGetProperty("categories", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return Fail("The reading came back without a list of categories.", out error);

        var keys = arr.EnumerateArray()
            .Where(v => v.ValueKind == JsonValueKind.String)
            .Select(v => Opportunities.OpportunityCategories.CleanKey(v.GetString()))
            .Where(k => k is not null && Opportunities.OpportunityCategories.Keys.Contains(k))
            .Distinct(StringComparer.Ordinal)
            .Take(Opportunities.OpportunityCategories.All.Count)
            .ToArray();

        return JsonSerializer.Serialize(new { categories = keys });
    }

    /// <summary>
    /// One paragraph, cut hard to the size of the field it is offered
    /// for — no marker, because the member may take it verbatim.
    /// </summary>
    private static string? ProfileSummary(JsonElement root, out string? error)
    {
        error = null;
        var summary = Str(root, "summary").Trim();
        if (summary.Length == 0)
            return Fail("The draft came back empty.", out error);
        var max = Profiles.ProfileRules.MaxBio;
        return JsonSerializer.Serialize(new
        {
            summary = summary.Length <= max ? summary : summary[..max].TrimEnd(),
        });
    }

    /// <summary>
    /// The review box's words: a phrase, and a sentence per line answering
    /// to the line's id. No figure is accepted here — a figure in the
    /// answer is ignored, because the figures are the portal's arithmetic
    /// and are joined on when the page reads the box. A line with no id or
    /// no sentence is dropped, an id said twice keeps its first sentence,
    /// and an empty list survives.
    /// </summary>
    private static string? ProfileReview(JsonElement root, out string? error)
    {
        error = null;
        if (root.ValueKind != JsonValueKind.Object)
            return Fail("The review came back in the wrong shape.", out error);

        var improvements = new List<object>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (root.TryGetProperty("improvements", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var id = Cut(Str(item, "id"), 80);
                var text = Cut(Str(item, "text"), 140);
                if (id.Length == 0 || text.Length == 0 || !seen.Add(id)) continue;
                improvements.Add(new { id, text });
                if (improvements.Count == Profiles.ProfileReview.MaxLines) break;
            }

        return JsonSerializer.Serialize(new
        {
            strongFor = NullIfEmpty(Cut(Str(root, "strongFor"), 60)),
            improvements,
        });
    }

    /// <summary>
    /// The approach draft: one text, cut hard to the field's own limit —
    /// no marker, because the applicant may take it verbatim.
    /// </summary>
    private static string? ProjectApproach(JsonElement root, out string? error)
    {
        error = null;
        var approach = Str(root, "approach").Trim();
        if (approach.Length == 0)
            return Fail("The draft came back empty.", out error);
        return JsonSerializer.Serialize(new
        {
            approach = Cut(approach, Opportunities.ApplicationRules.MaxApproach),
        });
    }

    /// <summary>
    /// The evaluation's words: a phrase per line answering to the line's
    /// id, in two lists, and one note. No figure is accepted here — the
    /// figures are the portal's arithmetic and are joined on when the page
    /// reads the box. A line with no id or no phrase is dropped, an id said
    /// twice keeps its first phrase, and empty lists survive.
    /// </summary>
    private static string? ApplicationEvaluation(JsonElement root, out string? error)
    {
        error = null;
        if (root.ValueKind != JsonValueKind.Object)
            return Fail("The evaluation came back in the wrong shape.", out error);

        List<object> Lines(string name)
        {
            var lines = new List<object>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (root.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var item in arr.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var id = Cut(Str(item, "id"), 40);
                    var text = Cut(Str(item, "text"), 60);
                    if (id.Length == 0 || text.Length == 0 || !seen.Add(id)) continue;
                    lines.Add(new { id, text });
                    if (lines.Count == 8) break;
                }
            return lines;
        }

        return JsonSerializer.Serialize(new
        {
            strengths = Lines("strengths"),
            risks = Lines("risks"),
            note = NullIfEmpty(Cut(Str(root, "note"), 200)),
        });
    }

    /// <summary>
    /// The requirements draft: rows with both halves, cut to the table's
    /// own caps with no marker because a row lands in the table verbatim.
    /// A half-filled row is dropped rather than refused — the save would
    /// refuse it, and the rest of the table is still worth offering. A
    /// constraint named twice keeps its first row.
    /// </summary>
    private static string? Requirements(JsonElement root, out string? error)
    {
        error = null;
        var items = new List<object>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("requirements", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var r in arr.EnumerateArray())
            {
                var title = Cut(Str(r, "title"), Opportunities.Rubric.MaxTitle);
                var detail = Cut(Str(r, "detail"), Opportunities.Rubric.MaxDetail);
                if (title.Length == 0 || detail.Length == 0 || !seen.Add(title)) continue;
                items.Add(new { title, detail });
                if (items.Count == Opportunities.Rubric.MaxRequirements) break;
            }
        if (items.Count == 0) return Fail("The model proposed no usable requirements.", out error);
        return JsonSerializer.Serialize(new { requirements = items });
    }

    /// <summary>
    /// The rubric draft: titled lines with a whole number of points each,
    /// cut to the table's caps. The points are the substance of the
    /// suggestion — which line carries the work — so they are the model's;
    /// what is the portal's is that they add up to a hundred, which is
    /// what the prompt asked for and what the standard rubric does, so a
    /// list that comes back at 90 or 110 is rescaled rather than shown
    /// with a total the client would have to fix by hand.
    /// </summary>
    private static string? Criteria(JsonElement root, out string? error)
    {
        error = null;
        var rows = new List<(string Title, int Points, string? Description)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("criteria", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var c in arr.EnumerateArray())
            {
                var title = Cut(Str(c, "title"), Opportunities.Rubric.MaxTitle);
                var given = c.ValueKind == JsonValueKind.Object
                    && c.TryGetProperty("points", out var p)
                    && p.ValueKind == JsonValueKind.Number
                    && p.TryGetDouble(out var d)
                        ? (int)Math.Round(d)
                        : 0;
                if (title.Length == 0 || given <= 0 || !seen.Add(title)) continue;
                rows.Add((title, given, NullIfEmpty(Cut(Str(c, "description"), Opportunities.Rubric.MaxDetail))));
                if (rows.Count == Opportunities.Rubric.MaxCriteria) break;
            }
        if (rows.Count == 0) return Fail("The model proposed no usable criteria.", out error);

        var points = ToHundred(rows.Select(r => r.Points).ToArray());
        return JsonSerializer.Serialize(new
        {
            criteria = rows.Select((r, i) => new { title = r.Title, points = points[i], description = r.Description }),
        });
    }

    /// <summary>
    /// Whole numbers in the same proportions, adding up to a hundred: each
    /// share rounded down, the remainder handed out one point at a time to
    /// the largest fractions first, and no line ever rounded down to zero.
    /// A list that already adds up to a hundred comes back untouched.
    /// </summary>
    internal static int[] ToHundred(int[] points)
    {
        var total = points.Sum();
        if (points.Length == 0 || points.Length > Opportunities.Rubric.MaxPoints || total == Opportunities.Rubric.MaxPoints)
            return points;
        var exact = points.Select(p => p * 100.0 / total).ToArray();
        var result = exact.Select(e => Math.Max(1, (int)Math.Floor(e))).ToArray();
        var sum = result.Sum();
        // The remainder goes to the lines rounding took the most from; an
        // overshoot (a line so small its floor was lifted to one) is taken
        // back from the largest lines, which can always give.
        var byFraction = Enumerable.Range(0, points.Length)
            .OrderByDescending(i => exact[i] - Math.Floor(exact[i]))
            .ThenBy(i => i)
            .ToArray();
        for (var k = 0; sum < 100; k = (k + 1) % byFraction.Length, sum++) result[byFraction[k]]++;
        var bySize = Enumerable.Range(0, points.Length)
            .OrderByDescending(i => result[i])
            .ThenBy(i => i)
            .ToArray();
        for (var k = 0; sum > 100; k = (k + 1) % bySize.Length)
            if (result[bySize[k]] > 1)
            {
                result[bySize[k]]--;
                sum--;
            }
        return result;
    }

    /// <summary>Cut to the box it is shown in, with no marker — the member reads it as it is.</summary>
    private static string Cut(string s, int max)
    {
        var t = s.Trim();
        return t.Length <= max ? t : t[..max].TrimEnd();
    }

    private static string Str(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object
        && el.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

    private static string[] StrList(JsonElement el, string name, int maxItems, int maxChars)
    {
        if (el.ValueKind != JsonValueKind.Object
            || !el.TryGetProperty(name, out var arr)
            || arr.ValueKind != JsonValueKind.Array)
            return [];
        return arr.EnumerateArray()
            .Where(v => v.ValueKind == JsonValueKind.String)
            .Select(v => AiRules.Clip(v.GetString(), maxChars))
            .Where(s => s.Length > 0)
            .Take(maxItems)
            .ToArray();
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
