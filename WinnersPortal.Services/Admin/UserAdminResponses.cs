namespace WinnersPortal.Services.Admin;

/// <summary>Every account, as the users screen lists them.</summary>
public sealed record UserListResponse
{
    public required List<UserRow> Users { get; init; }
}

/// <summary>The account an administrator created, and whether an invitation went out instead of a password.</summary>
public sealed record CreateUserResponse
{
    public required UserRow? User { get; init; }
    public required bool Invited { get; init; }
}

/// <summary>Where a password link was sent, until when it works, and whether it was an invitation.</summary>
public sealed record ResetLinkResponse
{
    public required string SentTo { get; init; }
    public required DateTimeOffset? ExpiresAtUtc { get; init; }
    public required bool Invited { get; init; }
}

/// <summary>What erasing an account took with it.</summary>
public sealed record EraseUserResponse
{
    public required string Outcome { get; init; }
    public required int OpportunitiesCancelled { get; init; }
    public required int EntriesWithdrawn { get; init; }
}
