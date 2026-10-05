namespace WinnersPortal.Services.Opportunities;

/// <summary>The announced award.</summary>
public sealed record AnnounceResponse
{
    public required Guid Id { get; init; }
    public required string Winner { get; init; }
    public required DateTimeOffset AnnouncedAtUtc { get; init; }
}

/// <summary>A paid award, and where the repository handover stands.</summary>
public sealed record MarkPaidResponse
{
    public required DateTimeOffset? PaidAtUtc { get; init; }
    public required string Handover { get; init; }
    public required string? TransferTargetLogin { get; init; }
    public required string? Note { get; init; }
}

/// <summary>Where the milestone stands after the step, and which one is open now.</summary>
public sealed record MilestonePaymentResponse
{
    public required int Number { get; init; }
    public required string State { get; init; }
    /// <summary>The milestone being worked on now, 1-based; null once every one is paid.</summary>
    public required int? Current { get; init; }
    /// <summary>This step paid the last milestone: the award is complete and the handover has started.</summary>
    public required bool Completed { get; init; }
}
