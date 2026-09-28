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

    /// <summary>Whether entrant code excerpts may be sent to the external provider.</summary>
    public async Task<bool> MaySendCodeAsync(CancellationToken ct = default) =>
        await IsEnabledAsync(ct) && await BoolAsync("ai.sendCodeToProvider", ct);

    /// <summary>Zero — including an unparsable value — blocks every provider call.</summary>
    public async Task<int> DailyCallLimitAsync(CancellationToken ct = default) =>
        int.TryParse(await settings.GetAsync("ai.dailyCallLimit", ct), out var n) && n >= 0 ? n : 0;

    /// <summary>Provider, model, key of the active setup — or null while none is active or it has no key.</summary>
    public async Task<AiProviderConfig?> ProviderConfigAsync(CancellationToken ct = default) =>
        await settings.ActiveSetupAsync(Setups.Ai, ct) is { } active ? Config(active) : null;

    /// <summary>One named provider setup, active or not — the settings test's.</summary>
    public async Task<AiProviderConfig?> ProviderConfigAsync(string setupId, CancellationToken ct = default) =>
        await settings.SetupAsync(Setups.Ai, setupId, ct) is { } setup ? Config(setup) : null;

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
