using System.Text.Json;

namespace WinnersPortal.Services.Activity;

/// <summary>
/// The other direction of a third-party call: a delivery another company
/// posts to the portal — GitHub's repository events, the identity
/// provider's verdicts. Each is recorded as a third-party row the way an
/// outgoing call is, with what arrived and what the portal answered. Pure,
/// so the naming is pinned by tests.
/// </summary>
public static class ExternalWebhooks
{
    /// <summary>The service a webhook path belongs to; null for any other path.</summary>
    public static string? ServiceOf(string? path) => path?.ToLowerInvariant() switch
    {
        "/api/webhooks/github" => ExternalServices.GitHub,
        "/api/webhooks/identity" or "/api/webhooks/identity/shufti" => ExternalServices.Identity,
        _ => null,
    };

    /// <summary>The row's Action: "Webhook from GitHub"; the identity provider is told apart by the path it posted to.</summary>
    public static string Action(string service, string? path = null) => service switch
    {
        ExternalServices.GitHub => "Webhook from GitHub",
        ExternalServices.Identity when IsShufti(path) => "Webhook from Shufti Pro",
        ExternalServices.Identity => "Webhook from Didit",
        _ => "Webhook received",
    };

    private static bool IsShufti(string? path) =>
        string.Equals(path, "/api/webhooks/identity/shufti", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What the delivery was about, for the row's Subject: GitHub's event
    /// and action ("pull_request · opened"), the identity provider's
    /// webhook type and status ("status.updated · Approved"), or Shufti
    /// Pro's one event ("verification.accepted"). Null when the body says
    /// none of these.
    /// </summary>
    public static string? Subject(string service, string? githubEvent, string? body)
    {
        var root = Parse(body);
        var parts = service == ExternalServices.GitHub
            ? new[] { githubEvent, Text(root, "action") }
            : new[] { Text(root, "webhook_type"), Text(root, "status"), Text(root, "event") };
        var said = parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        return said.Count == 0 ? null : string.Join(" · ", said);
    }

    /// <summary>
    /// Whose delivery it is, where the body says: the identity provider
    /// hands back the member's id — Didit as vendor_data, Shufti Pro inside
    /// the reference — which the portal put there when it opened the
    /// session. So a verdict is the member's row, and erasing the member
    /// clears what it carried.
    /// </summary>
    public static Guid? UserOf(string service, string? body)
    {
        if (service != ExternalServices.Identity) return null;
        var root = Parse(body);
        return Guid.TryParse(Text(root, "vendor_data"), out var id)
            ? id
            : Identity.IdentityProviderRequests.UserOfReference(Text(root, "reference"));
    }

    private static JsonElement? Parse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement? root, string name) =>
        root is { ValueKind: JsonValueKind.Object } r && r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
