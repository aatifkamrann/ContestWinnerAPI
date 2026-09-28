using WinnersPortal.Services.Ai;
using System.Net;
using System.Text.Json;
using WinnersPortal.Domain;
using WinnersPortal.Services.Email;

namespace WinnersPortal.Services.Notifications;

/// <summary>What one push notification says: a title, one line, and where a tap goes.</summary>
public sealed record PushContent(string Title, string Body, string Path, string Topic);

/// <summary>
/// The wording of every push the portal sends, as pure functions — the
/// counterpart of <see cref="Emails"/>, held to a stricter budget: a
/// notification is a title and one line on a lock screen, so it says what
/// happened and the one fact that decides whether to tap.
/// </summary>
public static class Pushes
{
    /// <summary>The Web Push topic header's limit: 32 URL-safe base64 characters.</summary>
    public const int MaxTopicLength = PushMessage.MaxTopicLength;

    /// <summary>
    /// One topic per event and opportunity, so a device that was offline gets
    /// the newest message about it rather than a pile of them.
    /// </summary>
    public static string Topic(string prefix, Guid opportunityId)
    {
        var topic = prefix + opportunityId.ToString("N");
        return topic.Length <= MaxTopicLength ? topic : topic[..MaxTopicLength];
    }

    public static PushContent OpportunityOpened(
        Guid opportunityId, string title, string slug, string clientName,
        decimal amount, string currency, DateTimeOffset? deadlineUtc, DateTimeOffset? startsAtUtc = null) => new(
        Title: $"New opportunity: {title}",
        Body: $"{Emails.Money(amount, currency)} fixed award from {clientName}."
            + (startsAtUtc is { } s ? $" Starts {s:d MMM}." : "")
            + (deadlineUtc is { } d ? $" Entry closes {d:d MMM}." : ""),
        Path: $"/opportunities/{slug}",
        Topic: Topic("open-", opportunityId));

    public static PushContent WinnerAnnounced(
        Guid opportunityId, string title, string slug, string winnerName, decimal amount, string currency) => new(
        Title: $"Winner announced: {title}",
        Body: $"{winnerName} won the {Emails.Money(amount, currency)} award.",
        Path: $"/opportunities/{slug}",
        Topic: Topic("won-", opportunityId));

    /// <summary>The JSON the service worker reads. The tag is the topic, so
    /// the notification tray collapses the same way the push service does.</summary>
    public static string Payload(PushMessage m, string? icon) =>
        JsonSerializer.Serialize(new { title = m.Title, body = m.Body, path = m.Path, tag = m.Topic, icon });

    /// <summary>
    /// 404 and 410 from a push service mean the subscription no longer
    /// exists — the browser unsubscribed, or the site's permission was
    /// revoked. The device is deleted, not retried.
    /// </summary>
    public static bool Gone(HttpStatusCode status) => status is HttpStatusCode.NotFound or HttpStatusCode.Gone;
}
