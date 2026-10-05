namespace WinnersPortal.Services.Identity;

/// <summary>The identity group's setting keys, named once.</summary>
public static class IdentityKeys
{
    public const string Enabled = "identity.enabled";
    public const string Provider = "identity.provider";
    public const string ApiKey = "identity.apiKey";
    public const string WorkflowId = "identity.workflowId";
    public const string WebhookSecret = "identity.webhookSecret";
    public const string ClientId = "identity.clientId";
    public const string RequireForPublish = "identity.requireForPublish";
    public const string RequireForApply = "identity.requireForApply";
    public const string RequireForPayments = "identity.requireForPayments";
}

/// <summary>
/// One verification provider the portal knows how to talk to. The wire
/// format in <see cref="IdentityProviderRequests"/> switches on a key the
/// settings screen offers, the way the AI providers do; the active setup's
/// provider is the one a member starts with.
/// </summary>
public sealed record IdentityProvider(string Key, string Label);

public static class IdentityProviders
{
    public const string Didit = "didit";
    public const string ShuftiPro = "shuftipro";

    public static readonly IReadOnlyList<IdentityProvider> All = [new(Didit, "Didit"), new(ShuftiPro, "Shufti Pro")];

    public static readonly IReadOnlyList<(string Value, string Label)> Choices =
        [.. All.Select(p => (p.Key, p.Label))];

    public static IdentityProvider? Find(string? key) =>
        All.FirstOrDefault(p => string.Equals(p.Key, key?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>What the screen and the log call a provider: its label, or the key itself for one the portal does not know.</summary>
    public static string LabelOf(string? key) => Find(key)?.Label ?? key ?? "";

    /// <summary>
    /// The fields each provider reads. Didit signs in with an API key, runs
    /// a workflow and signs its webhooks with a secret of their own; Shufti
    /// Pro signs in with a client ID and secret key, and signs its callbacks
    /// with that same secret key. The settings screen shows a setup only the
    /// fields its provider reads (SettingsScreen.tsx keeps the same lists).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> FieldsOf =
        new Dictionary<string, IReadOnlyList<string>>
        {
            [Didit] = [IdentityKeys.ApiKey, IdentityKeys.WorkflowId, IdentityKeys.WebhookSecret],
            [ShuftiPro] = [IdentityKeys.ClientId, IdentityKeys.ApiKey],
        };
}
