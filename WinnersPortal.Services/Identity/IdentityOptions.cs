using WinnersPortal.Domain;
using WinnersPortal.Services.Settings;

namespace WinnersPortal.Services.Identity;

/// <summary>
/// The identity settings as the doors read them. Every <c>identity.*</c>
/// read goes through here, so "is verification on, and for what" is one
/// class to audit. The provider's connection is one setup's fields; the
/// switches govern every setup at once.
/// </summary>
public sealed class IdentityOptions(SettingsService settings)
{
    /// <summary>Master switch. While false, no door asks and nothing leaves the server.</summary>
    public Task<bool> IsEnabledAsync(CancellationToken ct = default) => BoolAsync(IdentityKeys.Enabled, ct);

    public Task<bool> RequiredForPublishAsync(CancellationToken ct = default) => BoolAsync(IdentityKeys.RequireForPublish, ct);
    public Task<bool> RequiredForApplyAsync(CancellationToken ct = default) => BoolAsync(IdentityKeys.RequireForApply, ct);
    public Task<bool> RequiredForPaymentsAsync(CancellationToken ct = default) => BoolAsync(IdentityKeys.RequireForPayments, ct);

    /// <summary>Whether this role has a door that asks — what the shell shows a member who has not verified.</summary>
    public async Task<bool> RequiredForRoleAsync(string? role, CancellationToken ct = default)
    {
        if (!await IsEnabledAsync(ct)) return false;
        return role switch
        {
            Roles.Client => await RequiredForPublishAsync(ct),
            Roles.Freelancer => await RequiredForApplyAsync(ct) || await RequiredForPaymentsAsync(ct),
            _ => false,
        };
    }

    /// <summary>The active setup's connection, or null when none is active or the active one has no key.</summary>
    public async Task<IdentityProviderConfig?> ProviderConfigAsync(CancellationToken ct = default) =>
        await settings.ActiveSetupAsync(Setups.Identity, ct) is { } active ? Config(active) : null;

    /// <summary>A named setup's connection — the test button proves one at a time, active or not.</summary>
    public async Task<IdentityProviderConfig?> ProviderConfigAsync(string setupId, CancellationToken ct = default) =>
        await settings.SetupAsync(Setups.Identity, setupId, ct) is { } setup ? Config(setup) : null;

    /// <summary>Pure: a setup's fields to a connection, or null when it has no key.</summary>
    public static IdentityProviderConfig? Config(SetupValues s)
    {
        var apiKey = s.Get(IdentityKeys.ApiKey);
        if (string.IsNullOrWhiteSpace(apiKey)) return null;
        var provider = s.Get(IdentityKeys.Provider)?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(provider)) provider = IdentityProviders.Didit;
        return new IdentityProviderConfig(
            provider, apiKey, s.Get(IdentityKeys.WorkflowId)?.Trim() ?? "", s.Get(IdentityKeys.WebhookSecret), s.Name);
    }

    private async Task<bool> BoolAsync(string key, CancellationToken ct) =>
        string.Equals(await settings.GetAsync(key, ct), "true", StringComparison.OrdinalIgnoreCase);
}

public sealed record IdentityProviderConfig(
    string Provider, string ApiKey, string WorkflowId, string? WebhookSecret, string Setup = Setups.MainName);
