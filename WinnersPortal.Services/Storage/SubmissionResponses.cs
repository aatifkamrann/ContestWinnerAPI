namespace WinnersPortal.Services.Storage;

/// <summary>An entry's files, and whether the entry is frozen.</summary>
public sealed record SubmissionListResponse
{
    public required IEnumerable<SubmissionSummary> Items { get; init; }
    public required bool Frozen { get; init; }
}

/// <summary>An entrant's file as every list shows it.</summary>
public sealed record SubmissionSummary
{
    public required Guid Id { get; init; }
    public required string FileName { get; init; }
    public required string ContentType { get; init; }
    public required long SizeBytes { get; init; }
    public required DateTimeOffset? UploadedAtUtc { get; init; }
    public required int? Milestone { get; init; }
    public required string? Preview { get; init; }
    public required bool? Claimed { get; init; }
}
