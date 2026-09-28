namespace WinnersPortal.Domain;

/// <summary>
/// One raw webhook delivery, straight from the blueprint: the payload goes
/// into a jsonb column and stays queryable without a migration per event
/// type. The unique index on <see cref="DeliveryId"/> is what makes stamping
/// idempotent — every sender redelivers, and a redelivery must be a no-op.
/// GitHub's deliveries came first; the identity provider's share the table
/// and are told apart by <see cref="Source"/>.
/// </summary>
public sealed class WebhookDelivery
{
    public const string GitHub = "github";
    public const string Identity = "identity";

    public Guid Id { get; set; }

    /// <summary>Who sent it: <see cref="GitHub"/> or <see cref="Identity"/>. Replay dispatches on this.</summary>
    public string Source { get; set; } = GitHub;

    /// <summary>The sender's own id for the delivery (X-GitHub-Delivery, Didit's event_id); unique, stable across redeliveries.</summary>
    public required string DeliveryId { get; set; }

    /// <summary>X-GitHub-Event: push, create, pull_request, ping… — or the identity provider's webhook_type.</summary>
    public required string Event { get; set; }

    /// <summary>The payload's action field, when the event has one.</summary>
    public string? Action { get; set; }

    /// <summary>repository.full_name, for joining deliveries to entries.</summary>
    public string? RepoFullName { get; set; }

    /// <summary>The raw payload, stored as jsonb.</summary>
    public required string Payload { get; set; }

    /// <summary>What the handler did with it — "claimed m2", "no matching entry"…</summary>
    public string? HandledNote { get; set; }

    public DateTimeOffset ReceivedAtUtc { get; set; }
}
