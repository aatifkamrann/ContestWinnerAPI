namespace WinnersPortal.Services.Identity;

/// <summary>The identity group's setting keys, named once.</summary>
public static class IdentityKeys
{
    public const string Enabled = "identity.enabled";
    public const string Provider = "identity.provider";
    public const string ApiKey = "identity.apiKey";
    public const string WorkflowId = "identity.workflowId";
    public const string WebhookSecret = "identity.webhookSecret";
    public const string RequireForPublish = "identity.requireForPublish";
    public const string RequireForApply = "identity.requireForApply";
    public const string RequireForPayments = "identity.requireForPayments";
}

/// <summary>
/// One verification provider the portal knows how to talk to. There is one
/// today; the list exists so the wire format in
/// <see cref="IdentityProviderRequests"/> switches on a key the settings
/// screen offers, the way the AI providers do.
/// </summary>
public sealed record IdentityProvider(string Key, string Label);

public static class IdentityProviders
{
    public const string Didit = "didit";

    public static readonly IReadOnlyList<IdentityProvider> All = [new(Didit, "Didit")];

    public static readonly IReadOnlyList<(string Value, string Label)> Choices =
        [.. All.Select(p => (p.Key, p.Label))];

    public static IdentityProvider? Find(string? key) =>
        All.FirstOrDefault(p => string.Equals(p.Key, key?.Trim(), StringComparison.OrdinalIgnoreCase));
}
