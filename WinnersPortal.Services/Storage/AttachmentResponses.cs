namespace WinnersPortal.Services.Storage;

/// <summary>A reserved upload: the file's id, which names where its bytes go next, and the name it is kept under.</summary>
public sealed record UploadSlotResponse
{
    public required Guid Id { get; init; }
    public required string FileName { get; init; }
}

/// <summary>An opportunity's brief attachments.</summary>
public sealed record AttachmentListResponse
{
    public required IEnumerable<AttachmentSummary> Items { get; init; }
}

/// <summary>A brief file as the opportunity page and the draft editor list it.</summary>
public sealed record AttachmentSummary
{
    public required Guid Id { get; init; }
    public required string FileName { get; init; }
    public required string ContentType { get; init; }
    public required long SizeBytes { get; init; }
    public required DateTimeOffset? UploadedAtUtc { get; init; }
    public required string? Preview { get; init; }
}
