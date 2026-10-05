namespace WinnersPortal.Services.Admin;

/// <summary>The operations screen: repositories in trouble, every handover, the latest webhook deliveries, the slow queries.</summary>
public sealed record OperationsResponse
{
    public required DateTimeOffset GeneratedAtUtc { get; init; }
    public required IEnumerable<ProvisioningRow> Provisioning { get; init; }
    public required IEnumerable<HandoverRow> Handovers { get; init; }
    public required List<WebhookDeliveryRow> Deliveries { get; init; }
    public required SlowQueriesSection SlowQueries { get; init; }

    /// <summary>What the AI features cost: today and the last thirty days, by feature and by model.</summary>
    public required Ai.AiUsageSection AiUsage { get; init; }
}

/// <summary>A repository that failed to provision, or is still retrying after a failure.</summary>
public sealed record ProvisioningRow
{
    public required Guid Id { get; init; }
    public required string GithubUsername { get; init; }
    public required string Freelancer { get; init; }
    public required string OpportunityTitle { get; init; }
    public required string OpportunitySlug { get; init; }
    public required string OpportunityStatus { get; init; }
    public required string Status { get; init; }
    public required int Attempts { get; init; }
    public required DateTimeOffset? LastAttemptAtUtc { get; init; }
    public required string? Note { get; init; }
    public required bool CanRetry { get; init; }
}

/// <summary>An award's paper trail: announced, paid, transfer requested, verified.</summary>
public sealed record HandoverRow
{
    public required Guid Id { get; init; }
    public required string OpportunityTitle { get; init; }
    public required string OpportunitySlug { get; init; }
    public required string Client { get; init; }
    public required string? ClientGithubLogin { get; init; }
    public required string Winner { get; init; }
    public required string? RepoFullName { get; init; }
    public required decimal Amount { get; init; }
    public required string Currency { get; init; }
    public required DateTimeOffset AnnouncedAtUtc { get; init; }
    public required DateTimeOffset? PaidAtUtc { get; init; }
    public required string Handover { get; init; }
    public required string? TransferTargetLogin { get; init; }
    public required DateTimeOffset? TransferRequestedAtUtc { get; init; }
    public required DateTimeOffset? HandoverVerifiedAtUtc { get; init; }
    public required string? Note { get; init; }
    public required bool CanRestart { get; init; }
}

/// <summary>A webhook delivery as it was received and handled.</summary>
public sealed record WebhookDeliveryRow
{
    public required Guid Id { get; init; }
    /// <summary>Who sent it: github or identity.</summary>
    public required string Source { get; init; }
    public required string DeliveryId { get; init; }
    public required string Event { get; init; }
    public required string? Action { get; init; }
    public required string? RepoFullName { get; init; }
    public required string? HandledNote { get; init; }
    public required DateTimeOffset ReceivedAtUtc { get; init; }
}

/// <summary>The slow queries since the process started or the tally was last cleared.</summary>
public sealed record SlowQueriesSection
{
    /// <summary>What counts as slow, in milliseconds; null when the slow-query log is off.</summary>
    public required int? ThresholdMs { get; init; }
    /// <summary>Whether commands that served no request — background work — are counted.</summary>
    public required bool IncludesBackground { get; init; }
    public required DateTimeOffset SinceUtc { get; init; }
    /// <summary>The costliest in all first.</summary>
    public required List<SlowQueryRow> Items { get; init; }
}

/// <summary>One query in the code — one SQL text — and how slow it has been.</summary>
public sealed record SlowQueryRow
{
    /// <summary>With placeholders where the values go; never the values.</summary>
    public required string Sql { get; init; }
    /// <summary>Query, SaveChanges, BulkUpdate, ExecuteSqlRaw…</summary>
    public required string Source { get; init; }
    public required int Count { get; init; }
    public required long TotalMs { get; init; }
    public required long AverageMs { get; init; }
    public required long MaxMs { get; init; }
    public required long LastMs { get; init; }
    public required long? LastRows { get; init; }
    /// <summary>The request it last served, as method and path, or "background work".</summary>
    public required string LastDuring { get; init; }
    public required DateTimeOffset FirstAtUtc { get; init; }
    public required DateTimeOffset LastAtUtc { get; init; }
}

/// <summary>A repository queued to provision again.</summary>
public sealed record RetryProvisionResponse
{
    public required string Status { get; init; }
}

/// <summary>A restarted repository handover: where it stands now and who it goes to.</summary>
public sealed record RestartHandoverResponse
{
    public required string Handover { get; init; }
    public required string TransferTargetLogin { get; init; }
}

/// <summary>What replaying a stored webhook delivery did.</summary>
public sealed record ReplayDeliveryResponse
{
    public required string Note { get; init; }
}
