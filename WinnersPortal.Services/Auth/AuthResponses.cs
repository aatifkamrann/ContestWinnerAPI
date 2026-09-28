using WinnersPortal.Services.Common;

namespace WinnersPortal.Services.Auth;

/// <summary>Where a fresh confirmation code went, and whether the text message failed.</summary>
public sealed record CodeSentResponse
{
    public required CodeDestination SentTo { get; init; }
    public required bool PhoneFailed { get; init; }
}

/// <summary>Where the confirmation codes went, masked for the screen, and until when they work.</summary>
public sealed record CodeDestination
{
    public required string? Email { get; init; }
    public required string? Phone { get; init; }
    public required DateTimeOffset? ExpiresAtUtc { get; init; }
}

/// <summary>The bearer client's answer: the two tokens, when each stops working, and who they are for.</summary>
public sealed record TokenPairResponse
{
    public required string TokenType { get; init; }
    public required string AccessToken { get; init; }
    public required int ExpiresInSeconds { get; init; }
    public required DateTimeOffset ExpiresAtUtc { get; init; }
    public required string RefreshToken { get; init; }
    public required DateTimeOffset RefreshExpiresAtUtc { get; init; }
    public required SessionUser User { get; init; }
}

/// <summary>The account a session was started for; after a registration, also where its codes went.</summary>
public sealed record SessionUser
{
    public required Guid Id { get; init; }
    public required string Email { get; init; }
    public required string DisplayName { get; init; }
    public required string Role { get; init; }
    public required string? AvatarUrl { get; init; }
    public required bool ConfirmationPending { get; init; }
    public required CodeDestination? SentTo { get; init; }
    public required bool? PhoneFailed { get; init; }
}

/// <summary>The signed-in account's accent colour and status colours.</summary>
public sealed record ThemeResponse
{
    public required string? Color { get; init; }
    public required Dictionary<string, string>? Status { get; init; }
}

/// <summary>The signed-in account as every page reads it.</summary>
public sealed record MeResponse
{
    public required Guid Id { get; init; }
    public required string Email { get; init; }
    public required string DisplayName { get; init; }
    public required string Role { get; init; }
    public required string? GithubLogin { get; init; }
    public required string? AvatarUrl { get; init; }
    public required bool GithubConfigured { get; init; }
    public required int? AcceptedTermsVersion { get; init; }
    public required bool TermsPending { get; init; }
    public required bool ConfirmationPending { get; init; }
    /// <summary>Where they stand with identity verification: none, pending, in_review, approved, declined.</summary>
    public required string IdentityStatus { get; init; }
    /// <summary>Whether a door of theirs asks for verification they have not got — the shell's cue to invite them.</summary>
    public required bool IdentityRequired { get; init; }
    public required string? Phone { get; init; }
    public required CodeDestination CodeSentTo { get; init; }
}

/// <summary>The terms version the account has now accepted.</summary>
public sealed record AcceptTermsResponse
{
    public required int? AcceptedTermsVersion { get; init; }
    public required bool TermsPending { get; init; }
}

/// <summary>A confirmation code that was refused, and whether a new one has to be sent.</summary>
public sealed record CodeErrorResponse : IErrorResponse
{
    public required string Error { get; init; }
    public required bool Expired { get; init; }
}

/// <summary>Asked again too soon: the sentence, and the wait in seconds.</summary>
public sealed record RetryLaterResponse : IErrorResponse
{
    public required string Error { get; init; }
    public required int RetryAfterSeconds { get; init; }
}
