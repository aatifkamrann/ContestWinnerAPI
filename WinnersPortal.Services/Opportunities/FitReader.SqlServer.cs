using WinnersPortal.Domain;
using WinnersPortal.Infrastructure.Data;

namespace WinnersPortal.Services.Opportunities;

/// <summary>The SQL Server half of <see cref="FitReader"/>'s two reads: <c>Fit_Profile</c> and <c>Fit_Entries</c>.</summary>
public static partial class FitReader
{
    private sealed record ProfileRaw(string? Headline, int? Availability, int? HoursPerWeek);

    private sealed record SkillRaw(string Name, int Level, int Years);

    private sealed record EntryRaw(Guid Id, int OpportunityStatus);

    private sealed record DatedRaw(Guid EntryId, DateTimeOffset Due, DateTimeOffset? ClaimedAt);

    private static Task<ProfileRow?> ProfileSqlAsync(Sql sql, Guid userId, CancellationToken ct) =>
        sql.MultipleAsync(Procedures.FitProfile, new { userId }, async grid =>
        {
            var raw = await grid.ReadSingleOrDefaultAsync<ProfileRaw>();
            var skills = (await grid.ReadAsync<SkillRaw>()).Select(s => new SkillRow(s.Name, (SkillLevel)s.Level, s.Years)).ToList();
            var projects = (await grid.ReadAsync<ProjectRow>()).ToList();
            return raw is null
                ? null
                : new ProfileRow
                {
                    Headline = raw.Headline, Availability = (Availability?)raw.Availability, HoursPerWeek = raw.HoursPerWeek,
                    Skills = skills, Projects = projects,
                };
        }, ct);

    private static Task<List<EntryRow>> EntriesSqlAsync(Sql sql, Guid userId, CancellationToken ct) =>
        sql.MultipleAsync(Procedures.FitEntries, new { userId }, async grid =>
        {
            var entries = (await grid.ReadAsync<EntryRaw>()).ToList();
            var dated = (await grid.ReadAsync<DatedRaw>()).ToLookup(d => d.EntryId);
            return entries.Select(e => new EntryRow
            {
                OpportunityStatus = (OpportunityStatus)e.OpportunityStatus,
                Dated = dated[e.Id].Select(d => new DatedRow(d.Due, d.ClaimedAt)).ToList(),
            }).ToList();
        }, ct);
}
