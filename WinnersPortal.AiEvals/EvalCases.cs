using System.Text.Json;
using System.Text.RegularExpressions;
using WinnersPortal.Domain;

namespace WinnersPortal.AiEvals;

/// <summary>
/// One golden case as its file holds it: a feature (the folder), a name
/// (the file), the canonical input the prompt is built from, and what a
/// good answer must satisfy. The cases are the portal's own demo data and
/// nothing a real member typed.
/// </summary>
public sealed record EvalCase(
    AiFeature Feature,
    string Slug,
    string Name,
    string? Note,
    JsonElement Input,
    Expectation Expect,
    string File)
{
    public string Id => $"{Slug}/{Name}";
}

/// <summary>
/// What a case expects of the answer, beyond the validator every answer
/// must pass. A folder's <c>_defaults.json</c> is merged under each of its
/// cases: the case's value wins key by key, except that forbidden words
/// add up — a rule the whole feature keeps is not something a case should
/// have to repeat.
/// </summary>
public sealed class Expectation
{
    public static readonly IReadOnlySet<string> Known = new HashSet<string>(StringComparer.Ordinal)
    {
        "category", "subcategory", "confident", "minCount", "maxCount", "mentionsAny", "forbidWords", "forbidPattern", "judge",
    };

    /// <summary>The category key the answer must name (categorise).</summary>
    public string? Category { get; init; }

    /// <summary>Whether the case says anything about the subcategory; <see cref="Subcategory"/> then, null meaning "none".</summary>
    public bool SubcategoryGiven { get; init; }

    public string? Subcategory { get; init; }

    public bool? Confident { get; init; }

    /// <summary>Bounds on the feature's list — milestones, improvements, review focus.</summary>
    public int? MinCount { get; init; }

    public int? MaxCount { get; init; }

    /// <summary>Groups of words; some text in the answer must contain one word from each group (case-insensitive).</summary>
    public IReadOnlyList<IReadOnlyList<string>> MentionsAny { get; init; } = [];

    /// <summary>Words no text in the answer may contain (case-insensitive, whole words).</summary>
    public IReadOnlyList<string> ForbidWords { get; init; } = [];

    /// <summary>A regular expression no text in the answer may match (case-insensitive).</summary>
    public string? ForbidPattern { get; init; }

    /// <summary>A rubric for the judge model, for what no check can read — tone, faithfulness, whether prose says what the facts show.</summary>
    public string? Judge { get; init; }

    /// <summary>Reads one block, naming every key it does not know.</summary>
    public static Expectation Parse(JsonElement? defaults, JsonElement? own, List<string> problems)
    {
        var merged = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var forbid = new List<string>();
        foreach (var block in new[] { defaults, own })
        {
            if (block is not { ValueKind: JsonValueKind.Object } e) continue;
            foreach (var p in e.EnumerateObject())
            {
                if (!Known.Contains(p.Name))
                {
                    problems.Add($"expect.{p.Name} is not a check the evals know ({string.Join(", ", Known.Order())}).");
                    continue;
                }
                if (p.Name == "forbidWords") forbid.AddRange(Strings(p.Value, problems, "expect.forbidWords"));
                else merged[p.Name] = p.Value;
            }
        }

        string? Str(string key) => merged.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        int? Int(string key) => merged.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
        bool? Bool(string key) => merged.TryGetValue(key, out var v)
            ? v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null }
            : null;

        var pattern = Str("forbidPattern");
        if (pattern is not null)
        {
            try
            {
                _ = new Regex(pattern, RegexOptions.IgnoreCase);
            }
            catch (ArgumentException e)
            {
                problems.Add($"expect.forbidPattern is not a regular expression: {e.Message}");
            }
        }

        var groups = new List<IReadOnlyList<string>>();
        if (merged.TryGetValue("mentionsAny", out var any))
        {
            if (any.ValueKind != JsonValueKind.Array) problems.Add("expect.mentionsAny must be a list of word lists.");
            else
                foreach (var group in any.EnumerateArray())
                {
                    var words = Strings(group, problems, "expect.mentionsAny");
                    if (words.Count > 0) groups.Add(words);
                }
        }

        return new Expectation
        {
            Category = Str("category"),
            SubcategoryGiven = merged.ContainsKey("subcategory"),
            Subcategory = Str("subcategory"),
            Confident = Bool("confident"),
            MinCount = Int("minCount"),
            MaxCount = Int("maxCount"),
            MentionsAny = groups,
            ForbidWords = forbid.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            ForbidPattern = pattern,
            Judge = Str("judge"),
        };
    }

    private static List<string> Strings(JsonElement e, List<string> problems, string where)
    {
        if (e.ValueKind != JsonValueKind.Array)
        {
            problems.Add($"{where} must be a list of words.");
            return [];
        }
        var list = new List<string>();
        foreach (var item in e.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                list.Add(item.GetString()!.Trim());
            else problems.Add($"{where} holds something that is not a word.");
        return list;
    }
}

