namespace WinnersPortal.Services.Notifications;

/// <summary>What the account hears about, how, and on which devices.</summary>
public sealed record NotificationPreferencesResponse
{
    public required bool NewOpportunities { get; init; }
    public required bool Winners { get; init; }
    public required bool ByEmail { get; init; }
    public required string PushKey { get; init; }
    public required IEnumerable<PushDeviceView> Devices { get; init; }
}

/// <summary>A device that receives push notifications.</summary>
public sealed record PushDeviceView
{
    public required Guid Id { get; init; }
    public required string? Label { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required string EndpointHash { get; init; }
}

/// <summary>The notification choices as saved.</summary>
public sealed record NotificationChoicesResponse
{
    public required bool NewOpportunities { get; init; }
    public required bool Winners { get; init; }
    public required bool ByEmail { get; init; }
}

/// <summary>A device registered for push notifications.</summary>
public sealed record DeviceRegisteredResponse
{
    public required Guid Id { get; init; }
    public required string EndpointHash { get; init; }
}

/// <summary>The email choice after following an unsubscribe link.</summary>
public sealed record UnsubscribeResponse
{
    public required bool ByEmail { get; init; }
}

/// <summary>One page of the inbox behind the bell, newest first, with its counts.</summary>
public sealed record InboxResponse
{
    public required IEnumerable<InboxLine> Items { get; init; }
    public required int Unread { get; init; }
    public required int Total { get; init; }
    /// <summary>The cursor for the page after this one; null on the last.</summary>
    public required string? NextCursor { get; init; }
}

/// <summary>One line of the inbox: what happened, and where a click goes.</summary>
public sealed record InboxLine
{
    public required Guid Id { get; init; }
    public required string Kind { get; init; }
    public required string Title { get; init; }
    public required string Body { get; init; }
    public required string? Path { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required DateTimeOffset? ReadAtUtc { get; init; }
}

/// <summary>An inbox line as marked.</summary>
public sealed record InboxLineMarkedResponse
{
    public required Guid Id { get; init; }
    public required DateTimeOffset? ReadAtUtc { get; init; }
}

/// <summary>The whole inbox as marked: how many lines changed.</summary>
public sealed record InboxMarkedResponse
{
    public required int Changed { get; init; }
}
