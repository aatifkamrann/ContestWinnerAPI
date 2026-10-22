namespace WinnersPortal.Services.Ai;

/// <summary>
/// One model provider the portal knows how to talk to: its key in the
/// settings, its label in the dropdown, and the model a blank
/// <c>ai.model</c> resolves to. The wire format for each lives in
/// <see cref="AiProviderRequests"/>; this is the list that format switches on.
/// </summary>
/// <param name="DefaultBaseUrl">
/// Where a local server answers when its setup names no address of its own;
/// null for a hosted provider, whose address is fixed in
/// <see cref="AiProviderRequests"/>.
/// </param>
public sealed record AiProvider(string Key, string Label, string DefaultModel, string? DefaultBaseUrl = null)
{
    /// <summary>
    /// A server the operator runs, at an address they type: no key needed
    /// unless a gateway in front asks for one, nothing billed, and one queue
    /// shared by whoever calls it.
    /// </summary>
    public bool Local => DefaultBaseUrl is not null;
}

/// <summary>
/// The providers offered in the AI settings group, in the order an operator
/// should consider them: the one with a free tier first, then the two that
/// need credit on the account before a key answers, then a model server of
/// the operator's own.
/// </summary>
public static class AiProviders
{
    public const string Gemini = "gemini";
    public const string Anthropic = "anthropic";
    public const string OpenAi = "openai";

    /// <summary>
    /// Ollama, or any server that answers OpenAI's chat-completions request
    /// at a <c>/v1</c> address — LM Studio, vLLM, llama.cpp, a gateway in
    /// front of one. Nothing leaves the building unless the address says so.
    /// </summary>
    public const string Ollama = "ollama";

    /// <summary>A setup's server address (<see cref="AiProvider.Local"/> providers only).</summary>
    public const string BaseUrlKey = "ai.baseUrl";

    public static readonly IReadOnlyList<AiProvider> All =
    [
        new(Gemini, "Google Gemini", "gemini-3.6-flash"),
        new(Anthropic, "Anthropic Claude", "claude-sonnet-5-5"),
        new(OpenAi, "OpenAI", "gpt-5.5"),
        new(Ollama, "Ollama or another OpenAI-compatible server", "llama3.1:8b", "http://localhost:11434/v1"),
    ];

    /// <summary>The address a local setup is called at: its own, else the provider's default; null for a hosted provider.</summary>
    public static string? BaseUrl(string provider, string? saved) =>
        Find(provider) is { Local: true } p
            ? string.IsNullOrWhiteSpace(saved) ? p.DefaultBaseUrl : saved.Trim().TrimEnd('/')
            : null;

    /// <summary>
    /// Whether a key may travel to this address: over https, or to this
    /// machine. A key beside plain http to another machine would cross the
    /// network readable by anyone on the way, so it is never sent there —
    /// inside a private network the server needs no key, and none goes.
    /// </summary>
    public static bool KeyMayGoTo(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.IsLoopback);

    /// <summary>Why a server address cannot be saved, or null when it can.</summary>
    public static string? Problem(string key, string? value)
    {
        if (key is not (BaseUrlKey or EvalSettings.BaseUrlKey) || string.IsNullOrWhiteSpace(value)) return null;
        return Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
               && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment)
            ? null
            : "Server address must be an http or https address, such as http://localhost:11434/v1 — the part before /chat/completions.";
    }

    /// <summary>
    /// Why a setup's provider, address and key cannot stand together, or
    /// null when they can: a key saved beside a plain-http address on
    /// another machine is refused at the save, rather than quietly never sent.
    /// </summary>
    public static string? KeyProblem(string? provider, string? baseUrl, string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || BaseUrl(provider?.Trim().ToLowerInvariant() ?? "", baseUrl) is not { } url)
            return null;
        return KeyMayGoTo(url) ? null
            : $"A key cannot go to {url}: it would cross the network as plain text. Use an https address (a gateway in front of the server), "
              + "an address on this machine, or clear the key — a server inside your own network needs none.";
    }

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
