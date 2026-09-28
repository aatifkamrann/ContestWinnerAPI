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
        "/api/webhooks/identity" => ExternalServices.Identity,
        _ => null,
    };

    /// <summary>The row's Action: "Webhook from GitHub".</summary>
    public static string Action(string service) => service switch
    {
        ExternalServices.GitHub => "Webhook from GitHub",
        ExternalServices.Identity => "Webhook from Didit",
        _ => "Webhook received",
    };

    /// <summary>
    /// What the delivery was about, for the row's Subject: GitHub's event
    /// and action ("pull_request · opened"), the identity provider's
    /// webhook type and status ("status.updated · Approved"). Null when
    /// the body says neither.
    /// </summary>
    public static string? Subject(string service, string? githubEvent, string? body)
    {
        var root = Parse(body);
        var parts = service == ExternalServices.GitHub
            ? new[] { githubEvent, Text(root, "action") }
            : new[] { Text(root, "webhook_type"), Text(root, "status") };
        var said = parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        return said.Count == 0 ? null : string.Join(" · ", said);
    }

    /// <summary>
    /// Whose delivery it is, where the body says: the identity provider
    /// hands back the member's id as vendor_data, which the portal put there
    /// when it opened the session. So a verdict is the member's row, and
    /// erasing the member clears what it carried.
    /// </summary>
    public static Guid? UserOf(string service, string? body) =>
        service == ExternalServices.Identity && Guid.TryParse(Text(Parse(body), "vendor_data"), out var id) ? id : null;

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
