namespace WinnersPortal.Services.Database;

/// <summary>The database the portal is on, and how it came to be chosen.</summary>
public sealed record DatabaseStatusResponse
{
    /// <summary>postgres or sqlserver.</summary>
    public required string Provider { get; init; }
    public required string ProviderLabel { get; init; }
    /// <summary>override or environment — the file beside the keys (the setup page, this page or a move wrote it), or the environment.</summary>
    public required string Source { get; init; }
    public required string Server { get; init; }
    public required string Database { get; init; }
    /// <summary>What the server said it was, read at startup; null when it was not asked.</summary>
    public required string? ServerVersion { get; init; }
    /// <summary>Another database or connection is recorded and the process has not restarted onto it yet.</summary>
    public required bool RestartPending { get; init; }
    /// <summary>Where a move writes its choice — the file to delete to return to the environment's database.</summary>
    public required string OverridePath { get; init; }
    /// <summary>The running connection as the form shows it; never its password.</summary>
    public required DatabaseConnectionView Connection { get; init; }
    public required DatabaseMoveResponse Move { get; init; }
}

/// <summary>What a candidate database is, and what the asking screen may do with it.</summary>
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
    /// <summary>The portal's tables, with rows in them.</summary>
    public required bool HoldsPortal { get; init; }
    /// <summary>Its secret settings open with this portal's keys; null when nothing could say.</summary>
    public required bool? SamePortal { get; init; }
    /// <summary>connect, switch or move — what the screen may do next; null when nothing.</summary>
    public required string? Action { get; init; }
    /// <summary>Why nothing may be done, in a sentence; null when something may.</summary>
    public required string? Problem { get; init; }
    /// <summary>A caution to show beside the action (an older copy, missing keys); null when there is none.</summary>
    public required string? Warning { get; init; }
    /// <summary>What the tested database holds, when it holds the portal's data.</summary>
    public required DatabaseFreshness? Target { get; init; }
    /// <summary>What the database in use holds, for the comparison; null when it was not asked.</summary>
    public required DatabaseFreshness? Current { get; init; }
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
