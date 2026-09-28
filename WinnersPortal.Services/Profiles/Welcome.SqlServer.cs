using WinnersPortal.Domain;
using WinnersPortal.Infrastructure.Data;

namespace WinnersPortal.Services.Profiles;

/// <summary>The SQL Server half of <see cref="Welcome"/>'s reads: <c>Welcome_Account</c>, <c>Welcome_Profile</c>, <c>Users_ByRole</c> and <c>Welcome_Peers</c>.</summary>
public static partial class Welcome
{
    private sealed record ProfileRaw(string? Headline, string? Bio, string? PrimaryCategory, int? Availability, int Skills);

    private static Task<AccountRow?> AccountSqlAsync(Sql sql, Guid userId, CancellationToken ct) =>
        sql.SingleOrDefaultAsync<AccountRow>(Procedures.WelcomeAccount, new { userId }, ct);

    private static Task<(ProfileRow? Profile, int Payments)> ProfileSqlAsync(Sql sql, Guid userId, CancellationToken ct) =>
        sql.MultipleAsync(Procedures.WelcomeProfile, new { userId }, async grid =>
        {
            var raw = await grid.ReadSingleOrDefaultAsync<ProfileRaw>();
            var projects = (await grid.ReadAsync<ProjectRow>()).ToList();
            var payments = await grid.ReadSingleAsync<int>();
            var profile = raw is null
                ? null
                : new ProfileRow
                {
                    Headline = raw.Headline, Bio = raw.Bio, PrimaryCategory = raw.PrimaryCategory,
                    Availability = (Availability?)raw.Availability, Skills = raw.Skills, Projects = projects,
                };
            return (profile, payments);
        }, ct);

    private static Task<List<Guid>> FreelancersSqlAsync(Sql sql, CancellationToken ct) =>
        sql.QueryAsync<Guid>(Procedures.UsersByRole, new { role = Roles.Freelancer }, ct);

    private static Task<List<Guid>> PeersSqlAsync(Sql sql, string categoryKey, List<Guid> freelancers, CancellationToken ct) =>
        sql.QueryAsync<Guid>(Procedures.WelcomePeers, new { ids = Sql.JsonIds(freelancers), categoryKey }, ct);
}
