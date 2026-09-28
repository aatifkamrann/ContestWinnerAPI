namespace WinnersPortal.Services.Ai;

/// <summary>
/// One model provider the portal knows how to talk to: its key in the
/// settings, its label in the dropdown, and the model a blank
/// <c>ai.model</c> resolves to. The wire format for each lives in
/// <see cref="AiProviderRequests"/>; this is the list that format switches on.
/// </summary>
public sealed record AiProvider(string Key, string Label, string DefaultModel);

/// <summary>
/// The providers offered in the AI settings group, in the order an operator
/// should consider them: the one with a free tier first, then the two that
/// need credit on the account before a key answers.
/// </summary>
public static class AiProviders
{
    public const string Gemini = "gemini";
    public const string Anthropic = "anthropic";
    public const string OpenAi = "openai";

    public static readonly IReadOnlyList<AiProvider> All =
    [
        new(Gemini, "Google Gemini", "gemini-3.6-flash"),
        new(Anthropic, "Anthropic Claude", "claude-sonnet-5"),
        new(OpenAi, "OpenAI", "gpt-5.5"),
    ];

    /// <summary>What the settings dropdown offers, in the order above.</summary>
    public static readonly IReadOnlyList<(string Value, string Label)> Choices =
        [.. All.Select(p => (p.Key, p.Label))];

    /// <summary>The provider by key, or null for anything unrecognised.</summary>
    public static AiProvider? Find(string? key) =>
        All.FirstOrDefault(p => string.Equals(p.Key, key?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The keys, quoted and joined, for the "unknown provider" line.</summary>
    public static string Named => string.Join(", ", All.Select(p => $"\"{p.Key}\""));

    /// <summary>
    /// A call's provider and model as its activity row names them, "openai/gpt-5.5":
    /// the settings' own key and model, so what the row says is what was configured.
    /// </summary>
    public static string CallName(string provider, string model) => $"{provider}/{model}";

    /// <summary>The provider key and model out of a <see cref="CallName"/>; null for anything else.</summary>
    public static (string Provider, string Model)? ParseCallName(string? name)
    {
        var slash = name?.IndexOf('/') ?? -1;
        return slash > 0 && slash < name!.Length - 1 ? (name[..slash], name[(slash + 1)..]) : null;
    }
}
