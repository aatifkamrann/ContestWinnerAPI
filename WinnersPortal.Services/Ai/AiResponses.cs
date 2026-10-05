using System.Text.Json;
using WinnersPortal.Services.Profiles;

namespace WinnersPortal.Services.Ai;

/// <summary>A draft the model wrote on the spot, signed with <see cref="AiBrand"/>'s name, never the vendor's.</summary>
public sealed record AiDraftResponse
{
    public required JsonElement Output { get; init; }
    public required string Provider { get; init; }
    public required DateTimeOffset CompletedAtUtc { get; init; }

    /// <summary>True when the answer was held from an earlier press on the same form within the hour, so no call was spent.</summary>
    public required bool Cached { get; init; }
}

/// <summary>The profile review, or why it cannot run.</summary>
public abstract record ProfileReviewResponse;

/// <summary>
/// The profile review as the page gets it: live lines and figures, worded by
/// the model where its answer has words for them.
/// </summary>
public sealed record ProfileReviewResult : ProfileReviewResponse
{
    public required string Status { get; init; }
    public required ProfileReviewOutput Output { get; init; }
    public required int OpenOpportunities { get; init; }
    public required bool Worded { get; init; }
    public required bool Stale { get; init; }
    public required string? Note { get; init; }
    public required string? Provider { get; init; }
    public required DateTimeOffset? CompletedAtUtc { get; init; }
}

/// <summary>A profile review that cannot run, and why.</summary>
public sealed record ProfileReviewSkipped : ProfileReviewResponse
{
    public required string Status { get; init; }
    public required string Note { get; init; }
}

/// <summary>A piece of AI work, or the word that none was requested.</summary>
public abstract record AiArtifactResponse;

/// <summary>AI work that was never requested for this subject.</summary>
public sealed record AiArtifactNone : AiArtifactResponse
{
    public required string Status { get; init; }
}

/// <summary>A queued piece of AI work: its state, and its output once done.</summary>
public sealed record AiArtifactView : AiArtifactResponse
{
    public required string Status { get; init; }
    public required JsonElement? Output { get; init; }
    public required string? Note { get; init; }
    public required string? Provider { get; init; }
    public required DateTimeOffset? CompletedAtUtc { get; init; }
}
