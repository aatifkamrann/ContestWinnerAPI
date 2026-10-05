using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinnersPortal.Services.Ai;

namespace WinnersPortal.AiEvals;

/// <summary>One case's outcome in a run.</summary>
public sealed record CaseResult(
    string Feature,
    string Name,
    /// <summary>Whether the case was sent to the model at all — false in a dry run, or once the call cap was reached.</summary>
    bool Ran,
    bool Pass,
    IReadOnlyList<string> Failures,
    JudgeResult? Judge,
    string? Output,
    int LatencyMs,
    /// <summary>Why the case could not be scored: the provider refused, the input did not build, the cap was reached.</summary>
    string? Error,
    /// <summary>Tokens sent for the case, the judge's call included; 0 where the provider counted none.</summary>
    long InputTokens = 0,
    /// <summary>Tokens answered, likewise.</summary>
    long OutputTokens = 0,
    /// <summary>What the case cost at the saved prices; null while the model, or the judge's, has none.</summary>
    decimal? CostUsd = null)
{
    public string Id => $"{Feature}/{Name}";

    public AiTokens Tokens => new(InputTokens, OutputTokens);
}

/// <summary>
/// A run's report: which model, which prompt versions, how many calls it
/// spent, and every case's outcome. Saved as JSON so a later run can be
/// set beside it, and written as Markdown for a person to read.
/// </summary>
public sealed record EvalReport(
    DateTimeOffset RunAtUtc,
    string Provider,
    string Model,
    string? JudgeModel,
    IReadOnlyDictionary<string, int> PromptVersions,
    int Calls,
    int MaxCalls,
    bool CapReached,
    bool DryRun,
    IReadOnlyList<CaseResult> Cases)
{
    public int Passed => Cases.Count(c => c.Ran && c.Pass);

    public int Failed => Cases.Count(c => c.Ran && !c.Pass);

    public int NotRun => Cases.Count(c => !c.Ran);

    /// <summary>Every token the run spent, judge calls included.</summary>
    public AiTokens Tokens => Cases.Aggregate(AiTokens.None, (sum, c) => sum + c.Tokens);

    /// <summary>The run's estimate at the saved prices; null while any case that ran has no price.</summary>
    public decimal? CostUsd => Cases.Any(c => c.Ran && c.CostUsd is null) ? null : Cases.Sum(c => c.CostUsd ?? 0m);

    /// <summary>"12,345 in · 6,789 out · about $0.12" — the run's bill in one line.</summary>
    public string SpendLine => $"{Tokens}" + (CostUsd is { } usd ? $" · about {AiPrices.Dollars(usd)}" : " · no price saved for the model");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static EvalReport FromJson(string json) =>
        JsonSerializer.Deserialize<EvalReport>(json, Json) ?? throw new JsonException("The report is empty.");

    /// <summary>The file stem a run is saved under: when, which provider and model.</summary>
    public string FileStem =>
        $"{RunAtUtc:yyyyMMdd-HHmmss}-{Safe(Provider)}-{Safe(Model)}";

    private static string Safe(string s) => string.Concat(s.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '.' ? ch : '_'));

    /// <summary>The report a person reads: the totals, a table per feature, then every failure with its reasons.</summary>
    public string ToMarkdown()
    {
        var md = new StringBuilder();
        md.Append("# AI eval · ").Append(Provider).Append('/').Append(Model)
            .Append(" · ").Append(RunAtUtc.ToString("yyyy-MM-dd HH:mm")).AppendLine(" UTC");
        md.AppendLine();
        if (DryRun) md.AppendLine("**Dry run** — the prompts were built and nothing was sent.").AppendLine();
        md.Append("Prompt versions: ")
            .AppendLine(string.Join(", ", PromptVersions.OrderBy(p => p.Key).Select(p => $"{p.Key} v{p.Value}")));
        md.Append("Judge: ").AppendLine(JudgeModel ?? "none");
        md.Append("Calls: ").Append(Calls).Append(" of ").Append(MaxCalls);
        if (CapReached) md.Append(" — **the call cap was reached**; the cases after it did not run");
        md.AppendLine();
        if (!DryRun) md.Append("Tokens: ").AppendLine(SpendLine);
        md.AppendLine();
        md.Append("**").Append(Passed).Append(" passed, ").Append(Failed).Append(" failed");
        if (NotRun > 0) md.Append(", ").Append(NotRun).Append(" not run");
        md.AppendLine("**").AppendLine();

        md.AppendLine("| Feature | Passed | Failed | Not run | Median ms | Tokens in / out | Cost |");
        md.AppendLine("|---|---:|---:|---:|---:|---:|---:|");
        foreach (var g in Cases.GroupBy(c => c.Feature).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var ran = g.Where(c => c.Ran && c.LatencyMs > 0).Select(c => c.LatencyMs).OrderBy(x => x).ToList();
            var median = ran.Count == 0 ? "–" : ran[ran.Count / 2].ToString("N0");
            var tokens = g.Aggregate(AiTokens.None, (sum, c) => sum + c.Tokens);
            var cost = g.Any(c => c.Ran && c.CostUsd is null) ? "–" : AiPrices.Dollars(g.Sum(c => c.CostUsd ?? 0m));
            md.Append("| ").Append(g.Key)
                .Append(" | ").Append(g.Count(c => c.Ran && c.Pass))
                .Append(" | ").Append(g.Count(c => c.Ran && !c.Pass))
                .Append(" | ").Append(g.Count(c => !c.Ran))
                .Append(" | ").Append(median)
                .Append(" | ").Append(tokens.Input.ToString("N0")).Append(" / ").Append(tokens.Output.ToString("N0"))
                .Append(" | ").Append(cost).AppendLine(" |");
        }

        var failures = Cases.Where(c => c.Ran && !c.Pass).ToList();
        if (failures.Count > 0)
        {
            md.AppendLine().AppendLine("## Failures").AppendLine();
            foreach (var c in failures)
            {
                md.Append("### ").AppendLine(c.Id);
                if (c.Error is not null) md.Append("- ").AppendLine(c.Error);
                foreach (var f in c.Failures) md.Append("- ").AppendLine(f);
                if (c.Judge is { Pass: false } j) md.Append("- judge: ").AppendLine(j.Reason);
                if (c.Output is not null)
                    md.AppendLine().AppendLine("```json").AppendLine(Indent(c.Output)).AppendLine("```");
                md.AppendLine();
            }
        }

        var judged = Cases.Where(c => c.Ran && c.Judge is not null).ToList();
        if (judged.Count > 0)
        {
            md.AppendLine().AppendLine("## Judge").AppendLine();
            foreach (var c in judged)
                md.Append("- ").Append(c.Id).Append(": ").Append(c.Judge!.Pass ? "pass" : "fail").Append(" — ").AppendLine(c.Judge.Reason);
        }
        return md.ToString();
    }

    /// <summary>
    /// This run beside another — a second model, or the same model on a
    /// reworded prompt — case by case, so the question "is the new one
    /// better" is answered by a table and not by an impression.
    /// </summary>
    public string Compare(EvalReport other)
    {
        string Head(EvalReport r) => $"{r.Provider}/{r.Model}" + (r.PromptVersions.Count > 0
            ? " (" + string.Join(", ", r.PromptVersions.OrderBy(p => p.Key).Select(p => $"{p.Key} v{p.Value}")) + ")"
            : "");
        static string Mark(CaseResult? c) => c is null ? "–" : !c.Ran ? "not run" : c.Pass ? "pass" : "**fail**";

        var md = new StringBuilder();
        md.AppendLine("# AI eval · side by side").AppendLine();
        md.Append("- A: ").Append(Head(this)).Append(" · ").Append(RunAtUtc.ToString("yyyy-MM-dd HH:mm")).Append(" UTC · ").AppendLine(SpendLine);
        md.Append("- B: ").Append(Head(other)).Append(" · ").Append(other.RunAtUtc.ToString("yyyy-MM-dd HH:mm")).Append(" UTC · ").AppendLine(other.SpendLine);
        md.AppendLine();

        var mine = Cases.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var theirs = other.Cases.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var ids = mine.Keys.Union(theirs.Keys).OrderBy(id => id, StringComparer.Ordinal).ToList();

        md.AppendLine("| Feature | A passed | B passed | Cases |");
        md.AppendLine("|---|---:|---:|---:|");
        foreach (var g in ids.GroupBy(id => id[..id.IndexOf('/')]))
        {
            md.Append("| ").Append(g.Key)
                .Append(" | ").Append(g.Count(id => mine.TryGetValue(id, out var c) && c.Ran && c.Pass))
                .Append(" | ").Append(g.Count(id => theirs.TryGetValue(id, out var c) && c.Ran && c.Pass))
                .Append(" | ").Append(g.Count()).AppendLine(" |");
        }

        md.AppendLine().AppendLine("| Case | A | B | A ms | B ms |");
        md.AppendLine("|---|---|---|---:|---:|");
        foreach (var id in ids)
        {
            mine.TryGetValue(id, out var a);
            theirs.TryGetValue(id, out var b);
            md.Append("| ").Append(id).Append(" | ").Append(Mark(a)).Append(" | ").Append(Mark(b))
                .Append(" | ").Append(a is { Ran: true } ? a.LatencyMs.ToString("N0") : "–")
                .Append(" | ").Append(b is { Ran: true } ? b.LatencyMs.ToString("N0") : "–").AppendLine(" |");
        }

        var changed = ids.Where(id => mine.TryGetValue(id, out var a) && theirs.TryGetValue(id, out var b)
            && a.Ran && b.Ran && a.Pass != b.Pass).ToList();
        if (changed.Count > 0)
        {
            md.AppendLine().AppendLine("## Where they differ").AppendLine();
            foreach (var id in changed)
            {
                var a = mine[id];
                var b = theirs[id];
                md.Append("### ").AppendLine(id);
                md.Append("- A ").Append(a.Pass ? "passed" : "failed: " + string.Join("; ", a.Failures.Concat(a.Judge is { Pass: false } ja ? ["judge: " + ja.Reason] : []))).AppendLine();
                md.Append("- B ").Append(b.Pass ? "passed" : "failed: " + string.Join("; ", b.Failures.Concat(b.Judge is { Pass: false } jb ? ["judge: " + jb.Reason] : []))).AppendLine();
                md.AppendLine();
            }
        }
        return md.ToString();
    }

    private static string Indent(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc.RootElement, Json);
        }
        catch (JsonException)
        {
            return json;
        }
    }
}
