namespace WinnersPortal.Services.Database;

/// <summary>The database the portal is on, and how it came to be chosen.</summary>
public sealed record DatabaseStatusResponse
{
    /// <summary>postgres or sqlserver.</summary>
    public required string Provider { get; init; }
    public required string ProviderLabel { get; init; }
    /// <summary>override, environment or default — the file a move wrote, the environment, or the development fallback.</summary>
    public required string Source { get; init; }
    public required string Server { get; init; }
    public required string Database { get; init; }
    /// <summary>What the server said it was, read at startup; null when it was not asked.</summary>
    public required string? ServerVersion { get; init; }
    /// <summary>A move has recorded another database and the process has not restarted onto it yet.</summary>
    public required bool RestartPending { get; init; }
    /// <summary>Where a move writes its choice — the file to delete to return to the environment's database.</summary>
    public required string OverridePath { get; init; }
    public required DatabaseMoveResponse Move { get; init; }
}

/// <summary>A database to test or move to: the provider word and its connection string.</summary>
public sealed record DatabaseTargetRequest(string? Provider, string? ConnectionString);

/// <summary>What a candidate database is, in the four facts a move needs before it starts.</summary>
public sealed record DatabaseTestResponse
{
    public required bool Reachable { get; init; }
    public required string? ServerVersion { get; init; }
    public required bool VersionOk { get; init; }
    public required bool FullTextOk { get; init; }
    /// <summary>False when the server is reachable but the named database is not there yet — a move creates it.</summary>
    public required bool DatabaseExists { get; init; }
    /// <summary>No tables, or only the portal's own with nothing in them — the retry after a failed move.</summary>
    public required bool Empty { get; init; }
    /// <summary>Why a move cannot start, in a sentence; null when it can.</summary>
    public required string? Problem { get; init; }
}

/// <summary>Where a move stands; polled by the page driving it.</summary>
public sealed record DatabaseMoveResponse
{
    /// <summary>idle, checking, pausing, preparing, copying, verifying, recording, restarting, failed.</summary>
    public required string Phase { get; init; }
    public required string? Target { get; init; }
    public required string? TargetServer { get; init; }
    public required string? TargetDatabase { get; init; }
    public required string? CurrentTable { get; init; }
    public required int TablesDone { get; init; }
    public required int TablesTotal { get; init; }
    public required long RowsCopied { get; init; }
    public required string? Error { get; init; }
    public required DateTimeOffset? StartedAtUtc { get; init; }
    public required DateTimeOffset? FinishedAtUtc { get; init; }
    public required string? StartedBy { get; init; }
    public required bool RestartPending { get; init; }
}
