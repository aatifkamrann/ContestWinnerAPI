using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinnersPortal.Services.Ai;
using WinnersPortal.Services.Opportunities;

namespace WinnersPortal.AiEvals;

/// <summary>One profile the matching run reads: what the feed would embed, and the kinds of work the reading of it should name.</summary>
public sealed record MatchingCase(string Name, string? Note, string? Headline, IReadOnlyList<MatchingSkill> Skills, IReadOnlySet<string> Expected, string File)
{
    public string Text => EvalMatching.ProfileText(Headline, Skills);
}

public sealed record MatchingSkill(string Name, string Level, int Years);

/// <summary>
/// The embedding eval, run with <c>--matching</c>: could a vector per
/// profile and per category replace the model's reading of which kinds
/// of work a freelancer does (the Recommended colouring on the feed)? The
/// profiles under <c>evals/matching/</c> are embedded once, the categories
/// once, and every profile is compared with every category by cosine
/// similarity. Each case says which categories today's reading names for
/// it, and the report says, threshold by threshold, how far the vectors
/// agree with those readings — precision, recall and the profiles whose
/// set comes out exactly right. The feed keeps the model's reading until
/// a run of this says the agreement is good enough; the roadmap's rule is
/// an eval before a switch, and this is that eval.
/// </summary>
public static class EvalMatching
{
    public const string Folder = "matching";

    /// <summary>The thresholds tried, 0.30 to 0.90: a pair at or above one is "this profile's kind of work".</summary>
    public static readonly IReadOnlyList<double> Thresholds = [.. Enumerable.Range(30, 61).Select(i => i / 100.0)];

    /// <summary>Every category a reading may name; "Something else" is not a kind of work and is never named.</summary>
    public static IReadOnlyList<OpportunityCategory> Categories { get; } =
        [.. OpportunityCategories.All.Where(c => c.Key != "other")];

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private sealed record Wire(string? Note, Input? Input, Expect? Expect);

    private sealed record Input(string? Headline, List<MatchingSkill>? Skills);

    private sealed record Expect(List<string>? Categories);

