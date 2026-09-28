using WinnersPortal.Services.Common;

namespace WinnersPortal.Services.Identity;

/// <summary>A verification started: where to send the member.</summary>
public sealed record IdentityStartResponse
{
    public required string Url { get; init; }
}

/// <summary>Where a member stands, and which of their doors ask for it.</summary>
public sealed record IdentityStatusResponse
{
    /// <summary>none, pending, in_review, approved, declined.</summary>
    public required string Status { get; init; }
    public required DateTimeOffset? VerifiedAtUtc { get; init; }
    /// <summary>The provider's reason category on a decline; null otherwise.</summary>
    public required string? Note { get; init; }
    /// <summary>Whether this portal verifies anybody at all.</summary>
    public required bool Enabled { get; init; }
    /// <summary>Whether any of this member's doors asks — the shell's cue to invite them.</summary>
    public required bool Required { get; init; }
    public required IdentityDoors RequiredFor { get; init; }
}

/// <summary>Which doors ask for verification on this portal.</summary>
public sealed record IdentityDoors
{
    public required bool Publish { get; init; }
    public required bool Apply { get; init; }
    public required bool Payments { get; init; }
}

/// <summary>What the webhook receiver answers the provider.</summary>
public sealed record IdentityWebhookResponse
{
    public required bool Ok { get; init; }
    public required string Note { get; init; }
}

/// <summary>
/// The 403 a shut door answers: the sentence, and the flag the web tier
/// routes on — it sends the member to verify and back, rather than
/// showing the sentence and stopping.
/// </summary>
public sealed record VerificationRequiredResponse : IErrorResponse
{
    public required string Error { get; init; }
    public required bool VerificationRequired { get; init; }
}

/// <summary>A member's verification as an administrator reads it: the verdict, the proof, and where the copy stands.</summary>
public sealed record IdentityProofResponse
{
    /// <summary>none, pending, in_review, approved, declined — as on the member's own status.</summary>
    public required string Status { get; init; }
    public required string Provider { get; init; }
    public required string SessionId { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }
    public required DateTimeOffset? DecidedAtUtc { get; init; }
    public required string? Note { get; init; }
    public required DateTimeOffset? DecisionReadAtUtc { get; init; }
    /// <summary>The provider's whole decision, indented; null until it has been read.</summary>
    public required string? Decision { get; init; }
    /// <summary>What was read off the document and how the checks scored, in reading order.</summary>
    public required IEnumerable<IdentityProofFact> Facts { get; init; }
    public required IEnumerable<IdentityProofDocument> Documents { get; init; }
    /// <summary>Whether the worker still has the decision and its images to copy.</summary>
    public required bool ProofPending { get; init; }
    /// <summary>Why the last copy fell short; null when it did not.</summary>
    public required string? ProofError { get; init; }
}

public sealed record IdentityProofFact
{
    public required string Label { get; init; }
    public required string Value { get; init; }
}

/// <summary>One stored image; opened through GET /api/admin/identity-documents/{id}/view.</summary>
public sealed record IdentityProofDocument
{
    public required Guid Id { get; init; }
    public required string Label { get; init; }
    public required string ContentType { get; init; }
    public required long SizeBytes { get; init; }
}
