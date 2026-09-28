namespace WinnersPortal.Services.Storage;

/// <summary>A packaged entry: where to download it, and for how long.</summary>
public sealed record EntryZipResponse
{
    public required string Url { get; init; }
    public required string FileName { get; init; }
    public required long? SizeBytes { get; init; }
    public required DateTimeOffset? PackagedAtUtc { get; init; }
    public required int ExpiresInSeconds { get; init; }
}
