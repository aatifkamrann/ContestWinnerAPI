using WinnersPortal.Services.Ai;

namespace WinnersPortal.AiEvals;

/// <summary>
/// The loop: for each case, build the prompt as the portal would, send it,
/// pass the answer through the validator and the checks, and ask the judge
/// where the case has a rubric. Every call — the judge's included — counts
/// against the cap, and the cap ends the run rather than the run ending
/// the cap: a runaway loop cannot spend more than a person said.
/// </summary>
/// <param name="prices">The saved model prices, for the cost column; <see cref="AiPriceList.Empty"/> leaves it blank.</param>
public sealed class EvalRunner(IEvalModel model, IEvalModel? judge, int maxCalls, bool dryRun, Action<string>? log = null, AiPriceList? prices = null)
{
    private readonly AiPriceList prices = prices ?? AiPriceList.Empty;
    private int calls;

    public async Task<EvalReport> RunAsync(IReadOnlyList<EvalCase> cases, CancellationToken ct)
    {
        var results = new List<CaseResult>(cases.Count);
        var capReached = false;
        foreach (var c in cases)
        {
            ct.ThrowIfCancellationRequested();
            if (dryRun)
            {
                var job = EvalFeatures.Build(c.Feature, c.Input);
                log?.Invoke($"{c.Id}: {job.Prompt.System.Length + job.Prompt.User.Length:N0} characters of prompt, not sent");
                results.Add(new CaseResult(c.Slug, c.Name, false, false, [], null, null, 0, "dry run"));
                continue;
            }
            if (calls >= maxCalls)
            {
                capReached = true;
                results.Add(new CaseResult(c.Slug, c.Name, false, false, [], null, null, 0, $"the call cap ({maxCalls}) was reached before it"));
                continue;
            }
            results.Add(await RunOneAsync(c, ct));
        }

        return new EvalReport(
            DateTimeOffset.UtcNow, model.Provider, model.Model,
            judge is null ? null : $"{judge.Provider}/{judge.Model}",
            cases.Select(c => c.Slug).Distinct().ToDictionary(s => s, s => AiPrompts.Version(EvalFeatures.Parse(s)!.Value)),
            calls, maxCalls, capReached, dryRun, results);
    }

    private async Task<CaseResult> RunOneAsync(EvalCase c, CancellationToken ct)
    {
        EvalJob job;
        try
        {
            job = EvalFeatures.Build(c.Feature, c.Input);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new CaseResult(c.Slug, c.Name, true, false, [], null, null, 0, $"the input did not build a prompt: {e.Message}");
        }

        string text;
        int latency;
        AiTokens? spent;
        try
        {
            calls++;
            (text, latency, spent) = await model.CompleteAsync(job.Prompt.System, job.Prompt.User, AiOutputs.Schema(c.Feature), ct);
        }
        catch (Exception e) when (e is AiProviderException or HttpRequestException or TaskCanceledException)
        {
            log?.Invoke($"{c.Id}: the provider did not answer — {e.Message}");
            return new CaseResult(c.Slug, c.Name, true, false, [], null, null, 0, $"the provider did not answer: {e.Message}");
        }

        // The bill: the case's tokens at the model's price, and the judge's
        // at its own; a call that counted nothing costs nothing, and an
        // unpriced model leaves the case without a number rather than a wrong one.
        var tokens = spent ?? AiTokens.None;
        var cost = prices.Cost(model.Model, tokens);
        var (canonical, failures) = EvalChecks.Run(c, text);
        var all = failures.ToList();
        JudgeResult? verdict = null;
        if (c.Expect.Judge is { } rubric && canonical is not null)
        {
            if (judge is null)
                all.Add("judge: the case has a rubric and the run has no judge (--no-judge)");
            else if (calls >= maxCalls)
                all.Add($"judge: not asked, the call cap ({maxCalls}) was reached");
            else
            {
                var (system, user) = EvalJudge.Prompt(rubric, job.CanonicalInput, canonical);
                try
                {
                    calls++;
                    var (answer, _, judged) = await judge.CompleteAsync(system, user, EvalJudge.Schema, ct);
                    verdict = EvalJudge.Parse(answer);
                    var judgeTokens = judged ?? AiTokens.None;
                    tokens += judgeTokens;
                    cost = cost is { } so_far && prices.Cost(judge.Model, judgeTokens) is { } judgeCost ? so_far + judgeCost : null;
                }
                catch (Exception e) when (e is AiProviderException or HttpRequestException or TaskCanceledException)
                {
                    verdict = new JudgeResult(false, $"the judge did not answer: {e.Message}");
                }
            }
        }

        var pass = all.Count == 0 && verdict is not { Pass: false };
        log?.Invoke($"{c.Id}: {(pass ? "pass" : "FAIL")} · {latency:N0} ms"
            + (all.Count > 0 ? " · " + string.Join("; ", all) : "")
            + (verdict is { Pass: false } v ? " · judge: " + v.Reason : ""));
        return new CaseResult(c.Slug, c.Name, true, pass, all, verdict, canonical ?? text, latency, null, tokens.Input, tokens.Output, cost);
    }
}
