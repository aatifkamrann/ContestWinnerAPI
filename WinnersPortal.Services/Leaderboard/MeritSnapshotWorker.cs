using Microsoft.EntityFrameworkCore;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Domain;
using WinnersPortal.Services.Common;
using WinnersPortal.Services.Profiles;

namespace WinnersPortal.Services.Leaderboard;

/// <summary>
/// Writes the day's merit snapshot (<see cref="MeritSnapshot"/>): once per
/// UTC day, every freelancer's score as it stands, so the leaderboard's
/// trend has a yesterday to compare with. The one worker with no signal
/// to wake it — nothing a request does makes today's row more urgent, and
/// a day is recorded once whether the sweep finds it at midnight or at
/// noon. An hourly sweep is enough: the first cycle of a new day writes
/// the batch, and the rest of the day's cycles find it written.
/// </summary>
public sealed class MeritSnapshotWorker(
    IServiceScopeFactory scopes,
    ILogger<MeritSnapshotWorker> log,
    AppPause pause) : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);

    /// <summary>A year and a month of days, so a trend window never runs off the end.</summary>
    public const int KeepDays = 400;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // No cycle while a database move has the portal paused: a row
                // written now would be one the copy already went past.
                using var lease = pause.TryEnter();
                if (lease is not null)
                {
                    using var scope = scopes.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var written = await RecordTodayAsync(db, DateTimeOffset.UtcNow, ct);
                    if (written > 0) log.LogInformation("Recorded today's merit score for {Count} freelancers.", written);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError(e, "Merit snapshot cycle failed; retrying next sweep.");
            }
            try
            {
                await Task.Delay(SweepInterval, ct);
            }
            catch (OperationCanceledException)
            {
                // shutting down
            }
        }
    }

    /// <summary>
    /// The day's batch, unless it is already there. Every freelancer who is
    /// not erased, score nought included — a member who earns their first
    /// points tomorrow should have a nought to rise from. Rows older than
    /// <see cref="KeepDays"/> go in the same cycle. On SQL Server the four
    /// steps are the procedures <c>Merit_SnapshotRecorded</c>,
    /// <c>Users_ByRole</c>, <c>Merit_SnapshotInsert</c> (the batch as
    /// JSON, one statement) and <c>Merit_SnapshotSweep</c>.
    /// </summary>
    internal static async Task<int> RecordTodayAsync(AppDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var recorded = db.UseDapper
            ? await db.Sql.ScalarAsync<int>(Procedures.MeritSnapshotRecorded, new { today }, ct) == 1
            : await db.MeritSnapshots.AnyAsync(s => s.DayUtc == today, ct);
        if (recorded) return 0;

        var freelancers = db.UseDapper
            ? await db.Sql.QueryAsync<Guid>(Procedures.UsersByRole, new { role = Roles.Freelancer }, ct)
            : await db.Users.AsNoTracking()
                .Where(u => u.Role == Roles.Freelancer && u.ErasedAtUtc == null)
                .Select(u => u.Id)
                .ToListAsync(ct);
        if (freelancers.Count == 0) return 0;

        var merit = await MeritReader.MeritForAsync(db, freelancers, ct);
        var rows = freelancers.Select(id => new MeritSnapshot
        {
            UserId = id,
            DayUtc = today,
            Score = merit.TryGetValue(id, out var m) ? m.Score : 0,
        }).ToList();
        if (db.UseDapper)
        {
            await db.Sql.ExecuteAsync(Procedures.MeritSnapshotInsert, new { rows = RowsJson(rows) }, ct);
        }
        else
        {
            db.MeritSnapshots.AddRange(rows);
            await db.SaveChangesAsync(ct);
        }

        var cutoff = today.AddDays(-KeepDays);
        if (db.UseDapper) await db.Sql.ExecuteAsync(Procedures.MeritSnapshotSweep, new { cutoff }, ct);
        else await db.MeritSnapshots.Where(s => s.DayUtc < cutoff).ExecuteDeleteAsync(ct);
        return freelancers.Count;
    }

    /// <summary>The batch as the JSON the INSERT reads; the day as a date only.</summary>
    internal static string RowsJson(IEnumerable<MeritSnapshot> rows) =>
        System.Text.Json.JsonSerializer.Serialize(rows.Select(r => new { r.UserId, DayUtc = r.DayUtc.ToString("yyyy-MM-dd"), r.Score }));
}
