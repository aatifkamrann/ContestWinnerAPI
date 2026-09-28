namespace WinnersPortal.Domain;

/// <summary>
/// One browser that asked for push notifications: the subscription the push
/// service handed it, kept per device because that is what a subscription
/// is. A person with a laptop and a phone has two rows; removing one from
/// the Notifications page stops that device and no other. A device whose
/// push service says the subscription is gone (404 or 410) is deleted by
/// the worker, and its pending messages go with it.
/// </summary>
public sealed class PushDevice
{
    // Column limits: the one place a length is stated; the rules and the
    // DbContext both read it here.
    public const int MaxEndpointLength = 2000;

    public Guid Id { get; set; }

    public Guid UserId { get; set; }
    public User? User { get; set; }

    /// <summary>The push service URL for this browser. Unique: a browser that
    /// subscribes again on another account moves, it does not duplicate.</summary>
    public required string Endpoint { get; set; }

    /// <summary>The browser's P-256 public key (base64url), for payload encryption.</summary>
    public required string P256dh { get; set; }

    /// <summary>The browser's 16-byte auth secret (base64url), for the same.</summary>
    public required string Auth { get; set; }

    /// <summary>"Chrome on Windows" — what the browser said about itself, so the
    /// list on the Notifications page means something. Never trusted for anything.</summary>
    public string? Label { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    /// <summary>The last time this browser re-registered the same subscription.</summary>
    public DateTimeOffset LastSeenAtUtc { get; set; }
}

public enum PushStatus
{
    /// <summary>Waiting for the worker.</summary>
    Pending = 0,

    /// <summary>Accepted by the push service (201).</summary>
    Sent = 1,

    /// <summary>Gave up — repeated failures, or the row expired unsent.</summary>
    Failed = 2,
}

/// <summary>
/// The push outbox, the email outbox's twin: a row per device per event,
/// written in the same SaveChanges as the event it announces, drained by
/// <c>PushWorker</c> so no request ever waits on a push service. The payload
/// is stored as its parts and serialised at send time.
/// </summary>
public sealed class PushMessage
{
    // Column limits: the one place a length is stated; the rules and the
    // DbContext both read it here.
    public const int MaxTopicLength = 32;

    public Guid Id { get; set; }

    public Guid DeviceId { get; set; }
    public PushDevice? Device { get; set; }

    /// <summary>What happened, for the operator: opportunity_open, winner_announced.</summary>
    public required string Kind { get; set; }

    public required string Title { get; set; }
    public required string Body { get; set; }

    /// <summary>Portal-relative; the service worker opens it when the notification is tapped.</summary>
    public required string Path { get; set; }

    /// <summary>The Web Push topic: one per event, so a device that was offline
    /// gets the latest message about it rather than a pile.</summary>
    public required string Topic { get; set; }

    public PushStatus Status { get; set; } = PushStatus.Pending;
    public int Attempts { get; set; }
    public DateTimeOffset? AttemptedAtUtc { get; set; }
    public DateTimeOffset? SentAtUtc { get; set; }

    /// <summary>The last push-service error, for the operator.</summary>
    public string? LastError { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }
}
