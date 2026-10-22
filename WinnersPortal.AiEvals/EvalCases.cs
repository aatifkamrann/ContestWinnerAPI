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
    string File,
    CaseAnswers? Answers = null)
{
    public string Id => $"{Slug}/{Name}";
}

/// <summary>
/// A case's own sample answers, written by hand: one good answer that must
/// pass every check, and bad ones that must fail, each with the words its
/// failure must name. They prove with no model call that the checks can be
/// met and that they catch what the case exists for — a check no answer can
/// pass, or one that passes anything, is found at build time. The good
/// answers are also what <c>--replay</c> feeds the whole tool.
/// </summary>
public sealed record CaseAnswers(string Good, IReadOnlyList<BadAnswer> Bad);

/// <summary>A wrong answer, the words some failure must contain, and why it is wrong.</summary>
public sealed record BadAnswer(string Text, IReadOnlyList<string> Fails, string? Why);

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
        "category", "subcategory", "confident", "categories", "notCategories", "minCount", "maxCount", "mentionsAny", "forbidWords", "forbidPattern", "judge",
        "competitiveBetween",
    };

    /// <summary>The category key the answer must name (categorise).</summary>
    public string? Category { get; init; }

    /// <summary>Whether the case says anything about the subcategory; <see cref="Subcategory"/> then, null meaning "none".</summary>
    public bool SubcategoryGiven { get; init; }

    public string? Subcategory { get; init; }

    public bool? Confident { get; init; }

    /// <summary>Category keys the answer's list must hold (work-kinds).</summary>
    public IReadOnlyList<string> Categories { get; init; } = [];

    /// <summary>Category keys the answer's list must not hold (work-kinds).</summary>
    public IReadOnlyList<string> NotCategories { get; init; } = [];

    /// <summary>Bounds on the feature's list — milestones, improvements, review focus.</summary>
    public int? MinCount { get; init; }

    public int? MaxCount { get; init; }

    /// <summary>Groups of words; some text in the answer must contain one word from each group (case-insensitive).</summary>
    public IReadOnlyList<IReadOnlyList<string>> MentionsAny { get; init; } = [];

    /// <summary>Words no text in the answer may contain (case-insensitive, whole words).</summary>
    public IReadOnlyList<string> ForbidWords { get; init; } = [];

    /// <summary>A regular expression no text in the answer may match (case-insensitive).</summary>
    public string? ForbidPattern { get; init; }

    /// <summary>The range the competitive figure must land in, in whole dollars (award): a sense check on the scale, the judge reads the rest.</summary>
    public (decimal Low, decimal High)? CompetitiveBetween { get; init; }

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
                if (p.Name is "categories" or "notCategories")
                    foreach (var key in Strings(p.Value, problems, "expect." + p.Name))
                        if (!WinnersPortal.Services.Opportunities.OpportunityCategories.Keys.Contains(key))
                            problems.Add($"expect.{p.Name} names \"{key}\", which is not a category key.");
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

        (decimal, decimal)? between = null;
        if (merged.TryGetValue("competitiveBetween", out var range))
        {
            var ends = range.ValueKind == JsonValueKind.Array
                ? range.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.Number).Select(v => v.GetDecimal()).ToList()
                : [];
            if (ends.Count == 2 && ends[0] <= ends[1]) between = (ends[0], ends[1]);
            else problems.Add("expect.competitiveBetween must be two amounts, the lower first.");
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
            Categories = merged.TryGetValue("categories", out var cats) ? Strings(cats, [], "") : [],
            NotCategories = merged.TryGetValue("notCategories", out var notCats) ? Strings(notCats, [], "") : [],
            MinCount = Int("minCount"),
            MaxCount = Int("maxCount"),
            MentionsAny = groups,
            ForbidWords = forbid.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            ForbidPattern = pattern,
            Judge = Str("judge"),
            CompetitiveBetween = between,
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
                var answers = r.TryGetProperty("answers", out var a) ? ParseAnswers(a, own) : null;
                if (answers is null && a.ValueKind == JsonValueKind.Undefined)
                    own.Add("has no answers: one good answer and at least one bad one (evals/README.md, \"Sample answers\").");
                foreach (var p in r.EnumerateObject())
                    if (p.Name is not ("name" or "note" or "input" or "expect" or "answers"))
                        own.Add($"{p.Name} is not part of a case (name, note, input, expect, answers).");

                if (input.ValueKind == JsonValueKind.Object)
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
                    input.Clone(), expectation, file, answers));
            }
        }
        if (problems.Count > 0) throw new EvalCaseException(problems);
        return cases;
    }

    /// <summary>
    /// <c>{"good": answer, "bad": [{"answer": answer, "fails": [words], "why": text}]}</c>,
    /// where an answer is the model's JSON as an object, or a string holding
    /// raw text for an answer that is not JSON at all.
    /// </summary>
    private static CaseAnswers? ParseAnswers(JsonElement e, List<string> problems)
    {
        if (e.ValueKind != JsonValueKind.Object)
        {
            problems.Add("answers must be an object with good and bad.");
            return null;
        }
        foreach (var p in e.EnumerateObject())
            if (p.Name is not ("good" or "bad"))
                problems.Add($"answers.{p.Name} is not part of the answers (good, bad).");

        string? good = e.TryGetProperty("good", out var g) ? AnswerText(g) : null;
        if (good is null) problems.Add("answers.good is missing: the answer that must pass every check.");

        var bad = new List<BadAnswer>();
        if (!e.TryGetProperty("bad", out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0)
            problems.Add("answers.bad must list at least one answer the checks must refuse.");
        else
        {
            var n = 0;
            foreach (var item in list.EnumerateArray())
            {
                n++;
                if (item.ValueKind != JsonValueKind.Object)
                {
                    problems.Add($"answers.bad[{n}] must be an object with answer and fails.");
                    continue;
                }
                foreach (var p in item.EnumerateObject())
                    if (p.Name is not ("answer" or "fails" or "why"))
                        problems.Add($"answers.bad[{n}].{p.Name} is not part of a bad answer (answer, fails, why).");
                var text = item.TryGetProperty("answer", out var ans) ? AnswerText(ans) : null;
                if (text is null) problems.Add($"answers.bad[{n}].answer is missing.");
                var fails = item.TryGetProperty("fails", out var f) ? Strings(f, problems, $"answers.bad[{n}].fails") : [];
                if (fails.Count == 0) problems.Add($"answers.bad[{n}].fails must name the words its failure says.");
                if (text is not null && fails.Count > 0)
                    bad.Add(new BadAnswer(text, fails,
                        item.TryGetProperty("why", out var why) && why.ValueKind == JsonValueKind.String ? why.GetString() : null));
            }
        }
        return good is null || bad.Count == 0 ? null : new CaseAnswers(good, bad);
    }

    private static string? AnswerText(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object or JsonValueKind.Array => e.GetRawText(),
        JsonValueKind.String => e.GetString(),
        _ => null,
    };

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
                list.Add(item.GetString()!);
            else problems.Add($"{where} holds something that is not a word.");
        return list;
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
