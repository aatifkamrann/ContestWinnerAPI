namespace WinnersPortal.Services.Preview;

/// <summary>One claimed milestone's build, for the board's dialog: where it stands, why it failed, the log's tail, and whether the whole log can be downloaded.</summary>
public sealed record CheckpointBuildResponse
{
    /// <summary>none, pending, building, built or failed.</summary>
    public required string Status { get; init; }
    /// <summary>1-based, matching the tag.</summary>
    public required int MilestoneNumber { get; init; }
    /// <summary>The first seven characters of the commit built, or null for a claim that carried none.</summary>
    public required string? Commit { get; init; }
    public required DateTimeOffset? StartedAtUtc { get; init; }
    public required DateTimeOffset? FinishedAtUtc { get; init; }
    /// <summary>Why it failed, in a line; null while it runs or when it built.</summary>
    public required string? Error { get; init; }
    /// <summary>The last lines of the log, or null before the host answered.</summary>
    public required string? LogTail { get; init; }
    /// <summary>Whether the whole log is in storage — the download link's condition.</summary>
    public required bool LogAvailable { get; init; }
}
