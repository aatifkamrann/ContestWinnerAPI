using System.Text.Json;
using WinnersPortal.Domain;
using WinnersPortal.Infrastructure.Data;

namespace WinnersPortal.Services.Activity;

/// <summary>
/// The SQL Server half of the writer: a batch goes in as one call to
/// <c>Activity_Insert</c> with the rows' JSON — one round trip whatever
/// the batch size, the way EF Core's batching gives Postgres one — and
/// the retention sweep is <c>Activity_Sweep</c>.
/// </summary>
public sealed partial class ActivityWriter
{
    /// <summary>The batch as the JSON the INSERT reads — the columns and nothing else.</summary>
    internal static string RowsJson(IEnumerable<ActivityEvent> batch) =>
        JsonSerializer.Serialize(batch.Select(e => new
        {
            e.UserId, e.Visitor, e.Kind, e.Method, e.Path, e.Action, e.Page, e.Subject, e.Detail, e.Status, e.Ip, e.UserAgent, e.AtUtc,
            e.Service, e.DurationMs, e.Request, e.Response,
        }));

    private static Task<int> InsertSqlAsync(Sql sql, List<ActivityEvent> batch, CancellationToken ct) =>
        sql.ExecuteAsync(Procedures.ActivityInsert, new { rows = RowsJson(batch) }, ct);

    private static Task<int> SweepSqlAsync(Sql sql, DateTimeOffset cutOff, CancellationToken ct) =>
        sql.ExecuteAsync(Procedures.ActivitySweep, new { cutOff }, ct);

    private static Task<int> ClearAiBodiesSqlAsync(Sql sql, DateTimeOffset cutOff, CancellationToken ct) =>
        sql.ExecuteAsync(Procedures.ActivityClearAiBodies, new { cutOff }, ct);
}
