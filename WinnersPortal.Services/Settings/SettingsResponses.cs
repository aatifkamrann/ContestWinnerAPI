namespace WinnersPortal.Services.Settings;

/// <summary>Every settings group, as the settings screen edits them.</summary>
public sealed record SettingsResponse
{
    public required List<SettingsGroupDto> Groups { get; init; }
}

/// <summary>What this process does with Redis, and whether a saved change is waiting for the next restart.</summary>
public sealed record RedisStatusResponse
{
    /// <summary>This process started with Redis on and an address it could read.</summary>
    public required bool Active { get; init; }

    /// <summary>The connection is up right now; false while a configured Redis is down.</summary>
    public required bool Connected { get; init; }

    /// <summary>The servers this process was started with, never the password an address may carry.</summary>
    public string? Endpoints { get; init; }

    /// <summary>Started switched on, with no address or one that does not parse: running as if off.</summary>
    public required bool OnButUnusable { get; init; }

    public required bool RestartPending { get; init; }
}

/// <summary>Whether a saved JWT change is waiting for the next restart to take effect.</summary>
public sealed record JwtStatusResponse
{
    public required bool RestartPending { get; init; }
}

/// <summary>What revoking every session touched: the accounts signed out, and the refresh tokens withdrawn.</summary>
public sealed record SessionsRevokedResponse
{
    public required int Accounts { get; init; }
    public required int RefreshTokens { get; init; }
}
