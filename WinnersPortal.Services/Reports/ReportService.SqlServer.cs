using WinnersPortal.Infrastructure.Data;

namespace WinnersPortal.Services.Reports;

/// <summary>
/// The SQL Server half of the three reports: the rows and the figures
/// beside them as procedures, filling the same row classes the LINQ
/// fills. The scope — every row for an administrator, those on opportunities
/// the caller posted, those the caller entered — is a parameter of each
/// rows procedure, with the caller's id beside it.
/// </summary>
public sealed partial class ReportService
{
    private sealed record SkillRead(Guid OpportunityId, string Name);

    private static Task<List<Row>> OpportunitiesSqlAsync(Sql sql, Scope scope, Guid me, CancellationToken ct) =>
        sql.MultipleAsync(Procedures.ReportOpportunities, new { scope = (int)scope, me }, async grid =>
        {
            var rows = (await grid.ReadAsync<Row>()).ToList();
            var skills = (await grid.ReadAsync<SkillRead>()).ToLookup(s => s.OpportunityId, s => s.Name);
            foreach (var row in rows) row.Skills = skills[row.Id].ToList();
            return rows;
        }, ct);

    private static Task<OpportunityFigures> OpportunityFiguresSqlAsync(Sql sql, List<Guid> ids, CancellationToken ct) =>
        sql.MultipleAsync(Procedures.ReportOpportunityFigures, new { ids = Sql.JsonIds(ids) }, async grid => new OpportunityFigures(
            (await grid.ReadAsync<ApplicationFigures>()).ToDictionary(x => x.Key),
            (await grid.ReadAsync<EntryFigures>()).ToDictionary(x => x.Key),
            (await grid.ReadAsync<AwardFigure>()).ToList()), ct);

    private static Task<MyParts> MyPartsSqlAsync(Sql sql, List<Guid> ids, Guid me, CancellationToken ct) =>
        sql.MultipleAsync(Procedures.ReportMyParts, new { ids = Sql.JsonIds(ids), me }, async grid => new MyParts(
            (await grid.ReadAsync<PartEntry>()).ToList(),
            (await grid.ReadAsync<PartApplication>()).ToList(),
            (await grid.ReadAsync<Guid>()).ToList()), ct);

    /// <summary>The row with its advantages still as JSON, the way the column holds them.</summary>
    private sealed class ApplicationRaw : ApplicationRow
    {
        public string? AdvantagesJson { get; set; }
    }

    private static async Task<List<ApplicationRow>> ApplicationsSqlAsync(Sql sql, Scope scope, Guid me, CancellationToken ct)
    {
        var rows = await sql.QueryAsync<ApplicationRaw>(Procedures.ReportApplications, new { scope = (int)scope, me }, ct);
        foreach (var row in rows) row.Advantages = Sql.JsonList<string>(row.AdvantagesJson).ToArray();
        return rows.Cast<ApplicationRow>().ToList();
    }

    private static Task<ApplicationOutcomes> ApplicationOutcomesSqlAsync(Sql sql, List<Guid> entryIds, CancellationToken ct) =>
        sql.MultipleAsync(Procedures.ReportApplicationOutcomes, new { ids = Sql.JsonIds(entryIds) }, async grid => new ApplicationOutcomes(
            (await grid.ReadAsync<EntryOutcome>()).ToList(),
            (await grid.ReadAsync<AwardPaid>()).ToList()), ct);

    private sealed record ClaimRead(Guid EntryId, int Order, DateTimeOffset ClaimedAtUtc);

    private static Task<List<EntryRow>> EntriesSqlAsync(Sql sql, Scope scope, Guid me, CancellationToken ct) =>
        sql.MultipleAsync(Procedures.ReportEntries, new { scope = (int)scope, me }, async grid =>
        {
            var rows = (await grid.ReadAsync<EntryRow>()).ToList();
            var claims = (await grid.ReadAsync<ClaimRead>()).ToLookup(c => c.EntryId);
            foreach (var row in rows)
                row.Claims = claims[row.Id].Select(c => new ClaimRow { Order = c.Order, ClaimedAtUtc = c.ClaimedAtUtc }).ToList();
            return rows;
        }, ct);

    private static Task<EntryFacts> EntryFactsSqlAsync(Sql sql, List<Guid> entryIds, List<Guid> opportunityIds, CancellationToken ct) =>
        sql.MultipleAsync(
            Procedures.ReportEntryFacts,
            new { ids = Sql.JsonIds(entryIds), opportunities = Sql.JsonIds(opportunityIds) },
            async grid => new EntryFacts(
                (await grid.ReadAsync<MilestoneRead>()).ToList(),
                (await grid.ReadAsync<ApplicationOfEntry>()).ToList(),
                (await grid.ReadAsync<AwardOfEntry>()).ToList(),
                (await grid.ReadAsync<RatingOfAward>()).ToList()), ct);
}
