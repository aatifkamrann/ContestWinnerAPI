namespace WinnersPortal.Infrastructure.Data;

/// <summary>One query shape's record since the tally began.</summary>
public sealed record SlowQueryStat(
    string Sql, string Source, int Count, long TotalMs, long MaxMs, long LastMs, long? LastRows,
    string LastDuring, DateTimeOffset FirstAtUtc, DateTimeOffset LastAtUtc);

/// <summary>
/// The slow queries the interceptor has seen, grouped by their SQL — EF
/// writes the same text for the same query shape, with placeholders where
/// the values go, so one row is one query in the code however many times it
/// ran. Kept in memory for the life of the process: writing it to the
/// database would be a write from inside the database's own interceptor,
/// and a restart is a fair place for a fresh count. The operations screen
/// reads it and can clear it, so the effect of an index or a rewrite is
/// measured from the moment it shipped.
/// </summary>
/// <remarks>
/// The threshold is the <c>limits.slowQueryMs</c> setting, and whether
/// commands that served no request count is <c>limits.slowQueryBackground</c>;
/// both are applied here by a watcher in the services tier rather than read
/// by the interceptor: a settings read can itself be a query, and the
/// interceptor runs inside every one. Until the watcher's first read, the
/// defaults hold.
/// </remarks>
public sealed class SlowQueryStats(int thresholdMs = SlowQueryStats.DefaultMs, TimeProvider? clock = null)
{
    /// <summary>The setting that holds the threshold, on Settings → Limits &amp; maintenance.</summary>
    public const string SettingKey = "limits.slowQueryMs";

    public const int DefaultMs = 500;

    /// <summary>The switch beside it: whether the workers' commands, which serve no request, are logged and tallied too.</summary>
    public const string BackgroundSettingKey = "limits.slowQueryBackground";

    public const bool DefaultIncludesBackground = true;

    /// <summary>More distinct shapes than this and the one seen longest ago makes room.</summary>
    public const int MaxShapes = 100;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<string, SlowQueryStat> _byShape = new(StringComparer.Ordinal);
    private DateTimeOffset _since = (clock ?? TimeProvider.System).GetUtcNow();
    // Read by the interceptor on every command, written by the watcher.
    private int _thresholdMs = thresholdMs;
    private bool _includesBackground = DefaultIncludesBackground;

    /// <summary>A threshold as the setting holds it: a whole number of milliseconds, 0 or more; null when it is not one.</summary>
    public static int? ParseMs(string? raw) =>
        int.TryParse(raw?.Trim(), out var ms) && ms >= 0 ? ms : null;

    /// <summary>The background switch as the setting holds it; the default when it holds nothing a switch says.</summary>
    public static bool ParseIncludesBackground(string? raw) =>
        bool.TryParse(raw?.Trim(), out var on) ? on : DefaultIncludesBackground;

    /// <summary>What counts as slow, in milliseconds; 0 when the slow-query log is off.</summary>
    public int ThresholdMs => Volatile.Read(ref _thresholdMs);

    /// <summary>Whether a slow command outside any request — the workers' — is logged and tallied.</summary>
    public bool IncludesBackground => Volatile.Read(ref _includesBackground);

    /// <summary>
    /// Counts the workers' commands in or leaves them out; answers whether
    /// that changed. A change starts the tally again, as a new threshold
    /// does: a list that still held background rows after they were left
    /// out would say the opposite of the setting.
    /// </summary>
    public bool SetIncludesBackground(bool include)
    {
        lock (_gate)
        {
            if (include == _includesBackground) return false;
            Volatile.Write(ref _includesBackground, include);
            _byShape.Clear();
            _since = _clock.GetUtcNow();
            return true;
        }
    }

    /// <summary>
    /// Puts a threshold in force; answers whether it changed. A change starts
    /// the tally again — rows counted under another threshold would not mean
    /// what the screen says they mean.
    /// </summary>
    public bool SetThreshold(int ms)
    {
        if (ms < 0) throw new ArgumentOutOfRangeException(nameof(ms), ms, "A threshold is 0 (off) or more.");
        lock (_gate)
        {
            if (ms == _thresholdMs) return false;
            Volatile.Write(ref _thresholdMs, ms);
            _byShape.Clear();
            _since = _clock.GetUtcNow();
            return true;
        }
    }

    public DateTimeOffset SinceUtc { get { lock (_gate) return _since; } }

    public void Record(string sql, string source, long ms, long? rows, string during)
    {
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            if (_byShape.TryGetValue(sql, out var s))
            {
                _byShape[sql] = s with
                {
                    Count = s.Count + 1, TotalMs = s.TotalMs + ms, MaxMs = Math.Max(s.MaxMs, ms), LastMs = ms,
                    LastRows = rows, LastDuring = during, LastAtUtc = now,
                };
                return;
            }
            if (_byShape.Count >= MaxShapes)
                _byShape.Remove(_byShape.MinBy(p => p.Value.LastAtUtc).Key);
            _byShape[sql] = new SlowQueryStat(sql, source, 1, ms, ms, ms, rows, during, now, now);
        }
    }

    /// <summary>The shapes costing the most time in all, first.</summary>
    public IReadOnlyList<SlowQueryStat> Snapshot(int take)
    {
        lock (_gate)
            return _byShape.Values.OrderByDescending(s => s.TotalMs).ThenByDescending(s => s.LastAtUtc).Take(take).ToList();
    }

    /// <summary>Starts the count again from now.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _byShape.Clear();
            _since = _clock.GetUtcNow();
        }
    }
}