/// <summary>A case file that cannot be used, with every reason at once.</summary>
public sealed class EvalCaseException(IReadOnlyList<string> problems)
    : Exception("The eval cases have problems:\n  " + string.Join("\n  ", problems))
{
    public IReadOnlyList<string> Problems { get; } = problems;
}

public static class EvalCases
{
    public const string DefaultsFile = "_defaults.json";

    public const string ReportsFolder = "reports";

    /// <summary>
    /// Every case under <c>evals/</c>: one folder per feature slug, one
    /// file per case, the folder's defaults merged under each. Narrowed to
    /// the slugs and the case name given, when any are. A file that does
    /// not parse, an unknown folder or an unknown check fails the whole
    /// load — a case that is silently skipped is a case that never ran.
    /// </summary>
    public static IReadOnlyList<EvalCase> Load(string evalsDir, IReadOnlyCollection<string>? slugs = null, string? name = null)
    {
        if (!Directory.Exists(evalsDir)) throw new EvalCaseException([$"There is no folder at {evalsDir}."]);
        var problems = new List<string>();
        var cases = new List<EvalCase>();
        foreach (var dir in Directory.GetDirectories(evalsDir).OrderBy(d => d, StringComparer.Ordinal))
        {
            var slug = Path.GetFileName(dir);
            if (slug == ReportsFolder || slug == EvalMatching.Folder) continue;
            if (EvalFeatures.Parse(slug) is not { } feature)
            {
                problems.Add($"{slug}/ is not a feature the evals know ({EvalFeatures.Named}).");
                continue;
            }
            if (slugs is { Count: > 0 } && !slugs.Contains(slug)) continue;

            var defaultsPath = Path.Combine(dir, DefaultsFile);
            JsonElement? defaults = null;
            if (System.IO.File.Exists(defaultsPath))
                defaults = ParseFile(defaultsPath, problems);

            foreach (var file in Directory.GetFiles(dir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
            {
                if (Path.GetFileName(file) == DefaultsFile) continue;
                var caseName = Path.GetFileNameWithoutExtension(file);
                if (name is not null && !string.Equals(name, caseName, StringComparison.OrdinalIgnoreCase)) continue;
                var root = ParseFile(file, problems);
                if (root is not { ValueKind: JsonValueKind.Object } r) continue;

                var own = new List<string>();
                if (!r.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object)
                    own.Add("has no input object.");
                r.TryGetProperty("expect", out var expect);
                var expectation = Expectation.Parse(defaults, expect.ValueKind == JsonValueKind.Undefined ? null : expect, own);
                foreach (var p in r.EnumerateObject())
                    if (p.Name is not ("name" or "note" or "input" or "expect"))
                        own.Add($"{p.Name} is not part of a case (name, note, input, expect).");

                if (own.Count == 0)
                {
                    try
                    {
                        _ = EvalFeatures.Build(feature, input);
                    }
                    catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException)
                    {
                        own.Add($"input does not build a {slug} prompt: {e.Message}");
                    }
                }

                if (own.Count > 0)
                {
                    problems.AddRange(own.Select(p => $"{slug}/{caseName}.json: {p}"));
                    continue;
                }
                cases.Add(new EvalCase(
                    feature, slug, caseName,
                    r.TryGetProperty("note", out var note) && note.ValueKind == JsonValueKind.String ? note.GetString() : null,
                    input.Clone(), expectation, file));
            }
        }
        if (problems.Count > 0) throw new EvalCaseException(problems);
        return cases;
    }

    private static JsonElement? ParseFile(string file, List<string> problems)
    {
        try
        {
            using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(file), new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
            return doc.RootElement.Clone();
        }
        catch (JsonException e)
        {
            problems.Add($"{Path.GetFileName(Path.GetDirectoryName(file))}/{Path.GetFileName(file)}: not JSON ({e.Message}).");
            return null;
        }
    }

    /// <summary>The repository root: the folder holding WinnersPortal.sln above the running binary.</summary>
    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !System.IO.File.Exists(Path.Combine(dir.FullName, "WinnersPortal.sln")))
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new InvalidOperationException("WinnersPortal.sln was not found above the running binary; pass --cases.");
    }
}
