using WinnersPortal.Services.Ai;
using WinnersPortal.Services.Profiles;
using WinnersPortal.Domain;

namespace WinnersPortal.Services.Settings;

/// <summary>
/// The one place anything asks whether an AI feature may run. Nothing should
/// read <c>ai.*</c> settings directly — going through here is what guarantees
/// the master switch actually governs every feature, including ones added
/// later.
/// </summary>
public sealed class AiOptions(SettingsService settings)
{
    /// <summary>A setup's switch: on an inactive setup, it is asked when the active provider cannot be reached (<see cref="AiCaller"/>).</summary>
    public const string StandbyKey = "ai.standby";

    /// <summary>Master switch. While false, nothing AI runs and nothing leaves the server.</summary>
    public Task<bool> IsEnabledAsync(CancellationToken ct = default) => BoolAsync("ai.enabled", ct);

    /// <summary>
    /// Whether a named feature may run right now. False whenever the master
    /// switch is off, regardless of the feature's own flag.
    /// </summary>
    public async Task<bool> IsFeatureEnabledAsync(AiFeature feature, CancellationToken ct = default)
    {
        if (!await IsEnabledAsync(ct)) return false;

        var key = feature switch
        {
            AiFeature.EntryDigest => "ai.features.entryDigest",
            AiFeature.MilestoneExtraction => "ai.features.milestoneExtraction",
            AiFeature.BriefCoach => "ai.features.briefCoach",
            AiFeature.SpamFilter => "ai.features.spamFilter",
            AiFeature.ProgressNarrative => "ai.features.progressNarrative",
            AiFeature.SeoMetadata => "ai.features.seoMetadata",
            AiFeature.StandingNotes => "ai.features.standingNotes",
            AiFeature.CategorySuggestion => "ai.features.categorySuggestion",
            AiFeature.RecommendedMatching => "ai.features.recommendedMatching",
            AiFeature.ProfileSummary => "ai.features.profileSummary",
            AiFeature.ProfileReview => "ai.features.profileReview",
            AiFeature.ProjectApproach => "ai.features.projectApproach",
            AiFeature.ApplicationEvaluation => "ai.features.applicationEvaluation",
            AiFeature.RequirementsSuggestion => "ai.features.requirementsSuggestion",
            AiFeature.CriteriaSuggestion => "ai.features.criteriaSuggestion",
            _ => throw new ArgumentOutOfRangeException(nameof(feature)),
        };

        if (!await BoolAsync(key, ct)) return false;

        // The spam scan runs entirely on this server — no provider, no key.
        // Everything else calls a language model and needs one configured.
        return !RequiresProvider(feature) || await ProviderConfigAsync(ct) is not null;
    }

    /// <summary>
    /// The name AI work goes out under on a member's screen: the portal's
    /// own, from Branding, never the vendor's. See <see cref="AiBrand"/>.
    /// </summary>
    public async Task<string> PublicNameAsync(CancellationToken ct = default) =>
        AiBrand.NameOf(await settings.GetAsync("branding.portalName", ct));

    /// <summary>Whether entrant code excerpts may be sent to the external provider.</summary>
    public async Task<bool> MaySendCodeAsync(CancellationToken ct = default) =>
        await IsEnabledAsync(ct) && await BoolAsync("ai.sendCodeToProvider", ct);

    /// <summary>Zero — including an unparsable value — blocks every provider call.</summary>
    public async Task<int> DailyCallLimitAsync(CancellationToken ct = default) =>
        int.TryParse(await settings.GetAsync("ai.dailyCallLimit", ct), out var n) && n >= 0 ? n : 0;

    /// <summary>
    /// The call timeout, retries, pause and the two per-member caps
    /// (<see cref="AiLimits"/>), read live — the resilience pipeline asks on
    /// every attempt, so a saved change is in force for the next call. A
    /// value the screen would have refused (a deployment's variable) reads
    /// as its default rather than as zero: these bound a call, they do not
    /// spend.
    /// </summary>
    public async Task<AiLimitValues> LimitsAsync(CancellationToken ct = default) => AiLimits.Read(
        await settings.GetAsync(AiLimits.CallTimeoutKey, ct),
        await settings.GetAsync(AiLimits.RetriesKey, ct),
        await settings.GetAsync(AiLimits.PauseKey, ct),
        await settings.GetAsync(AiLimits.MemberDailyKey, ct),
        await settings.GetAsync(AiLimits.MemberPerMinuteKey, ct));

