using WinnersPortal.AiEvals;
using WinnersPortal.Services.Ai;

// The golden-set eval. One command scores every case under evals/ against
// a model and writes a report; a second run against another model, or the
// same model on a reworded prompt, is set beside the first with --compare.
//
//   dotnet run --project tests/WinnersPortal.AiEvals -- [options]
//
//   --feature <slug>      only this feature's folder (repeatable): categorise, milestones, profile-review, entry-digest
//   --case <name>         only the case with this file name
//   --matching            the embedding eval instead: evals/matching/ profiles against the categories (no prompt is sent)
//   --provider <key>      gemini | anthropic | openai        (default: Settings → AI evals, else WP_EVAL_PROVIDER, else gemini)
//   --model <name>        the model                          (default: WP_EVAL_MODEL, else the provider's default; with --matching, its embedding model)
//   --judge-model <name>  the judge's model, same provider   (default: the model under test)
//   --no-judge            skip every rubric; cases with one fail and say so
//   --max-calls <n>       the spend cap, judge calls included (default: Settings → AI evals, else WP_EVAL_MAX_CALLS, else 100)
//   --dry-run             build every prompt, send nothing, print the sizes
//   --compare <report>    after the run, print this run beside an earlier report's JSON
//   --cases <dir>         the cases folder (default: evals/ at the repository root)
//   --out <dir>           where reports go (default: evals/reports/)
//   --keys <dir>          the portal's keys folder (default: DP_KEYS_DIR, else src/WinnersPortal.Api/keys)
//   --no-settings         do not read Settings → AI evals; the variables only, and the Enable switch unseen
//
// The run is allowed by "Enable AI evals" under Settings → AI evals: off,
// or unreadable, the tool stops before anything is sent (a dry run and
// --no-settings excepted). The key is the eval key saved there, read from
// the portal's own database with the keys folder beside the API; while
// none is saved, WP_EVAL_API_KEY. The call cap and the call timeout are
// read the same way (Calls per eval run, Eval call timeout), and the model
// prices under AI automation give the report its cost column. Never the
// portal's AI key, never a file of its own, never an argument that would
// land in a shell history. What is saved wins over the variables. A run
// with no key is refused before anything is sent.
//
// --matching is the other eval: could a vector per profile and per
// category stand in for the model's reading of which kinds of work a
// freelancer does? It embeds the profiles under evals/matching/ and the
// categories, compares every pair, and reports how far the vectors
// agree with today's readings at each threshold. The feed keeps the
// reading until a run says they agree.

var features = new List<string>();
string? caseName = null, provider = null, model = null, judgeModel = null, compare = null, casesDir = null, outDir = null, keysDir = null;
int? maxCalls = null;
bool noJudge = false, dryRun = false, noSettings = false, matching = false;

for (var i = 0; i < args.Length; i++)
{
    string Next()
    {
        if (i + 1 >= args.Length) throw new ArgumentException($"{args[i]} needs a value.");
        return args[++i];
    }
    switch (args[i])
    {
        case "--feature": features.Add(Next()); break;
        case "--case": caseName = Next(); break;
        case "--provider": provider = Next(); break;
        case "--model": model = Next(); break;
        case "--judge-model": judgeModel = Next(); break;
        case "--no-judge": noJudge = true; break;
        case "--max-calls": maxCalls = int.Parse(Next()); break;
        case "--dry-run": dryRun = true; break;
        case "--compare": compare = Next(); break;
        case "--cases": casesDir = Next(); break;
        case "--out": outDir = Next(); break;
        case "--keys": keysDir = Next(); break;
        case "--no-settings": noSettings = true; break;
        case "--matching": matching = true; break;
        case "--help" or "-h":
            Console.WriteLine(File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Program.cs"))
                .TakeWhile(l => l.StartsWith("//")).Select(l => l.TrimStart('/', ' ')).Aggregate((a, b) => a + "\n" + b));
            return 0;
        default:
            Console.Error.WriteLine($"Unknown option {args[i]}; try --help.");
            return 2;
    }
}

var root = casesDir is null || outDir is null ? EvalCases.RepoRoot() : null;
casesDir ??= Path.Combine(root!, "evals");
outDir ??= Path.Combine(casesDir, EvalCases.ReportsFolder);

