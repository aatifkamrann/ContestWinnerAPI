namespace WinnersPortal.Services.Activity;

/// <summary>A page of the activity log, newest first, with the count the filters match.</summary>
public sealed record ActivityLogResponse
{
    public required int Total { get; init; }
    public required int Page { get; init; }
    public required int Size { get; init; }
    public required IEnumerable<ActivityLogRow> Rows { get; init; }
}

/// <summary>One recorded request: what was done, to what, by whom, and how it answered.</summary>
public sealed record ActivityLogRow
{
    public required long Id { get; init; }
    public required DateTimeOffset AtUtc { get; init; }
    public required string Kind { get; init; }
    public required string Action { get; init; }
    public required string Method { get; init; }
    public required string Path { get; init; }
    public required string? Page { get; init; }
    public required string? Subject { get; init; }
    public required string? Detail { get; init; }
    public required int Status { get; init; }
    public required string? Ip { get; init; }
    public required string? UserAgent { get; init; }
    public required string? Visitor { get; init; }
    public required Guid? UserId { get; init; }
    public required ActivityLogUser? User { get; init; }

    /// <summary>For a third-party call: which kind of service (ai, identity, github, …) and how long it took.</summary>
    public required string? Service { get; init; }
    public required int? DurationMs { get; init; }
}

/// <summary>What one third-party call sent and what came back, secrets masked.</summary>
public sealed record ActivityExchangeResponse
{
    public required long Id { get; init; }
    public required string? Request { get; init; }
    public required string? Response { get; init; }
}

/// <summary>The AI providers the portal can call, and every provider and model the log's AI calls were made with.</summary>
public sealed record ActivityAiModelsResponse
{
    public required IEnumerable<ActivityAiProvider> Providers { get; init; }
    public required IEnumerable<ActivityAiModel> Models { get; init; }
}

/// <summary>One AI provider as the settings name it: its key and its label.</summary>
public sealed record ActivityAiProvider
{
    public required string Key { get; init; }
    public required string Label { get; init; }
}

/// <summary>One provider and model AI calls were made with, how many, and the last one's time.</summary>
public sealed record ActivityAiModel
{
    /// <summary>"openai/gpt-5.5" — what the row's Subject says, and what the Model filter sends back.</summary>
    public required string Id { get; init; }
    public required string Provider { get; init; }
    public required string ProviderLabel { get; init; }
    public required string Model { get; init; }
    public required int Calls { get; init; }
    public required DateTimeOffset LastAtUtc { get; init; }
}

/// <summary>The account a row belongs to, as it reads now; an erased account keeps only its id.</summary>
public sealed record ActivityLogUser
{
    public required Guid? Id { get; init; }
    public required string DisplayName { get; init; }
    public required string? Email { get; init; }
    public required string? Role { get; init; }
    public required bool Erased { get; init; }
    public required string? AvatarUrl { get; init; }
}
