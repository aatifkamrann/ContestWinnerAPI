using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Services.Settings;

namespace WinnersPortal.Services.Ai;

/// <summary>
/// The golden-set eval's switch, provider, key, call cap and timeout
/// (tests/WinnersPortal.AiEvals): five settings of their own, on Settings →
/// AI evals, apart from the AI setup so a run never spends the portal's
/// key or its daily ceiling. The portal never reads them; the tool reads
/// them straight from the portal's database, the way the API reads the
/// Redis pair before its host exists — and runs only while the switch is
/// on, so a key saved there spends nothing until somebody decides it may.
/// </summary>
/// <remarks>
/// What is saved wins. <c>WP_EVAL_PROVIDER</c>, <c>WP_EVAL_API_KEY</c> and
/// <c>WP_EVAL_MAX_CALLS</c> — the names the tool shipped with — fill in
/// only while a setting is blank; <c>WP_EVAL_APIKEY</c> and
/// <c>WP_EVAL_MAXCALLS</c>, the settings convention's own names, are read
/// after them. A <c>--provider</c> or <c>--max-calls</c> typed for one run
/// still wins over all: it is that run's choice.
/// </remarks>
public static class EvalSettings
{
    public const string Group = "eval";

    /// <summary>Off, the tool refuses to run; the portal's own AI switch has no say over it.</summary>
    public const string EnabledKey = "eval.enabled";

    public const string ProviderKey = "eval.provider";
    public const string ApiKeyKey = "eval.apiKey";

    /// <summary>The spend cap of one run, judge calls included.</summary>
    public const string MaxCallsKey = "eval.maxCalls";

    /// <summary>Seconds one eval call may take; the tool's one retry is on top.</summary>
    public const string TimeoutKey = "eval.timeoutSeconds";

    public const string ProviderEnv = "WP_EVAL_PROVIDER";
    public const string ApiKeyEnv = "WP_EVAL_API_KEY";
    public const string MaxCallsEnv = "WP_EVAL_MAX_CALLS";

    public const int DefaultMaxCalls = 100;
    public const int MaxMaxCalls = 10000;
    public const int DefaultTimeoutSeconds = 100;

    /// <summary>Where a value came from when it was saved on the settings screen.</summary>
    public const string FromSettings = "Settings → AI evals";

