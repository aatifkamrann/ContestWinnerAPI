namespace WinnersPortal.Domain;

/// <summary>
/// One day's provider calls and tokens for one feature on one model: the
/// rows the AI usage panel adds up, by feature and by model, and the rows
/// a daily spend budget is measured against once the model has a price.
/// Tokens are what a provider bills — the words sent and the words
/// answered, counted by the provider in its own answer — so the row
/// carries both, as the provider reported them, and never a price: prices
/// change, and are applied when the row is read.
/// </summary>
public sealed class AiSpend
{
    /// <summary>The UTC calendar day, "2026-10-01".</summary>
    public string Day { get; set; } = "";

    /// <summary>The feature, spelled the way its settings switch is ("entryDigest"), or "settingsTest" for the settings screen's ping.</summary>
    public string Feature { get; set; } = "";

    /// <summary>"gemini", "anthropic" or "openai".</summary>
    public string Provider { get; set; } = "";

    public string Model { get; set; } = "";

    /// <summary>Calls the provider answered that day, failures with a bill included; a call it never ran is not here.</summary>
    public int Calls { get; set; }

    /// <summary>Tokens sent, as the provider counted them.</summary>
    public long InputTokens { get; set; }

    /// <summary>Tokens answered, a thinking model's reasoning included — it is billed as output.</summary>
    public long OutputTokens { get; set; }
}