    /// <summary>
    /// Every profile under <c>evals/matching/</c>, narrowed to one name
    /// when given. A file that does not parse, a category the portal does
    /// not have or a profile with nothing to embed fails the whole load,
    /// as the prompt cases' loader does.
    /// </summary>
    public static IReadOnlyList<MatchingCase> Load(string evalsDir, string? name = null)
    {
        var dir = Path.Combine(evalsDir, Folder);
        if (!Directory.Exists(dir)) throw new EvalCaseException([$"There is no folder at {dir}."]);
        var problems = new List<string>();
        var cases = new List<MatchingCase>();
        foreach (var file in Directory.GetFiles(dir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            var caseName = Path.GetFileNameWithoutExtension(file);
            if (caseName.StartsWith('_')) continue;
            if (name is not null && !string.Equals(name, caseName, StringComparison.OrdinalIgnoreCase)) continue;
            Wire? wire;
            try
            {
                wire = JsonSerializer.Deserialize<Wire>(File.ReadAllText(file), Json);
            }
            catch (JsonException e)
            {
                problems.Add($"{Folder}/{caseName}: {e.Message}");
                continue;
            }
            var skills = wire?.Input?.Skills ?? [];
            var expected = new HashSet<string>(StringComparer.Ordinal);
            foreach (var key in wire?.Expect?.Categories ?? [])
            {
                if (Categories.Any(c => c.Key == key)) expected.Add(key);
                else problems.Add($"{Folder}/{caseName}: expect.categories names \"{key}\", which is not a category the portal has.");
            }
            if (wire?.Expect?.Categories is null)
                problems.Add($"{Folder}/{caseName}: has no expect.categories (an empty list says the reading names none).");
            if (string.IsNullOrWhiteSpace(wire?.Input?.Headline) && skills.Count == 0)
                problems.Add($"{Folder}/{caseName}: has nothing to embed — give input.headline or input.skills.");
            if (skills.Any(s => string.IsNullOrWhiteSpace(s?.Name)))
                problems.Add($"{Folder}/{caseName}: a skill has no name.");
            cases.Add(new MatchingCase(caseName, wire?.Note, wire?.Input?.Headline, skills, expected, file));
        }
        if (problems.Count > 0) throw new EvalCaseException(problems);
        return cases;
    }

    /// <summary>
    /// The profile as a text a model can place: the headline, then the
    /// skills with their level and years — the same facts the reading's
    /// prompt is given (<see cref="AiInputs.WorkKinds"/>), as sentences
    /// rather than JSON, because an embedding model reads prose best.
    /// </summary>
    public static string ProfileText(string? headline, IEnumerable<MatchingSkill> skills)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(headline)) parts.Add(headline.Trim().TrimEnd('.') + ".");
        var named = skills.Where(s => !string.IsNullOrWhiteSpace(s.Name))
            .Select(s => $"{s.Name.Trim()} ({s.Level.ToLowerInvariant()}, {s.Years} {(s.Years == 1 ? "year" : "years")})")
            .ToList();
        if (named.Count > 0) parts.Add("Skills: " + string.Join(", ", named) + ".");
        return string.Join(" ", parts);
    }

    /// <summary>A category as a text: its label, its subcategories and the skills the taxonomy lists under them.</summary>
    public static string CategoryText(OpportunityCategory category)
    {
        var skills = category.Subcategories.SelectMany(s => s.Skills).Concat(category.Skills)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var text = new StringBuilder(category.Label).Append('.');
        if (category.Subcategories.Count > 0)
            text.Append(" Kinds of work: ").Append(string.Join(", ", category.Subcategories.Select(s => s.Label))).Append('.');
        if (skills.Count > 0) text.Append(" Skills: ").Append(string.Join(", ", skills)).Append('.');
        return text.ToString();
    }

    /// <summary>
    /// The agreement between the vectors and the readings, every
    /// threshold tried over every profile-and-category pair. Pure: the
    /// vectors are whatever the run was given.
    /// </summary>
    public static MatchingReport Score(
        IReadOnlyList<MatchingCase> cases, IReadOnlyList<float[]> profileVectors, IReadOnlyList<float[]> categoryVectors,
        string provider, string model, int calls, int maxCalls, AiTokens tokens, decimal? costUsd)
    {
        if (profileVectors.Count != cases.Count || categoryVectors.Count != Categories.Count)
            throw new ArgumentException("One vector per profile and one per category are needed to score.");
        var similarities = cases.Select((_, i) => Categories
                .Select((c, j) => (c.Key, Similarity: AiEmbeddings.Cosine(profileVectors[i], categoryVectors[j])))
                .ToDictionary(x => x.Key, x => Math.Round(x.Similarity, 4)))
            .ToList();

        var rows = new List<ThresholdRow>();
        foreach (var t in Thresholds)
        {
            int tp = 0, fp = 0, fn = 0, exact = 0;
            for (var i = 0; i < cases.Count; i++)
            {
                var predicted = Predicted(similarities[i], t);
                tp += predicted.Count(cases[i].Expected.Contains);
                fp += predicted.Count(k => !cases[i].Expected.Contains(k));
                fn += cases[i].Expected.Count(k => !predicted.Contains(k));
                if (predicted.SetEquals(cases[i].Expected)) exact++;
            }
            var precision = tp + fp == 0 ? 0 : (double)tp / (tp + fp);
            var recall = tp + fn == 0 ? 0 : (double)tp / (tp + fn);
            var f1 = precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall);
            rows.Add(new ThresholdRow(t, tp, fp, fn, Math.Round(precision, 3), Math.Round(recall, 3), Math.Round(f1, 3), exact));
        }
        // The best threshold is the one where the vectors and the readings
        // agree most (F1); between equals, the one that gets most profiles
        // exactly right, then the higher — a stricter line names less by mistake.
        var best = rows.OrderByDescending(r => r.F1).ThenByDescending(r => r.Exact).ThenByDescending(r => r.Threshold).First().Threshold;

        var results = cases.Select((c, i) =>
        {
            var predicted = Predicted(similarities[i], best);
            return new MatchingResult(
                c.Name,
                [.. c.Expected.Order(StringComparer.Ordinal)],
                [.. predicted.Order(StringComparer.Ordinal)],
                [.. c.Expected.Where(k => !predicted.Contains(k)).Order(StringComparer.Ordinal)],
                [.. predicted.Where(k => !c.Expected.Contains(k)).Order(StringComparer.Ordinal)],
                similarities[i]);
        }).ToList();

        return new MatchingReport(
            DateTimeOffset.UtcNow, provider, model, calls, maxCalls, tokens.Input, tokens.Output, costUsd, rows, best, results);
    }

    private static HashSet<string> Predicted(IReadOnlyDictionary<string, double> similarities, double threshold) =>
        similarities.Where(p => p.Value >= threshold - 1e-9).Select(p => p.Key).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The run itself: the categories embedded in one call, the profiles
    /// in as few as they fit, scored and written. The cap and the key are
    /// the prompt eval's; a dry run prints what would be embedded and
    /// sends nothing. Returns the exit code.
    /// </summary>
    public static async Task<int> RunAsync(
        IReadOnlyList<MatchingCase> profiles, string provider, string? model, EvalSettings.Choice choice, AiPriceList prices,
        bool dryRun, string outDir, string settingsNote, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(model)) model = AiEmbeddings.DefaultModel(provider);
        }
        catch (AiProviderException e)
        {
            Console.Error.WriteLine(e.Message);
            return 2;
        }
        var categoryTexts = Categories.Select(CategoryText).ToList();
        var profileTexts = profiles.Select(p => p.Text).ToList();
        var needed = 1 + (profiles.Count + AiEmbeddings.MaxTexts - 1) / AiEmbeddings.MaxTexts;
        Console.WriteLine($"{profiles.Count} profile(s) against {Categories.Count} categories · {provider}/{model} (provider from {choice.ProviderFrom}"
            + (choice.ApiKey is null ? ", no key" : $", key from {choice.ApiKeyFrom}")
            + $") · {needed} embedding call(s) of a cap of {choice.MaxCalls} (from {choice.MaxCallsFrom}) · timeout {choice.TimeoutSeconds} s{(dryRun ? " · dry run" : "")}");
        if (dryRun)
        {
            foreach (var (text, i) in categoryTexts.Select((t, i) => (t, i)))
                Console.WriteLine($"  category {Categories[i].Key}: {text.Length:N0} characters");
            foreach (var (p, i) in profiles.Select((p, i) => (p, i)))
                Console.WriteLine($"  {p.Name}: {profileTexts[i].Length:N0} characters, expects {(p.Expected.Count == 0 ? "none" : string.Join(", ", p.Expected.Order()))}");
            Console.WriteLine("Nothing was sent.");
            return 0;
        }
        if (choice.ApiKey is null)
        {
            Console.Error.WriteLine(
                "No eval key. Save one under Settings → AI evals, or set WP_EVAL_API_KEY in this shell; or pass --dry-run. "
                + $"The evals spend on a key of their own, never the portal's. (Settings → AI evals {settingsNote}.)");
            return 2;
        }
        if (needed > choice.MaxCalls)
        {
            Console.Error.WriteLine($"The matching run needs {needed} embedding call(s) and the cap is {choice.MaxCalls}; raise it with --max-calls. Nothing was sent.");
            return 2;
        }
        if (prices.Find(model) is null)
            Console.WriteLine($"No price is saved for {model} under Settings → AI automation, so the report will count tokens and not dollars.");

        var embedder = new HttpEvalEmbedder(provider, model, choice.ApiKey, choice.TimeoutSeconds);
        var calls = 0;
        var tokens = AiTokens.None;
        var counted = true;
        async Task<List<float[]>> EmbedAsync(IReadOnlyList<string> texts)
        {
            var vectors = new List<float[]>(texts.Count);
            foreach (var chunk in texts.Chunk(AiEmbeddings.MaxTexts))
            {
                var (embedding, ms) = await embedder.EmbedAsync(chunk, ct);
                calls++;
                if (embedding.Tokens is { } t) tokens += t;
                else counted = false;
                Console.WriteLine($"  embedded {chunk.Length} text(s) in {ms:N0} ms" + (embedding.Tokens is { } tk ? $" · {tk}" : " · no token count"));
                vectors.AddRange(embedding.Vectors);
            }
            return vectors;
        }

        List<float[]> categoryVectors, profileVectors;
        try
        {
            categoryVectors = await EmbedAsync(categoryTexts);
            profileVectors = await EmbedAsync(profileTexts);
        }
        catch (Exception e) when (e is AiProviderException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            Console.Error.WriteLine($"The embedding call failed: {e.Message}");
            return 1;
        }
        var report = Score(profiles, profileVectors, categoryVectors, provider, model, calls, choice.MaxCalls, tokens,
            counted ? prices.Cost(model, tokens) : null);

        Console.WriteLine();
        Console.WriteLine(report.ToMarkdown());
        Directory.CreateDirectory(outDir);
        var stem = Path.Combine(outDir, report.FileStem);
        File.WriteAllText(stem + ".json", report.ToJson());
        File.WriteAllText(stem + ".md", report.ToMarkdown());
        Console.WriteLine($"Saved {stem}.json and .md");
        return 0;
    }
}