IReadOnlyList<EvalCase> cases = [];
IReadOnlyList<MatchingCase> profiles = [];
try
{
    if (matching) profiles = EvalMatching.Load(casesDir, caseName);
    else cases = EvalCases.Load(casesDir, features, caseName);
}
catch (EvalCaseException e)
{
    Console.Error.WriteLine(e.Message);
    return 2;
}
if (cases.Count == 0 && profiles.Count == 0)
{
    Console.Error.WriteLine($"No cases matched under {casesDir}.");
    return 2;
}

// Settings → AI evals first: what is saved there wins over the variables.
EvalSettings.Saved? saved = null;
var settingsNote = "not read (--no-settings)";
if (!noSettings)
{
    var (read, where) = await EvalPortal.ReadAsync(EvalPortal.KeysDir(keysDir), CancellationToken.None);
    saved = read;
    settingsNote = read is null ? $"not read: {where}" : $"read from {where}";
    Console.WriteLine($"Settings → AI evals {settingsNote}.");
}
if (EvalSettings.RunProblem(saved, settingsNote, noSettings, dryRun) is { } stopped)
{
    Console.Error.WriteLine(stopped);
    return 2;
}
var choice = EvalSettings.Choose(provider, saved, Environment.GetEnvironmentVariable, maxCalls);
provider = choice.Provider;
maxCalls = choice.MaxCalls;
if (AiProviders.Find(provider) is null)
{
    Console.Error.WriteLine($"Unknown provider \"{provider}\" (from {choice.ProviderFrom}) — the portal supports {AiProviders.Named}.");
    return 2;
}
model ??= Environment.GetEnvironmentVariable("WP_EVAL_MODEL");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

if (matching)
{
    try
    {
        return await EvalMatching.RunAsync(
            profiles, provider, model, choice, AiPrices.Parse(saved?.ModelPrices), dryRun, outDir, settingsNote, cts.Token);
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine("Stopped.");
        return 130;
    }
}

if (string.IsNullOrWhiteSpace(model)) model = AiProviderRequests.DefaultModel(provider);

var apiKey = choice.ApiKey;
if (!dryRun && apiKey is null)
{
    Console.Error.WriteLine(
        "No eval key. Save one under Settings → AI evals, or set WP_EVAL_API_KEY in this shell; or pass --dry-run. "
        + $"The evals spend on a key of their own, never the portal's. (Settings → AI evals {settingsNote}.)");
    return 2;
}

Console.WriteLine($"{cases.Count} case(s) · {provider}/{model} (provider from {choice.ProviderFrom}"
    + (apiKey is null ? ", no key" : $", key from {choice.ApiKeyFrom}")
    + $") · cap {maxCalls} calls (from {choice.MaxCallsFrom}) · timeout {choice.TimeoutSeconds} s (from {choice.TimeoutFrom}){(dryRun ? " · dry run" : "")}");
var under = new HttpEvalModel(provider, model, apiKey ?? "", choice.TimeoutSeconds);
var judge = noJudge ? null : judgeModel is null ? under : new HttpEvalModel(provider, judgeModel, apiKey ?? "", choice.TimeoutSeconds);
var prices = AiPrices.Parse(saved?.ModelPrices);
if (!dryRun && !noSettings && prices.Find(model) is null)
    Console.WriteLine($"No price is saved for {model} under Settings → AI automation, so the report will count tokens and not dollars.");
var runner = new EvalRunner(under, judge, maxCalls.Value, dryRun, Console.WriteLine, prices);

EvalReport report;
try
{
    report = await runner.RunAsync(cases, cts.Token);
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Stopped.");
    return 130;
}

Console.WriteLine();
Console.WriteLine(report.ToMarkdown());

if (!dryRun)
{
    Directory.CreateDirectory(outDir);
    var stem = Path.Combine(outDir, report.FileStem);
    File.WriteAllText(stem + ".json", report.ToJson());
    File.WriteAllText(stem + ".md", report.ToMarkdown());
    Console.WriteLine($"Saved {stem}.json and .md");
}

if (compare is not null)
{
    var other = EvalReport.FromJson(File.ReadAllText(compare));
    Console.WriteLine();
    Console.WriteLine(report.Compare(other));
}

return report.Failed == 0 ? 0 : 1;
