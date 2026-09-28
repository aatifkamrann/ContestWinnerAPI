namespace WinnersPortal.Services.Opportunities;

/// <summary>
/// The figures over the Opportunities list: how many opportunities are open for
/// entry, what their awards add up to, and how many of them close soon.
/// Pure — the endpoint hands it the open rows and the clock.
/// </summary>
public static class OpportunityStats
{
    /// <summary>
    /// "Soon" is the window the card's pulse turns orange in, so the count
    /// over the list and the cards under it agree on what is ending.
    /// </summary>
    public static readonly TimeSpan EndingWindow = TimeSpan.FromHours(72);

    public sealed record Figures(int Open, decimal Rewards, string Currency, int EndingSoon);

    /// <param name="open">Every opportunity open for entry: its award, its currency, and when entry closes.</param>
    public static Figures Of(IEnumerable<(decimal Award, string Currency, DateTimeOffset? Closes)> open, DateTimeOffset now)
    {
        var rows = open.ToList();
        var soon = now + EndingWindow;
        // One currency in practice; where there are several, the total is
        // labelled with the one most of the money is in.
        var currency = rows
            .GroupBy(r => r.Currency)
            .OrderByDescending(g => g.Sum(r => r.Award))
            .Select(g => g.Key)
            .FirstOrDefault() ?? "USD";
        return new Figures(
            rows.Count,
            rows.Sum(r => r.Award),
            currency,
            rows.Count(r => r.Closes is { } at && at <= soon));
    }
}
