using System.Data;
using Dapper;
using WinnersPortal.Domain;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Services.Opportunities;

namespace WinnersPortal.Services.Activity;

/// <summary>
/// The SQL Server half of the administrator's read: the count and the page
/// in one round trip from <c>Activity_Page</c>, whose every filter is a
/// parameter, null for "not applied". The typed words match every text
/// column with LIKE, which the default collation reads case aside — the
/// same nine columns the LINQ's ILIKE reads on Postgres.
/// </summary>
public sealed partial class ActivityService
{
    /// <summary>
    /// The filter as the procedure's parameters — every condition the
    /// LINQ's <see cref="Filter"/> applies, normalised the same way: a
    /// visitor that is not a visitor id, a kind that is not one, and blank
    /// words are not applied. Pure, so the tests can read what each filter
    /// becomes.
    /// </summary>
    internal static DynamicParameters Parameters(ActivityFilter f, int skip, int take)
    {
        var p = new DynamicParameters();
        p.Add("skip", skip);
        p.Add("take", take);
        p.Add("userId", f.User, DbType.Guid);
        p.Add("visitor", ActivityNames.IsVisitorId(f.Visitor) ? f.Visitor : null, DbType.String);
        p.Add("kind", KnownKind(f.Kind) ? f.Kind : null, DbType.String);
        p.Add("service", ExternalServices.IsKnown(f.Service) ? f.Service : null, DbType.String);
        p.Add("members", f.Who switch { "members" => true, "visitors" => false, _ => (bool?)null }, DbType.Boolean);
        p.Add("since", f.From, DbType.DateTimeOffset);
        p.Add("until", f.To, DbType.DateTimeOffset);
        p.Add("q", string.IsNullOrWhiteSpace(f.Q) ? null : Search.TitlePattern(f.Q), DbType.String);
        // A LIKE pattern, as the words are: a provider key is letters only, so nothing in it needs escaping.
        p.Add("aiProvider", AiProviderFilter(f.AiProvider) is { } provider ? provider + "/%" : null, DbType.String);
        p.Add("aiModel", AiModelFilter(f.AiModel), DbType.String);
        return p;
    }

    private static Task<(int Total, List<ActivityRowData> Rows)> PageSqlAsync(
        Sql sql, ActivityFilter f, int pageIndex, int pageSize, CancellationToken ct) =>
        sql.MultipleAsync(Procedures.ActivityPage, Parameters(f, (pageIndex - 1) * pageSize, pageSize), async grid =>
        {
            var total = await grid.ReadSingleAsync<int>();
            var rows = (await grid.ReadAsync<ActivityRowData>()).ToList();
            return (total, rows);
        }, ct);

    private static Task<List<AiModelRow>> AiModelsSqlAsync(Sql sql, CancellationToken ct) =>
        sql.QueryAsync<AiModelRow>(Procedures.ActivityAiModels, null, ct);

    private static Task<ExchangeRow?> ExchangeSqlAsync(Sql sql, long id, CancellationToken ct) =>
        sql.SingleOrDefaultAsync<ExchangeRow>(Procedures.ActivityExchange, new { id }, ct);
}
