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