    /// <summary>The model prices as saved (<see cref="AiPrices"/>); empty while none are, and then nothing is estimated.</summary>
    public async Task<AiPriceList> PricesAsync(CancellationToken ct = default) =>
        AiPrices.Parse(await settings.GetAsync(AiPrices.Key, ct));

    /// <summary>The day's spend budget in dollars, or null for none — blank, zero, or a value the screen would have refused.</summary>
    public async Task<decimal?> DailyBudgetUsdAsync(CancellationToken ct = default) =>
        AiPrices.ParseBudget(await settings.GetAsync(AiPrices.BudgetKey, ct));

    /// <summary>Provider, model, key of the active setup — or null while none is active or it has no key.</summary>
    public async Task<AiProviderConfig?> ProviderConfigAsync(CancellationToken ct = default) =>
        await settings.ActiveSetupAsync(Setups.Ai, ct) is { } active ? Config(active) : null;

    /// <summary>One named provider setup, active or not — the settings test's.</summary>
    public async Task<AiProviderConfig?> ProviderConfigAsync(string setupId, CancellationToken ct = default) =>
        await settings.SetupAsync(Setups.Ai, setupId, ct) is { } setup ? Config(setup) : null;

    /// <summary>
    /// A setup marked to stand by for the active one: the first inactive
    /// setup of the list with its Standby switch on and a key saved, or
    /// null while there is none. The active setup's own mark means
    /// nothing — it is the one being stood in for.
    /// </summary>
    public async Task<AiProviderConfig?> StandbyConfigAsync(CancellationToken ct = default) =>
        Standby(await settings.SetupsAsync(Setups.Ai, ct));

    /// <summary>Pure: the standby among a connection's setups, as <see cref="StandbyConfigAsync"/> picks it.</summary>
    public static AiProviderConfig? Standby(IEnumerable<SetupValues> setups) =>
        setups
            .Where(s => !s.Enabled && string.Equals(s.Get(StandbyKey)?.Trim(), "true", StringComparison.OrdinalIgnoreCase))
            .Select(Config)
            .FirstOrDefault(c => c is not null);

    /// <summary>The models named per feature (<see cref="AiFeatureModels"/>); empty while none are.</summary>
    public async Task<AiFeatureModelList> FeatureModelsAsync(CancellationToken ct = default) =>
        AiFeatureModels.Parse(await settings.GetAsync(AiFeatureModels.Key, ct));

    /// <summary>
    /// Where a feature's call goes right now (<see cref="AiRoute"/>): the
    /// active setup with the model this feature asks — the one named for
    /// the feature, else the setup's own, else the provider's default — and
    /// the standby, if one is marked. Null while no setup is active or the
    /// active one has no key.
    /// </summary>
    public async Task<AiRoute?> RouteAsync(AiFeature feature, CancellationToken ct = default)
    {
        if (await ProviderConfigAsync(ct) is not { } active) return null;
        var model = (await FeatureModelsAsync(ct)).For(feature)
            ?? active.Model
            ?? AiProviderRequests.DefaultModel(active.Provider);
        return new AiRoute(active, model, await StandbyConfigAsync(ct));
    }

    /// <summary>Pure: provider, model, key — or null while the setup has no key.</summary>
    public static AiProviderConfig? Config(SetupValues s)
    {
        var apiKey = s.Get("ai.apiKey");
        if (string.IsNullOrWhiteSpace(apiKey)) return null;
        var provider = s.Get("ai.provider")?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(provider)) provider = AiProviders.Gemini;
        var model = s.Get("ai.model")?.Trim();
        return new AiProviderConfig(provider, string.IsNullOrWhiteSpace(model) ? null : model, apiKey, s.Name);
    }

    /// <summary>The spam scan works from the portal's own data; the rest talk to a model.</summary>
    public static bool RequiresProvider(AiFeature feature) => feature is not AiFeature.SpamFilter;

    private async Task<bool> BoolAsync(string key, CancellationToken ct) =>
        string.Equals(await settings.GetAsync(key, ct), "true", StringComparison.OrdinalIgnoreCase);
}

/// <param name="Setup">The setup's name, for the log line that says which provider answered or failed.</param>
public sealed record AiProviderConfig(string Provider, string? Model, string ApiKey, string Setup = Setups.MainName);
