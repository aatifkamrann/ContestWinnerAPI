namespace WinnersPortal.Services.Ai;

/// <summary>
/// How long an AI call's request and answer stay readable on its activity
/// row. The row is evidence that a call happened — who asked, when, which
/// provider and model, the status, the time taken, the token count — and
/// is kept as long as the activity log keeps rows; the two bodies are the
/// most personal text the log holds, a member's brief or profile or note
/// as it was sent, and the draft that came back, so they are cleared
/// sooner. Pure, so the default and the floor are tests; the sweep itself
/// is the activity writer's hourly one.
/// </summary>
public static class AiRetention
{
    public const string Key = "ai.bodyRetentionDays";
    public const int DefaultDays = 30;
    public const int MaxDays = 3650;

    /// <summary>Days to keep; 0 keeps the bodies as long as the row. Anything unreadable is the default.</summary>
    public static int Days(string? configured) =>
        AiLimits.ParseWhole(configured, 0, MaxDays) ?? DefaultDays;

    /// <summary>The moment before which a row's bodies are cleared, or null when they are kept with the row.</summary>
    public static DateTimeOffset? CutOff(string? configured, DateTimeOffset now) =>
        Days(configured) is var days and > 0 ? now.AddDays(-days) : null;

    /// <summary>Why a value cannot be stored under the setting, or null when it can.</summary>
    public static string? Problem(string? value) =>
        string.IsNullOrEmpty(value) || AiLimits.ParseWhole(value, 0, MaxDays) is not null
            ? null
            : $"AI call bodies kept must be a whole number of days from 0 to {MaxDays}, where 0 keeps them as long as the row.";
}