    /// <summary>
    /// The values as the portal's database holds them; null where nothing is
    /// saved. The model prices are the AI tab's, read along for the report's
    /// cost column; the switch is a string so "false", blank and absent read alike.
    /// </summary>
    public sealed record Saved(
        string? Provider, string? ApiKey, string? MaxCalls = null, string? TimeoutSeconds = null,
        string? Enabled = null, string? ModelPrices = null)
    {
        public bool IsEnabled => string.Equals(Enabled?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Why a run may not start, or null when it may: the switch is off, or
    /// the settings could not be read and so the switch could not be seen.
    /// A dry run sends nothing and is let through; <c>--no-settings</c> is
    /// the typed decision to run on the variables alone, and is let
    /// through too — the one way round the switch is to say so on the
    /// command line, where it shows in the shell's history.
    /// </summary>
    public static string? RunProblem(Saved? saved, string settingsNote, bool noSettings, bool dryRun)
    {
        if (dryRun || noSettings) return null;
        if (saved is null)
            return $"Settings → AI evals {settingsNote}, so whether the evals are enabled there cannot be seen; "
                + "the tool runs only while they are. Fix the portal it reads, or pass --no-settings to run on the variables alone.";
        return saved.IsEnabled ? null
            : "AI evals are switched off under Settings → AI evals. Turn on \"Enable AI evals\" there and run again; "
            + "nothing was sent.";
    }

    /// <summary>
    /// What a run uses, and where each came from: <c>--provider</c> or
    /// <c>--max-calls</c>, <see cref="FromSettings"/>, a variable's name, or
    /// <c>default</c>. The key and its source are null when there is no
    /// key anywhere.
    /// </summary>
    public sealed record Choice(
        string Provider, string ProviderFrom, string? ApiKey, string? ApiKeyFrom,
        int MaxCalls, string MaxCallsFrom, int TimeoutSeconds, string TimeoutFrom);

    /// <summary>Each value from the first source that has one: the run's flag, the saved setting, the variables, the default.</summary>
    public static Choice Choose(string? flagProvider, Saved? saved, Func<string, string?> env, int? flagMaxCalls = null)
    {
        var keyPin = SettingsRegistry.EnvVarName(ApiKeyKey);
        var callsPin = SettingsRegistry.EnvVarName(MaxCallsKey);
        var timeoutPin = SettingsRegistry.EnvVarName(TimeoutKey);
        var (provider, providerFrom) =
            Given(flagProvider) is { } flag ? (flag, "--provider")
            : Given(saved?.Provider) is { } kept ? (kept, FromSettings)
            : Given(env(ProviderEnv)) is { } named ? (named, ProviderEnv)
            : (AiProviders.Gemini, "default");
        var (key, keyFrom) =
            Given(saved?.ApiKey) is { } keptKey ? (keptKey, FromSettings)
            : Given(env(ApiKeyEnv)) is { } namedKey ? (namedKey, ApiKeyEnv)
            : Given(env(keyPin)) is { } pinnedKey ? (pinnedKey, keyPin)
            : ((string?)null, (string?)null);
        var (calls, callsFrom) =
            flagMaxCalls is { } typed ? (typed, "--max-calls")
            : Calls(saved?.MaxCalls) is { } keptCalls ? (keptCalls, FromSettings)
            : Calls(env(MaxCallsEnv)) is { } namedCalls ? (namedCalls, MaxCallsEnv)
            : Calls(env(callsPin)) is { } pinnedCalls ? (pinnedCalls, callsPin)
            : (DefaultMaxCalls, "default");
        var (timeout, timeoutFrom) =
            Seconds(saved?.TimeoutSeconds) is { } keptTimeout ? (keptTimeout, FromSettings)
            : Seconds(env(timeoutPin)) is { } pinnedTimeout ? (pinnedTimeout, timeoutPin)
            : (DefaultTimeoutSeconds, "default");
        return new Choice(provider.ToLowerInvariant(), providerFrom, key, keyFrom, calls, callsFrom, timeout, timeoutFrom);
    }

    /// <summary>
    /// The rows, the key decrypted with the portal's own ring. Throws
    /// when the database cannot be reached or the ring is not the one the
    /// key was saved under — the caller says which, and carries on without.
    /// </summary>
    public static async Task<Saved> ReadAsync(
        DatabaseProvider provider, string connectionString, IDataProtector settingsProtector, CancellationToken ct)
    {
        await using var db = new AppDbContext(AppDbContextOptions.Build(provider, connectionString));
        var rows = await db.Settings.AsNoTracking()
            .Where(s => s.Key == ProviderKey || s.Key == ApiKeyKey || s.Key == MaxCallsKey || s.Key == TimeoutKey
                || s.Key == EnabledKey || s.Key == AiPrices.Key)
            .ToListAsync(ct);
        string? Value(string key) =>
            rows.FirstOrDefault(r => r.Key == key) is { Value: { } value } row
                ? row.IsSecret ? settingsProtector.Unprotect(value) : value
                : null;
        return new Saved(Value(ProviderKey), Value(ApiKeyKey), Value(MaxCallsKey), Value(TimeoutKey), Value(EnabledKey), Value(AiPrices.Key));
    }

    private static string? Given(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static int? Calls(string? value) => AiLimits.ParseWhole(value, 1, MaxMaxCalls);

    private static int? Seconds(string? value) =>
        AiLimits.ParseWhole(value, AiLimits.MinCallTimeoutSeconds, AiLimits.MaxCallTimeoutSeconds);
}
