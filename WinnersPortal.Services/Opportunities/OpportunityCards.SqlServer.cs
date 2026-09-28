using System.Data;
using Dapper;
using WinnersPortal.Infrastructure.Data;

namespace WinnersPortal.Services.Opportunities;

/// <summary>
/// The SQL Server half of the feed: <c>Opportunity_Feed</c> picks the page's
/// ids under the order in force, then returns the cards and their skills
/// as two result sets of one round trip. Every filter, the order and the
/// keyset are its parameters; the text search is FREETEXT over the
/// full-text index plus the title as typed, as <see cref="OpportunitySearch"/>
/// renders it for EF Core.
/// </summary>
public static partial class OpportunityCards
{
    /// <summary>The feed's order as the procedure numbers it.</summary>
    internal static byte Sort(FeedQuery f) => (byte)(f.ByAward ? 2 : f.Ending ? 1 : 0);

    /// <summary>
    /// The feed's query as the procedure's parameters, every one of them
    /// present: null where a filter is not applied, the subcategory only
    /// within its category, the cursor's amount only under the award order.
    /// Pure, so the tests can read them.
    /// </summary>
    internal static DynamicParameters FeedParameters(FeedQuery f)
    {
        var p = new DynamicParameters();
        p.Add("openOnly", f.OpenOnly);
        p.Add("sort", Sort(f));
        p.Add("now", f.Now);
        p.Add("take", f.Take);
        p.Add("words", f.Q?.Trim(), DbType.String);
        p.Add("pattern", f.Q is { } q ? Opportunities.Search.TitlePattern(q) : null, DbType.String);
        p.Add("category", f.CategoryKey, DbType.String);
        p.Add("subcategory", f.CategoryKey is null ? null : f.SubcategoryKey, DbType.String);
        p.Add("cursorAt", f.Cursor?.At, DbType.DateTimeOffset);
        p.Add("cursorId", f.Cursor?.Id, DbType.Guid);
        p.Add("cursorAmount", f.Cursor is { } cur && f.ByAward ? cur.Amount ?? 0m : null, DbType.Decimal);
        return p;
    }

    private sealed record SkillRead(Guid OpportunityId, string Name);

    internal static Task<List<Row>> FeedSqlAsync(Sql sql, FeedQuery f, CancellationToken ct) =>
        sql.MultipleAsync(Procedures.OpportunityFeed, FeedParameters(f), ReadCardsAsync, ct);

    internal static Task<List<Row>> OpenSqlAsync(Sql sql, DateTimeOffset now, CancellationToken ct) =>
        sql.MultipleAsync(Procedures.OpportunityOpen, new { now }, ReadCardsAsync, ct);

    private static async Task<List<Row>> ReadCardsAsync(SqlMapper.GridReader grid)
    {
        var rows = (await grid.ReadAsync<Row>()).ToList();
        var skills = (await grid.ReadAsync<SkillRead>()).ToLookup(s => s.OpportunityId, s => s.Name);
        foreach (var row in rows) row.Skills = skills[row.Id].ToList();
        return rows;
    }

    private sealed record CountRead(Guid OpportunityId, int Count);

    private static async Task<Dictionary<Guid, int>> CountApplicationsSqlAsync(Sql sql, List<Guid> ids, CancellationToken ct) =>
        (await sql.QueryAsync<CountRead>(Procedures.OpportunityApplicationCounts, new { ids = Sql.JsonIds(ids) }, ct))
            .ToDictionary(x => x.OpportunityId, x => x.Count);
}
