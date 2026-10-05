using WinnersPortal.Domain;
using WinnersPortal.Services.Settings;

namespace WinnersPortal.Services.Ai;

/// <summary>
/// Where one feature's call goes: the active setup and the model it asks
/// for this feature — the one named for the feature on Settings → AI
/// automation, else the setup's own, else the provider's default — and
/// the standby setup, if one is marked, that is asked instead when the
/// active provider cannot be reached. Resolved once per call by
/// <see cref="AiOptions.RouteAsync"/>, so a test can hand the caller a
/// route of its own.
/// </summary>
public sealed record AiRoute(AiProviderConfig Active, string Model, AiProviderConfig? Standby)
{
    /// <summary>The standby's own model — never the feature's, which was named for the active provider.</summary>
    public string? StandbyModel =>
        Standby is null ? null : Standby.Model ?? AiProviderRequests.DefaultModel(Standby.Provider);
}

/// <summary>A provider's answer and who gave it: the provider, model and setup that answered, and whether it was the standby.</summary>
public sealed record AiAnswer(AiCompletion Completion, string Provider, string Model, string Setup, bool Standby);

/// <summary>
/// The call every feature makes — the worker's jobs and the inline tools;
/// the settings test keeps its own, because it proves one setup alone. The
/// active setup is asked first. When it could not be reached at all — a
/// 429, a 5xx, a timed-out call, a connection that never answered, or the
/// pause after repeated failures (<see cref="AiRules.Unreached"/>) — and a
/// standby setup is marked, the standby is asked the same prompt, under
/// its own provider, key and model, and its call is its own row in the
/// activity log beside the failed one. A refusal, a rejected key or a
/// missing model is the active setup's own answer and is not passed on:
/// the standby covers a provider's bad minute, not an operator's mistake.
/// Each provider's host has its own circuit (<see cref="AiResilience"/>),
/// which is what lets the standby answer while the active one rests.
/// </summary>
public sealed class AiCaller(AiOptions ai, AiProviderClient client, ILogger<AiCaller> log)
{
    /// <summary>What the standby's activity row says after the feature and prompt version.</summary>
    public const string StandbyDetail = "standby";

    /// <summary>The feature's call along the route the settings give it right now.</summary>
    public async Task<AiAnswer> CompleteAsync(AiFeature feature, (string System, string User) prompt, CancellationToken ct)
    {
        var route = await ai.RouteAsync(feature, ct)
            ?? throw new InvalidOperationException("No AI provider key is saved — the operator can add one in settings.");
        return await CompleteAsync(route, feature, prompt, ct);
    }

    /// <summary>
    /// The feature's call along a given route. A failure the standby was
    /// not asked about, or the standby's own, is thrown as the provider
    /// threw it, naming the provider and model it came from
    /// (<see cref="AiProviderException.Provider"/>), so a billed failure
    /// is spent under the model that billed it.
    /// </summary>
    public async Task<AiAnswer> CompleteAsync(AiRoute route, AiFeature feature, (string System, string User) prompt, CancellationToken ct)
    {
        var schema = AiOutputs.Schema(feature);
        var detail = AiPrompts.CallDetail(feature);
        var active = route.Active;
        Exception unreached;
        try
        {
            var answer = await client.CompleteAsync(
                active.Provider, route.Model, active.ApiKey, prompt.System, prompt.User, schema, ct, detail);
            return new AiAnswer(answer, active.Provider, route.Model, active.Setup, Standby: false);
        }
        catch (AiProviderException e) when (route.Standby is null || !AiRules.Unreached(e))
        {
            e.Name(active.Provider, route.Model);
            throw;
        }
        catch (Exception e) when (route.Standby is not null && AiRules.Unreached(e))
        {
            unreached = e;
        }

        var standby = route.Standby!;
        var model = route.StandbyModel!;
        log.LogWarning(unreached, "AI {Feature}: {Provider} could not be reached; asking the standby {Standby} ({Setup}).",
            feature, active.Provider, standby.Provider, standby.Setup);
        try
        {
            var answer = await client.CompleteAsync(
                standby.Provider, model, standby.ApiKey, prompt.System, prompt.User, schema, ct, $"{detail} · {StandbyDetail}");
            return new AiAnswer(answer, standby.Provider, model, standby.Setup, Standby: true);
        }
        catch (AiProviderException e)
        {
            e.Name(standby.Provider, model);
            throw;
        }
    }
}
