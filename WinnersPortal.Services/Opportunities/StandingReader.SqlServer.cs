using WinnersPortal.Domain;
using WinnersPortal.Infrastructure.Data;

namespace WinnersPortal.Services.Opportunities;

/// <summary>
/// The SQL Server half of <see cref="StandingReader"/>: the six batched
/// reads as the ten result sets of <c>Standing_Batch</c>, one round trip
/// — the opportunities and their milestones, the entries with their claims and
/// uploads, the entrants' measured profiles and skills, who connected
/// GitHub, their decided opportunities elsewhere, and their ratings.
/// </summary>
public static partial class StandingReader
{
    private sealed record OpportunityRaw(Guid Id, string Title, string BriefMarkdown, DateTimeOffset? DeadlineUtc, int Delivery, bool RequiresCompose);
    private sealed record MilestoneRaw(Guid OpportunityId, int Order, string Title, string? Description, DateTimeOffset? DueUtc);
    private sealed record EntryRaw(
        Guid Id, Guid OpportunityId, Guid FreelancerId, DateTimeOffset CreatedAtUtc, DateTimeOffset? LastPushAtUtc, string? Note,
        string Name, int? TreeFileCount, bool? TreeHasTests, bool? TreeHasReadme, bool? TreeHasCi, bool? TreeHasCompose);
    private sealed record ClaimRaw(
        Guid EntryId, int Order, DateTimeOffset ClaimedAtUtc, Guid CheckpointId, int BuildStatus, DateTimeOffset? BuildFinishedAtUtc);
    private sealed record FileRaw(Guid EntryId, string FileName, string ContentType);
    private sealed record ProfileRaw(
        Guid UserId, string? Headline, string? Bio, string? Location, int? HoursPerWeek, int? YearsExperience, int Projects, int Linked);
    private sealed record SkillRaw(Guid UserId, string Name);

    private static Task<Reads> ReadSqlAsync(Sql sql, IReadOnlyCollection<Guid> opportunityIds, CancellationToken ct) =>
        sql.MultipleAsync(Procedures.StandingBatch, new { ids = Sql.JsonIds(opportunityIds) }, async grid =>
        {
            var opportunities = (await grid.ReadAsync<OpportunityRaw>()).ToList();
            var milestones = (await grid.ReadAsync<MilestoneRaw>()).ToLookup(m => m.OpportunityId);
            var entries = (await grid.ReadAsync<EntryRaw>()).ToList();
            var claims = (await grid.ReadAsync<ClaimRaw>()).ToLookup(c => c.EntryId);
            var files = (await grid.ReadAsync<FileRaw>()).ToLookup(f => f.EntryId);
            var profiles = (await grid.ReadAsync<ProfileRaw>()).ToList();
            var skills = (await grid.ReadAsync<SkillRaw>()).ToLookup(s => s.UserId, s => s.Name);
            var github = (await grid.ReadAsync<Guid>()).ToHashSet();
            var history = (await grid.ReadAsync<HistoryRow>()).ToList();
            var ratings = (await grid.ReadAsync<RatingRow>()).ToDictionary(r => r.UserId);

            return new Reads(
                opportunities.Select(c => new OpportunityRow
                {
                    Id = c.Id, Title = c.Title, BriefMarkdown = c.BriefMarkdown, DeadlineUtc = c.DeadlineUtc,
                    Delivery = (OpportunityDelivery)c.Delivery, RequiresCompose = c.RequiresCompose,
                    Milestones = milestones[c.Id].Select(m => new MilestoneRow(m.Order, m.Title, m.Description, m.DueUtc)).ToList(),
                }).ToList(),
                entries.Select(e => new EntryRow
                {
                    Id = e.Id, OpportunityId = e.OpportunityId, FreelancerId = e.FreelancerId, CreatedAtUtc = e.CreatedAtUtc,
                    LastPushAtUtc = e.LastPushAtUtc, Note = e.Note, Name = e.Name,
                    TreeFileCount = e.TreeFileCount, TreeHasTests = e.TreeHasTests, TreeHasReadme = e.TreeHasReadme, TreeHasCi = e.TreeHasCi, TreeHasCompose = e.TreeHasCompose,
                    Claimed = claims[e.Id]
                        .Select(c => new ClaimRow(c.Order, c.ClaimedAtUtc, c.CheckpointId, (PreviewBuildStatus)c.BuildStatus, c.BuildFinishedAtUtc))
                        .ToList(),
                    Files = files[e.Id].Select(f => new FileRow(f.FileName, f.ContentType)).ToList(),
                }).ToList(),
                profiles.ToDictionary(p => p.UserId, p => new ProfileRow
                {
                    UserId = p.UserId, Headline = p.Headline, Bio = p.Bio, Location = p.Location,
                    HoursPerWeek = p.HoursPerWeek, YearsExperience = p.YearsExperience,
                    Skills = skills[p.UserId].ToList(), Projects = p.Projects, Linked = p.Linked,
                }),
                github,
                history,
                ratings);
        }, ct);
}
