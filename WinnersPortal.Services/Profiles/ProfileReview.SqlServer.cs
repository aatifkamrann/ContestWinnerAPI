using WinnersPortal.Domain;
using WinnersPortal.Infrastructure.Data;

namespace WinnersPortal.Services.Profiles;

/// <summary>The SQL Server half of <see cref="ProfileReview"/>'s reads: <c>ProfileReview_Account</c>, <c>ProfileReview_Profile</c> and <c>ProfileReview_Open</c>.</summary>
public static partial class ProfileReview
{
    private sealed record ProfileRaw(
        string? Headline, string? Bio, string? Location, string? PrimaryCategory, string? SecondaryCategories,
        int? Availability, int? HoursPerWeek, int? YearsExperience);

    private sealed record SkillRaw(string Name, int Level, int Years);

    private sealed record OpenRaw(Guid Id, int MinMeritScore);

    private sealed record OpportunitySkillRaw(Guid OpportunityId, string Name, string Key);

    private static Task<AccountRow?> AccountSqlAsync(Sql sql, Guid userId, CancellationToken ct) =>
        sql.SingleOrDefaultAsync<AccountRow>(Procedures.ProfileReviewAccount, new { userId }, ct);

    private static Task<(ProfileRow? Profile, int Payments)> ProfileSqlAsync(Sql sql, Guid userId, CancellationToken ct) =>
        sql.MultipleAsync(Procedures.ProfileReviewProfile, new { userId }, async grid =>
        {
            var raw = await grid.ReadSingleOrDefaultAsync<ProfileRaw>();
            var skills = (await grid.ReadAsync<SkillRaw>()).Select(s => new SkillRow(s.Name, (SkillLevel)s.Level, s.Years)).ToList();
            var projects = (await grid.ReadAsync<ProjectRow>()).ToList();
            var payments = await grid.ReadSingleAsync<int>();
            var profile = raw is null
                ? null
                : new ProfileRow
                {
                    Headline = raw.Headline, Bio = raw.Bio, Location = raw.Location, PrimaryCategory = raw.PrimaryCategory,
                    SecondaryCategories = Sql.JsonList<string>(raw.SecondaryCategories),
                    Availability = (Availability?)raw.Availability, HoursPerWeek = raw.HoursPerWeek,
                    YearsExperience = raw.YearsExperience, Skills = skills, Projects = projects,
                };
            return (profile, payments);
        }, ct);

    private static Task<List<Door>> OpenSqlAsync(Sql sql, CancellationToken ct) =>
        sql.MultipleAsync(Procedures.ProfileReviewOpen, null, async grid =>
        {
            var opportunities = (await grid.ReadAsync<OpenRaw>()).ToList();
            var skills = (await grid.ReadAsync<OpportunitySkillRaw>()).ToLookup(s => s.OpportunityId);
            return opportunities
                .Select(c => new Door(c.MinMeritScore, skills[c.Id].Select(s => (s.Name, s.Key)).ToList()))
                .ToList();
        }, ct);
}
