using System.Globalization;

namespace WinnersPortal.Services.Ai;

/// <summary>
/// What a model costs, as the operator typed it on Settings → AI
/// automation: one model per line, its price per million tokens sent and
/// per million answered, in US dollars — the two numbers every provider's
/// price page gives. The portal never fetches a price: a price list is a
/// vendor's to change and an operator's to copy, and a wrong number here
/// mis-states a bill, so the screen refuses a line it cannot read rather
/// than skipping it. Blank means no estimate anywhere, and a model missing
/// from the list is shown as unpriced rather than as free.
/// </summary>
public static class AiPrices
{
    public const string Key = "ai.modelPrices";

    /// <summary>The day's estimated spend the portal stops at; blank or 0 is no budget.</summary>
    public const string BudgetKey = "ai.dailyBudgetUsd";

    public const int MaxLines = 50;
    public const decimal MaxPrice = 10_000m;
    public const decimal MaxBudget = 1_000_000m;

    public const string Format =
        "one model per line: the model's name, then its price per million tokens sent, then per million answered, "
        + "in US dollars — \"gemini-3.6-flash 0.30 2.50\"";

    /// <summary>Why the list cannot be stored, or null when it can — every line read, none skipped.</summary>
    public static string? Problem(string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (key == BudgetKey)
            return BudgetNumber(value) is null
                ? $"The daily spend budget must be a number of US dollars from 0 to {MaxBudget:N0}, where blank or 0 means no budget."
                : null;
        if (key != Key) return null;
        var lines = Lines(value).ToList();
        if (lines.Count > MaxLines) return $"Model prices can list at most {MaxLines} models.";
        foreach (var (n, line) in lines)
            if (ParseLine(line) is null)
                return $"Line {n} of the model prices could not be read ({Clip(line)}). Give {Format}.";
        return null;
    }

    /// <summary>The list as typed, model names folded; a value the screen would refuse reads as the lines it could.</summary>
    public static AiPriceList Parse(string? value)
    {
        var prices = new Dictionary<string, AiPrice>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, line) in Lines(value))
            if (ParseLine(line) is { } price) prices[price.Model] = price;
        return new AiPriceList(prices);
    }

    /// <summary>A budget in dollars, or null for blank, zero and anything unreadable: no budget.</summary>
    public static decimal? ParseBudget(string? value) => BudgetNumber(value) is { } n and not 0 ? n : null;

    /// <summary>The number as typed, zero included, or null for blank and anything the screen would refuse.</summary>
    private static decimal? BudgetNumber(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim().TrimStart('$');
        return decimal.TryParse(text, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite,
                CultureInfo.InvariantCulture, out var n) && n >= 0 && n <= MaxBudget
            ? n
            : null;
    }

    /// <summary>"$0.0123" for an estimate — cents matter at these prices, so four places and no rounding to a whole cent.</summary>
    public static string Dollars(decimal usd) => "$" + usd.ToString(usd < 1 ? "0.####" : "N2", CultureInfo.InvariantCulture);

    private static IEnumerable<(int Number, string Line)> Lines(string? value)
    {
        if (value is null) yield break;
        var n = 0;
        foreach (var raw in value.Split('\n'))
        {
            n++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            yield return (n, line);
        }
    }

    /// <summary>"model 0.30 2.50", the parts split on spaces, commas or an equals sign.</summary>
    private static AiPrice? ParseLine(string line)
    {
        var parts = line.Split([' ', '\t', ',', '=', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 3) return null;
        return Price(parts[1]) is { } input && Price(parts[2]) is { } output
            ? new AiPrice(parts[0], input, output)
            : null;
    }

    private static decimal? Price(string text) =>
        decimal.TryParse(text.TrimStart('$'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n)
        && n >= 0 && n <= MaxPrice
            ? n
            : null;

    private static string Clip(string line) => line.Length <= 40 ? line : line[..40] + "…";
}

/// <summary>One model's price per million tokens sent and answered, in US dollars.</summary>
public sealed record AiPrice(string Model, decimal InputPerMillion, decimal OutputPerMillion)
{
    public decimal Cost(AiTokens tokens) =>
        (tokens.Input * InputPerMillion + tokens.Output * OutputPerMillion) / 1_000_000m;
}

/// <summary>The price list in force; empty while nothing is saved.</summary>
public sealed class AiPriceList(IReadOnlyDictionary<string, AiPrice> prices)
{
    public static readonly AiPriceList Empty = new(new Dictionary<string, AiPrice>());

    public bool Any => prices.Count > 0;

    public AiPrice? Find(string? model) => model is not null && prices.TryGetValue(model, out var p) ? p : null;

    /// <summary>The estimate for a call, or null while the model has no price.</summary>
    public decimal? Cost(string? model, AiTokens tokens) => Find(model)?.Cost(tokens);
}

/// <summary>
/// A call's tokens as the provider counted them: sent and answered. The
/// unit every provider bills by and every price page quotes — a word is
/// about one and a third of them in English, a JSON brief more.
/// </summary>
public readonly record struct AiTokens(long Input, long Output)
{
    public static readonly AiTokens None = new(0, 0);

    public long Total => Input + Output;

    public static AiTokens operator +(AiTokens a, AiTokens b) => new(a.Input + b.Input, a.Output + b.Output);

    /// <summary>"1,234 in · 567 out", as the activity row and the report print it.</summary>
    public override string ToString() => $"{Input:N0} in · {Output:N0} out";
}