/// <summary>One threshold's agreement: the pairs named rightly, wrongly and missed, and the profiles whose whole set came out right.</summary>
public sealed record ThresholdRow(double Threshold, int Tp, int Fp, int Fn, double Precision, double Recall, double F1, int Exact);

/// <summary>One profile at the best threshold: what the reading names, what the vectors name, and every category's similarity.</summary>
public sealed record MatchingResult(
    string Name,
    IReadOnlyList<string> Expected,
    IReadOnlyList<string> Predicted,
    IReadOnlyList<string> Missed,
    IReadOnlyList<string> Extra,
    IReadOnlyDictionary<string, double> Similarities);

/// <summary>A matching run's report, saved as JSON and written as Markdown like the prompt eval's.</summary>
public sealed record MatchingReport(
    DateTimeOffset RunAtUtc,
    string Provider,
    string Model,
    int Calls,
    int MaxCalls,
    long InputTokens,
    long OutputTokens,
    decimal? CostUsd,
    IReadOnlyList<ThresholdRow> Thresholds,
    double BestThreshold,
    IReadOnlyList<MatchingResult> Profiles)
{
    public AiTokens Tokens => new(InputTokens, OutputTokens);

    public ThresholdRow Best => Thresholds.First(r => Math.Abs(r.Threshold - BestThreshold) < 1e-9);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public string FileStem => $"{RunAtUtc:yyyyMMdd-HHmmss}-matching-{Safe(Provider)}-{Safe(Model)}";

    private static string Safe(string s) => string.Concat(s.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '.' ? ch : '_'));

    private static string F(double d) => d.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>The report a person reads: the agreement at every fifth threshold and the best, then each profile at the best.</summary>
    public string ToMarkdown()
    {
        var md = new StringBuilder();
        md.Append("# AI matching eval · ").Append(Provider).Append('/').Append(Model)
            .Append(" · ").Append(RunAtUtc.ToString("yyyy-MM-dd HH:mm")).AppendLine(" UTC");
        md.AppendLine();
        md.Append("Embedding calls: ").Append(Calls).Append(" of ").AppendLine(MaxCalls.ToString(CultureInfo.InvariantCulture));
        md.Append("Tokens: ").Append(Tokens.ToString())
            .AppendLine(CostUsd is { } usd ? $" · about {AiPrices.Dollars(usd)}" : " · no price saved for the model, or no count from the provider");
        md.AppendLine();
        var best = Best;
        md.Append("**Best threshold ").Append(F(BestThreshold)).Append("**: precision ").Append(F(best.Precision))
            .Append(", recall ").Append(F(best.Recall)).Append(", F1 ").Append(F(best.F1))
            .Append(" — ").Append(best.Exact).Append(" of ").Append(Profiles.Count).AppendLine(" profiles read exactly as today.");
        md.AppendLine();
        md.AppendLine("| Threshold | Right | Extra | Missed | Precision | Recall | F1 | Exact |");
        md.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var r in Thresholds.Where(r => Math.Round(r.Threshold * 100) % 5 == 0 || Math.Abs(r.Threshold - BestThreshold) < 1e-9))
            md.Append("| ").Append(F(r.Threshold)).Append(Math.Abs(r.Threshold - BestThreshold) < 1e-9 ? " (best)" : "")
                .Append(" | ").Append(r.Tp).Append(" | ").Append(r.Fp).Append(" | ").Append(r.Fn)
                .Append(" | ").Append(F(r.Precision)).Append(" | ").Append(F(r.Recall)).Append(" | ").Append(F(r.F1))
                .Append(" | ").Append(r.Exact).AppendLine(" |");
        md.AppendLine();
        md.AppendLine("## Profiles at the best threshold");
        md.AppendLine();
        foreach (var p in Profiles)
        {
            var mark = p.Missed.Count == 0 && p.Extra.Count == 0 ? "✓" : "✗";
            md.Append("- ").Append(mark).Append(' ').Append(p.Name)
                .Append(" — today: ").Append(p.Expected.Count == 0 ? "none" : string.Join(", ", p.Expected))
                .Append("; vectors: ").Append(p.Predicted.Count == 0 ? "none" : string.Join(", ", p.Predicted));
            if (p.Missed.Count > 0) md.Append("; missed ").Append(string.Join(", ", p.Missed.Select(k => $"{k} ({F(p.Similarities[k])})")));
            if (p.Extra.Count > 0) md.Append("; extra ").Append(string.Join(", ", p.Extra.Select(k => $"{k} ({F(p.Similarities[k])})")));
            md.AppendLine();
        }
        return md.ToString();
    }
}
