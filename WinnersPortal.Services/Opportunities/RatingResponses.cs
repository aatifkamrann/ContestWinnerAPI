namespace WinnersPortal.Services.Opportunities;

/// <summary>A saved rating.</summary>
public sealed record RatingResponse
{
    public required int Stars { get; init; }
    public required string? Comment { get; init; }
    public required DateTimeOffset UpdatedAtUtc { get; init; }
}
