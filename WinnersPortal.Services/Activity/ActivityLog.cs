using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Domain;
using WinnersPortal.Services.Ai;
using WinnersPortal.Services.Common;
using WinnersPortal.Services.Settings;

namespace WinnersPortal.Services.Activity;

/// <summary>
/// Where a request drops its row. A channel rather than a save: the person
/// whose click is being recorded never waits on the write, and the
/// request-scoped DbContext has usually been disposed by the time the
/// middleware runs. The writer below drains it in batches. If the channel
/// is ever full — ten thousand rows the writer has not caught up with —
/// the newest are dropped, and the log says so; a log must never be the
/// thing that takes the portal down.
/// </summary>
public sealed class ActivityLog(ILogger<ActivityLog> log)
{
    private readonly Channel<ActivityEvent> _queue = Channel.CreateBounded<ActivityEvent>(
        new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    private long _dropped;

    public void Record(ActivityEvent e)
    {
        if (!_queue.Writer.TryWrite(e) && Interlocked.Increment(ref _dropped) % 1000 == 1)
            log.LogWarning("Activity log queue full; rows are being dropped ({Dropped} so far).", _dropped);
    }

    internal ChannelReader<ActivityEvent> Reader => _queue.Reader;
}

/// <summary>How long rows are kept. Pure, so the default and the floor are tests.</summary>
public static class ActivityRetention
{
    public const string Key = "limits.activityRetentionDays";
    public const int DefaultDays = 90;

    /// <summary>Days to keep; 0 keeps everything. Anything unparseable is the default.</summary>
    public static int Days(string? configured) =>
        int.TryParse(configured?.Trim(), out var days) && days >= 0 ? days : DefaultDays;

    public static DateTimeOffset? CutOff(string? configured, DateTimeOffset now) =>
        Days(configured) is var days and > 0 ? now.AddDays(-days) : null;
}

/// <summary>
/// Drains the activity queue into the database — a batch every quarter
/// second while rows arrive, nothing while they do not — and once an hour
/// deletes rows older than the retention setting. The final batch is
/// written on shutdown, so a deploy loses nothing that reached the queue.
/// </summary>
public sealed partial class ActivityWriter(
    IServiceScopeFactory scopes,
    ActivityLog queue,
    SettingsService settings,
    ILogger<ActivityWriter> log,
    AppPause pause) : BackgroundService
{
    private const int BatchSize = 500;
    private static readonly TimeSpan BatchWindow = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);

    private DateTimeOffset _lastSweep = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Wake for rows, or for the hourly sweep, whichever is first.
                var waited = await WaitForRowsAsync(SweepInterval, ct);
                if (waited)
                {
                    await Task.Delay(BatchWindow, ct); // let the rest of a burst arrive
                    // While a database move has the portal paused nothing is
                    // written; the rows of that minute are dropped rather
                    // than left to loop here — a page opened during a move
                    // is the one fact this log is allowed to lose.
                    using var lease = pause.TryEnter();
                    if (lease is not null) await FlushAsync(ct);
                    else while (queue.Reader.TryRead(out _)) { }
                }
                if (DateTimeOffset.UtcNow - _lastSweep >= SweepInterval)
                {
                    using var lease = pause.TryEnter();
                    if (lease is not null) await SweepAsync(ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // shutting down
        }
        // Whatever reached the queue is written, briefly, before the process goes.
        using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await FlushAsync(grace.Token);
    }

    private async Task<bool> WaitForRowsAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            return await queue.Reader.WaitToReadAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false; // the sweep tick
        }
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        var batch = new List<ActivityEvent>(BatchSize);
        while (queue.Reader.TryRead(out var e))
        {
            batch.Add(e);
            if (batch.Count == BatchSize)
            {
                await WriteAsync(batch, ct);
                batch.Clear();
            }
        }
        if (batch.Count > 0) await WriteAsync(batch, ct);
    }

    private async Task WriteAsync(List<ActivityEvent> batch, CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (db.UseDapper) await InsertSqlAsync(db.Sql, batch, ct);
            else
            {
                db.ActivityEvents.AddRange(batch);
                await db.SaveChangesAsync(ct);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Rows lost, and said so; the next batch is a fresh context.
            log.LogError(e, "Activity log write failed; {Count} rows dropped.", batch.Count);
        }
    }

    /// <summary>
    /// The hour's two sweeps: rows older than the log's retention go
    /// whole, and the AI rows older than the shorter body retention
    /// (<see cref="AiRetention"/>) keep their row and lose what was sent
    /// and answered. Either setting at 0 skips its sweep.
    /// </summary>
    private async Task SweepAsync(CancellationToken ct)
    {
        _lastSweep = DateTimeOffset.UtcNow;
        try
        {
            var cutOff = ActivityRetention.CutOff(await settings.GetAsync(ActivityRetention.Key, ct), _lastSweep);
            var bodyCutOff = AiRetention.CutOff(await settings.GetAsync(AiRetention.Key, ct), _lastSweep);
            if (cutOff is null && bodyCutOff is null) return;
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (cutOff is { } removeBefore)
            {
                var removed = db.UseDapper
                    ? await SweepSqlAsync(db.Sql, removeBefore, ct)
                    : await db.ActivityEvents.Where(e => e.AtUtc < removeBefore).ExecuteDeleteAsync(ct);
                if (removed > 0) log.LogInformation("Activity log: {Count} rows older than {CutOff:u} removed.", removed, removeBefore);
            }
            if (bodyCutOff is { } clearBefore)
            {
                var cleared = db.UseDapper
                    ? await ClearAiBodiesSqlAsync(db.Sql, clearBefore, ct)
                    : await db.ActivityEvents
                        .Where(e => e.Service == ExternalServices.Ai && e.AtUtc < clearBefore && (e.Request != null || e.Response != null))
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(e => e.Request, (string?)null)
                            .SetProperty(e => e.Response, (string?)null), ct);
                if (cleared > 0) log.LogInformation("Activity log: the bodies of {Count} AI calls older than {CutOff:u} cleared.", cleared, clearBefore);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogError(e, "Activity log retention sweep failed; retrying next hour.");
        }
    }
}
