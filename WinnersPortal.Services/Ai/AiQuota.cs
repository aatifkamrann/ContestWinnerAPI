using WinnersPortal.Services.Settings;

namespace WinnersPortal.Services.Ai;

/// <summary>
/// The daily call ceiling, backed by two settings rows. Every provider call
/// — worker jobs and the admin's test button alike — consumes through here,
/// so the count the ceiling compares against is the count that actually
/// left the building.
/// </summary>
public sealed class AiQuota(SettingsService settings, AiOptions ai)
{
    private const string DateKey = "system.aiCallsDate";
    private const string CountKey = "system.aiCallsCount";

    /// <summary>True and counted, or false — the caller skips, never queues.</summary>
    public async Task<bool> TryConsumeAsync(CancellationToken ct)
    {
        var (allowed, date, count) = AiQuotaRules.Consume(
            await settings.GetAsync(DateKey, ct),
            await settings.GetAsync(CountKey, ct),
            DateTimeOffset.UtcNow,
            await ai.DailyCallLimitAsync(ct));
        if (!allowed) return false;
        await settings.SetManyAsync(new Dictionary<string, string?>
        {
            [DateKey] = date,
            [CountKey] = count.ToString(),
        }, changedBy: "ai-worker", allowSystem: true, ct: ct);
        return true;
    }

    /// <summary>
    /// Uncount a call the provider refused before running it — a 429 or a
    /// 5xx costs nothing, and a ceiling that counts those is a spend limit
    /// measuring the wrong thing. A refusal the request itself earned (a
    /// 4xx) is not refunded: that one was ours.
    /// </summary>
    public async Task RefundAsync(CancellationToken ct)
    {
        var (date, count) = AiQuotaRules.Refund(
            await settings.GetAsync(DateKey, ct),
            await settings.GetAsync(CountKey, ct),
            DateTimeOffset.UtcNow);
        await settings.SetManyAsync(new Dictionary<string, string?>
        {
            [DateKey] = date,
            [CountKey] = count.ToString(),
        }, changedBy: "ai-worker", allowSystem: true, ct: ct);
    }

    public async Task<(int Used, int Limit)> StateAsync(CancellationToken ct)
    {
        var today = AiQuotaRules.DayKey(DateTimeOffset.UtcNow);
        var used = string.Equals(await settings.GetAsync(DateKey, ct), today, StringComparison.Ordinal)
            && int.TryParse(await settings.GetAsync(CountKey, ct), out var n) && n > 0 ? n : 0;
        return (used, await ai.DailyCallLimitAsync(ct));
    }
}
