using WinnersPortal.Infrastructure.Data;

namespace WinnersPortal.Services.Database;

public enum MovePhase
{
    Idle,
    Checking,
    Pausing,
    Preparing,
    Copying,
    Verifying,
    Recording,
    Restarting,
    Failed,
}

/// <summary>
/// One move at a time, and where it stands: written by the mover from its
/// background task, read by the page polling it. A singleton, since the
/// move outlives the request that started it.
/// </summary>
public sealed class DatabaseMoveState
{
    private readonly object _gate = new();

    public MovePhase Phase { get; private set; } = MovePhase.Idle;
    public DatabaseProvider? Target { get; private set; }
    public string? TargetServer { get; private set; }
    public string? TargetDatabase { get; private set; }
    public string? CurrentTable { get; private set; }
    public int TablesDone { get; private set; }
    public int TablesTotal { get; private set; }
    public long RowsCopied { get; private set; }
    public string? Error { get; private set; }
    public DateTimeOffset? StartedAtUtc { get; private set; }
    public DateTimeOffset? FinishedAtUtc { get; private set; }
    public string? StartedBy { get; private set; }

    /// <summary>The override is written and the process is about to end; Program.cs answers with exit code 3.</summary>
    public bool RestartRequested { get; private set; }

    public bool IsRunning => Phase is not (MovePhase.Idle or MovePhase.Failed or MovePhase.Restarting);

    /// <summary>Claims the single slot; false when a move is running or one has already recorded its target.</summary>
    public bool TryBegin(DatabaseProvider target, string server, string database, string by)
    {
        lock (_gate)
        {
            if (IsRunning || RestartRequested) return false;
            Phase = MovePhase.Checking;
            Target = target;
            TargetServer = server;
            TargetDatabase = database;
            CurrentTable = null;
            TablesDone = 0;
            TablesTotal = 0;
            RowsCopied = 0;
            Error = null;
            StartedAtUtc = DateTimeOffset.UtcNow;
            FinishedAtUtc = null;
            StartedBy = by;
            return true;
        }
    }

    public void SetPhase(MovePhase phase)
    {
        lock (_gate) Phase = phase;
    }

    public void SetTables(int total)
    {
        lock (_gate) TablesTotal = total;
    }

    public void TableStarted(string table)
    {
        lock (_gate) CurrentTable = table;
    }

    public void RowsAdded(long rows)
    {
        lock (_gate) RowsCopied += rows;
    }

    public void TableDone()
    {
        lock (_gate)
        {
            TablesDone++;
            CurrentTable = null;
        }
    }

    public void Recorded()
    {
        lock (_gate)
        {
            RestartRequested = true;
            Phase = MovePhase.Restarting;
            FinishedAtUtc = DateTimeOffset.UtcNow;
        }
    }

    public void Fail(string error)
    {
        lock (_gate)
        {
            Phase = MovePhase.Failed;
            Error = error;
            FinishedAtUtc = DateTimeOffset.UtcNow;
        }
    }

    public DatabaseMoveResponse Snapshot()
    {
        lock (_gate)
        {
            return new DatabaseMoveResponse
            {
                Phase = Phase.ToString().ToLowerInvariant(),
                Target = Target is { } t ? DatabaseProviders.Name(t) : null,
                TargetServer = TargetServer,
                TargetDatabase = TargetDatabase,
                CurrentTable = CurrentTable,
                TablesDone = TablesDone,
                TablesTotal = TablesTotal,
                RowsCopied = RowsCopied,
                Error = Error,
                StartedAtUtc = StartedAtUtc,
                FinishedAtUtc = FinishedAtUtc,
                StartedBy = StartedBy,
                RestartPending = RestartRequested,
            };
        }
    }
}
