using WinnersPortal.Services.Ai;

namespace WinnersPortal.AiEvals;

/// <summary>
/// The case's own answers against the case's own checks (<see cref="CaseAnswers"/>):
/// the good one must pass all of them, and each bad one must fail with the
/// words it names. Pure and model-free; the test suite runs it over every
/// case on disk, and <c>--replay</c> prints what it finds.
/// </summary>
public static class EvalAnswers
{
    public static IReadOnlyList<string> Problems(EvalCase c)
    {
        if (c.Answers is not { } answers) return [$"{c.Id}: has no answers"];
        var problems = new List<string>();
        var (_, good) = EvalChecks.Run(c, answers.Good);
        problems.AddRange(good.Select(f => $"{c.Id}: the good answer fails — {f}"));
        // A good answer is one the model should write: one that passes only
        // after the gate repaired it is a slip, however good what is left.
        if (AiOutputs.Validate(c.Feature, answers.Good, out _) is { } gated)
            problems.AddRange(EvalRepairs.Find(c.Feature, answers.Good, gated).Select(r => $"{c.Id}: the good answer leans on the gate — {r}"));

        for (var i = 0; i < answers.Bad.Count; i++)
        {
            var bad = answers.Bad[i];
            var label = $"{c.Id}: bad answer {i + 1}" + (bad.Why is { } why ? $" ({why})" : "");
            var (_, failures) = EvalChecks.Run(c, bad.Text);
            if (failures.Count == 0)
            {
                problems.Add($"{label} passes every check, so the checks would not catch it");
                continue;
            }
            foreach (var word in bad.Fails.Where(w => !failures.Any(f => f.Contains(w, StringComparison.OrdinalIgnoreCase))))
                problems.Add($"{label} fails, but no failure says \"{word}\": {string.Join("; ", failures)}");
        }
        return problems;
    }
}

/// <summary>
/// The model under test, played by the cases themselves: each prompt is
/// answered with its case's good answer. A <c>--replay</c> run goes through
/// the whole tool — prompt built, validator, checks, report — with no key
/// and no call, which proves the tool and the cases, never the prompts.
/// </summary>
public sealed class ReplayModel : IEvalModel
{
    public const string Name = "replay";

    private readonly Dictionary<string, string> answers = new(StringComparer.Ordinal);

    public ReplayModel(IEnumerable<EvalCase> cases)
    {
        foreach (var c in cases)
        {
            if (c.Answers is null) continue;
            var (system, user) = EvalFeatures.Build(c.Feature, c.Input).Prompt;
            var key = system + "\n" + user;
            if (answers.TryGetValue(key, out var other) && other != c.Answers.Good)
                throw new InvalidOperationException($"{c.Id} builds the same prompt as another case with a different good answer.");
            answers[key] = c.Answers.Good;
        }
    }

    public string Provider => Name;

    public string Model => "case answers";

    public Task<(string Text, int LatencyMs, AiTokens? Tokens)> CompleteAsync(string system, string user, object schema, CancellationToken ct) =>
        answers.TryGetValue(system + "\n" + user, out var text)
            ? Task.FromResult((text, 0, (AiTokens?)null))
            : throw new AiProviderException(0, "No case answer was written for this prompt.");
}
