using WinnersPortal.Domain;

namespace WinnersPortal.Services.Ai;

/// <summary>
/// The AI usage panel on Admin → Operations: today and the last thirty
/// days, by feature and by model, in calls, tokens and — where the model
/// has a price — dollars. Built from the day rows (<see cref="AiSpend"/>)
/// and the price list as it stands now, so a corrected price restates
/// every day in the window; the rows themselves never hold a price.
/// </summary>
public static class AiUsageReport
{
    /// <summary>Days in the window, today included.</summary>
    public const int WindowDays = 30;

    public static string FromDay(DateTimeOffset nowUtc) => AiQuotaRules.DayKey(nowUtc.AddDays(-(WindowDays - 1)));

    public static AiUsageSection Build(
        IReadOnlyList<AiSpend> rows, DateTimeOffset nowUtc, AiPriceList prices, decimal? dailyBudgetUsd, int dailyCallLimit)
    {
        var today = AiQuotaRules.DayKey(nowUtc);
        var unpriced = rows.Select(r => r.Model).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(m => prices.Find(m) is null).OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList();
        return new AiUsageSection
        {
            FromDay = FromDay(nowUtc),
            Today = Totals(rows.Where(r => r.Day == today), prices),
            Window = Totals(rows, prices),
            ByFeature = rows.GroupBy(r => r.Feature, StringComparer.Ordinal)
                .Select(g => Line(g.Key, g, prices))
                .OrderByDescending(l => l.Calls).ThenBy(l => l.Name, StringComparer.Ordinal).ToList(),
            ByModel = rows.GroupBy(r => AiProviders.CallName(r.Provider, r.Model), StringComparer.OrdinalIgnoreCase)
                .Select(g => Line(g.Key, g, prices))
                .OrderByDescending(l => l.Calls).ThenBy(l => l.Name, StringComparer.Ordinal).ToList(),
            Unpriced = unpriced,
            PricesSet = prices.Any,
            DailyBudgetUsd = dailyBudgetUsd,
            DailyCallLimit = dailyCallLimit,
        };
    }

    /// <summary>Today's estimate across every priced model — what the daily budget is measured against.</summary>
    public static decimal CostToday(IEnumerable<AiSpend> rows, string today, AiPriceList prices) =>
        rows.Where(r => r.Day == today).Sum(r => prices.Cost(r.Model, new AiTokens(r.InputTokens, r.OutputTokens)) ?? 0m);

    private static AiUsageTotals Totals(IEnumerable<AiSpend> rows, AiPriceList prices)
    {
        var list = rows.ToList();
        return new AiUsageTotals(
            list.Sum(r => r.Calls), list.Sum(r => r.InputTokens), list.Sum(r => r.OutputTokens),
            prices.Any ? list.Sum(r => prices.Cost(r.Model, new AiTokens(r.InputTokens, r.OutputTokens)) ?? 0m) : null);
    }

    private static AiUsageLine Line(string name, IEnumerable<AiSpend> rows, AiPriceList prices)
    {
        var list = rows.ToList();
        var tokens = new AiTokens(list.Sum(r => r.InputTokens), list.Sum(r => r.OutputTokens));
        // A line is priced when every model on it is; a feature answered by
        // an unpriced model one week and a priced one the next has no
        // honest number, and says so.
        var priced = list.All(r => prices.Find(r.Model) is not null);
        return new AiUsageLine(name, list.Sum(r => r.Calls), tokens.Input, tokens.Output,
            priced ? list.Sum(r => prices.Cost(r.Model, new AiTokens(r.InputTokens, r.OutputTokens)) ?? 0m) : null);
    }
}

/// <summary>The panel's data: two totals, two tables, and what the estimates leave out.</summary>
public sealed record AiUsageSection
{
    /// <summary>The first day of the window, "2026-09-02".</summary>
    public required string FromDay { get; init; }

    public required AiUsageTotals Today { get; init; }

    /// <summary>The last thirty days, today included.</summary>
    public required AiUsageTotals Window { get; init; }

    /// <summary>The window by feature, busiest first; the name is the feature's switch ("entryDigest") or "settingsTest".</summary>
    public required List<AiUsageLine> ByFeature { get; init; }

    /// <summary>The window by provider and model, busiest first.</summary>
    public required List<AiUsageLine> ByModel { get; init; }

    /// <summary>Models in the window with no price, which every estimate leaves out.</summary>
    public required List<string> Unpriced { get; init; }

    /// <summary>Whether any price is saved; while none is, no estimate is shown at all.</summary>
    public required bool PricesSet { get; init; }

    public required decimal? DailyBudgetUsd { get; init; }

    public required int DailyCallLimit { get; init; }
}

/// <summary>Calls, tokens and the priced models' estimate; the estimate is null while no price is saved.</summary>
public sealed record AiUsageTotals(int Calls, long InputTokens, long OutputTokens, decimal? CostUsd);

/// <summary>One feature's or one model's row; the estimate is null while any model on it is unpriced.</summary>
public sealed record AiUsageLine(string Name, int Calls, long InputTokens, long OutputTokens, decimal? CostUsd);
