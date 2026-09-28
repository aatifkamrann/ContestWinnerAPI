namespace WinnersPortal.Services.Preview;

/// <summary>
/// One preview as its panel reads it: where it stands, whether the viewer
/// can start it and why not, and — once it is up — its address and the
/// entrant's notes. The address carries no ticket: Open preview mints one.
/// </summary>
public sealed record PreviewResponse
{
    /// <summary>The preview's id: the checkpoint's (a milestone) or the entry's (the final).</summary>
    public required Guid Id { get; init; }
    /// <summary>none, pending, starting, running, stopped or failed.</summary>
    public required string Status { get; init; }
    /// <summary>milestone or final.</summary>
    public required string Kind { get; init; }
    /// <summary>1-based for a milestone; null for the final.</summary>
    public required int? MilestoneNumber { get; init; }
    /// <summary>The first seven characters of the commit it runs, once known.</summary>
    public required string? Commit { get; init; }
    /// <summary>Whether Start (or Start again) would be taken now.</summary>
    public required bool CanStart { get; init; }
    /// <summary>Why it cannot start, when it cannot: builds first, no preview address, the final not yet frozen.</summary>
    public required string? StartProblem { get; init; }
    /// <summary>The preview's host name (and port, if it has one), for the panel to show; null until it runs.</summary>
    public required string? Address { get; init; }
    public required DateTimeOffset? RequestedAtUtc { get; init; }
    public required DateTimeOffset? StartedAtUtc { get; init; }
    public required DateTimeOffset? LastSeenAtUtc { get; init; }
    public required DateTimeOffset? StoppedAtUtc { get; init; }
    /// <summary>True between Stop pressed and the host taking it down.</summary>
    public required bool Stopping { get; init; }
    public required string? StopReason { get; init; }
    public required string? Error { get; init; }
    public required string? LogTail { get; init; }
    /// <summary>The entrant's PREVIEW.md at that commit, as plain text.</summary>
    public required string? Notes { get; init; }
    /// <summary>How long an unvisited preview lasts, for the panel's line under Open.</summary>
    public required int IdleMinutes { get; init; }
    /// <summary>The viewer is the entrant, reading their own notes — the panel says "your".</summary>
    public required bool ViewerIsEntrant { get; init; }
}
