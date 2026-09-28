namespace WinnersPortal.Services.Opportunities;

/// <summary>A cancelled opportunity.</summary>
public sealed record CancelResponse
{
    public required string Status { get; init; }
    public required DateTimeOffset? CancelledAtUtc { get; init; }
}
