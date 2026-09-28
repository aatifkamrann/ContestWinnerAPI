namespace WinnersPortal.Services.Opportunities;

/// <summary>A removed entry, and when its repository access ends.</summary>
public sealed record RemoveEntryResponse
{
    public required DateTimeOffset RemovedAtUtc { get; init; }
    public required DateTimeOffset? RepoAccessEndsAtUtc { get; init; }
}

/// <summary>One of the viewer's own entries, with the opportunity it is in.</summary>
public sealed record MyEntryRow
{
    public required Guid Id { get; init; }
    public required string Status { get; init; }
    public required StandingSummary? Standing { get; init; }
    public required DateTimeOffset? RemovedAtUtc { get; init; }
    public required string? RemovedReason { get; init; }
    public required DateTimeOffset? RepoAccessEndsAtUtc { get; init; }
    public required string GithubUsername { get; init; }
    public required string? RepoFullName { get; init; }
    public required string ProvisionStatus { get; init; }
    public required DateTimeOffset? LastPushAtUtc { get; init; }
    public required int MilestonesDone { get; init; }
    public required int MilestoneCount { get; init; }
    public required int FilesUploaded { get; init; }
    public required DateTimeOffset EnteredAtUtc { get; init; }
    public required bool Won { get; init; }
    public required DateTimeOffset? AwardPaidAtUtc { get; init; }
    public required FitView? Fit { get; init; }
    public required OpportunitySummary Opportunity { get; init; }
}

/// <summary>An entrant's standing score and rank.</summary>
public sealed record StandingSummary
{
    public required int Score { get; init; }
    public required string Band { get; init; }
    public required int Rank { get; init; }
    public required int Of { get; init; }
}
