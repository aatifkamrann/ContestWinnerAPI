namespace WinnersPortal.Services.Common;

/// <summary>
/// A process-wide pause the six workers honour and the request gate
/// enforces, for the minute a database move copies every table: nothing
/// may write to the source once the copy has begun, or the target ends up
/// a row short. A worker takes a lease for each cycle and is refused one
/// while paused; the mover pauses, waits for the leases to drain, copies,
/// and either resumes or ends the process.
/// </summary>
public sealed class AppPause
{
    private readonly object _gate = new();
    private int _active;
    private volatile string? _reason;

    public bool IsPaused => _reason is not null;

    /// <summary>What the gate tells a refused request.</summary>
    public string? Reason => _reason;

    /// <summary>Cycles under way right now — zero once drained.</summary>
    public int Active
    {
        get { lock (_gate) return _active; }
    }

    public void Pause(string reason) => _reason = reason;

    public void Resume() => _reason = null;

    /// <summary>A worker's cycle: null while paused, else a lease to dispose when the cycle ends.</summary>
    public IDisposable? TryEnter()
    {
        lock (_gate)
        {
            if (_reason is not null) return null;
            _active++;
            return new Lease(this);
        }
    }

    /// <summary>Waits until no cycle holds a lease, or the timeout passes (false).</summary>
    public async Task<bool> DrainAsync(TimeSpan timeout, CancellationToken ct)
    {
        var until = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            if (Active == 0) return true;
            if (DateTimeOffset.UtcNow >= until) return false;
            await Task.Delay(100, ct);
        }
    }

    private void Leave()
    {
        lock (_gate) _active--;
    }

    private sealed class Lease(AppPause owner) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) owner.Leave();
        }
    }
}
