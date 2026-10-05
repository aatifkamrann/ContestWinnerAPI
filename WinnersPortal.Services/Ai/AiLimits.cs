namespace WinnersPortal.Services.Ai;

/// <summary>
/// The knobs on Settings → AI automation that bound a provider call and a
/// member's spend, each with a default and a shape — and the eval tool's
/// two on Settings → AI evals, which the tool reads from the database the
/// way it reads its key. The price list and the daily budget beside them
/// are <see cref="AiPrices"/>', checked through the same door. Every one is a setting rather than a constant
/// because each is a number an operator may want to move: a slow model
/// wants a longer timeout, a free tier a smaller per-member cap.
/// </summary>
public static class AiLimits
{
    /// <summary>Seconds one attempt at the provider may take before it is abandoned and, retries allowing, tried again.</summary>
    public const string CallTimeoutKey = "ai.callTimeoutSeconds";

    /// <summary>Further attempts after a 429, a 5xx, a timed-out attempt or a connection that never answered.</summary>
    public const string RetriesKey = "ai.callRetries";

    /// <summary>Seconds the portal sends nothing after the provider has failed half of the last minute's calls; 0 never pauses.</summary>
    public const string PauseKey = "ai.pauseSeconds";

    /// <summary>Inline tool calls one member may make per UTC day; 0 refuses them all.</summary>
    public const string MemberDailyKey = "ai.memberDailyLimit";

    /// <summary>Inline tool calls one member may make in any minute; 0 refuses them all.</summary>
    public const string MemberPerMinuteKey = "ai.memberPerMinute";

    public const int DefaultCallTimeoutSeconds = 60;
    public const int MinCallTimeoutSeconds = 5;
    public const int MaxCallTimeoutSeconds = 600;
    public const int DefaultRetries = 2;
    public const int MaxRetries = 5;
    public const int DefaultPauseSeconds = 60;
    public const int MaxPauseSeconds = 3600;
    public const int DefaultMemberDaily = 30;
    public const int DefaultMemberPerMinute = 6;
    public const int MaxMemberCalls = 100000;

    /// <summary>A whole number within the range, or null for anything else — blank included.</summary>
    public static int? ParseWhole(string? value, int min, int max) =>
        int.TryParse(value?.Trim(), out var n) && n >= min && n <= max ? n : null;

    /// <summary>
    /// Why a value cannot be stored under one of these settings, or null
    /// when it can. Refused on the screen rather than read as a default
    /// later: a timeout of "abc" that quietly became sixty seconds would
    /// leave the operator believing the number on the screen.
    /// </summary>
    public static string? Problem(string key, string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        return key switch
        {
            CallTimeoutKey when ParseWhole(value, MinCallTimeoutSeconds, MaxCallTimeoutSeconds) is null =>
                $"Call timeout must be a whole number of seconds from {MinCallTimeoutSeconds} to {MaxCallTimeoutSeconds}.",
            RetriesKey when ParseWhole(value, 0, MaxRetries) is null =>
                $"Retries per call must be a whole number from 0 to {MaxRetries}.",
            PauseKey when ParseWhole(value, 0, MaxPauseSeconds) is null =>
                $"The pause must be a whole number of seconds from 0 to {MaxPauseSeconds}, where 0 never pauses.",
            MemberDailyKey when ParseWhole(value, 0, MaxMemberCalls) is null =>
                "Inline tool calls per member per day must be a whole number of 0 or more, where 0 refuses them all.",
            MemberPerMinuteKey when ParseWhole(value, 0, MaxMemberCalls) is null =>
                "Inline tool calls per member per minute must be a whole number of 0 or more, where 0 refuses them all.",
            EvalSettings.MaxCallsKey when ParseWhole(value, 1, EvalSettings.MaxMaxCalls) is null =>
                $"Calls per eval run must be a whole number from 1 to {EvalSettings.MaxMaxCalls}.",
            EvalSettings.TimeoutKey when ParseWhole(value, MinCallTimeoutSeconds, MaxCallTimeoutSeconds) is null =>
                $"The eval call timeout must be a whole number of seconds from {MinCallTimeoutSeconds} to {MaxCallTimeoutSeconds}.",
            AiRetention.Key => AiRetention.Problem(value),
            AiFeatureModels.Key => AiFeatureModels.Problem(value),
            _ => AiPrices.Problem(key, value),
        };
    }

    /// <summary>The five values as the settings hold them, each falling back to its default when unreadable.</summary>
    public static AiLimitValues Read(string? timeout, string? retries, string? pause, string? memberDaily, string? memberPerMinute) => new(
        ParseWhole(timeout, MinCallTimeoutSeconds, MaxCallTimeoutSeconds) ?? DefaultCallTimeoutSeconds,
        ParseWhole(retries, 0, MaxRetries) ?? DefaultRetries,
        ParseWhole(pause, 0, MaxPauseSeconds) ?? DefaultPauseSeconds,
        ParseWhole(memberDaily, 0, MaxMemberCalls) ?? DefaultMemberDaily,
        ParseWhole(memberPerMinute, 0, MaxMemberCalls) ?? DefaultMemberPerMinute);
}

/// <summary>The five limits in force, read live from the settings on every call.</summary>
public sealed record AiLimitValues(
    int CallTimeoutSeconds, int Retries, int PauseSeconds, int MemberDailyLimit, int MemberPerMinute)
{
    public static readonly AiLimitValues Defaults = new(
        AiLimits.DefaultCallTimeoutSeconds, AiLimits.DefaultRetries, AiLimits.DefaultPauseSeconds,
        AiLimits.DefaultMemberDaily, AiLimits.DefaultMemberPerMinute);

    /// <summary>What one logical call may take in all: every attempt at its timeout, plus the waits between them.</summary>
    public TimeSpan TotalTimeout =>
        TimeSpan.FromSeconds(CallTimeoutSeconds * (Retries + 1)) + AiResilience.BackoffAllowance;
}
