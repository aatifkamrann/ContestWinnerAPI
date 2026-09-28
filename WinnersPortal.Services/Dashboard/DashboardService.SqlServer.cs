using WinnersPortal.Infrastructure.Data;

namespace WinnersPortal.Services.Dashboard;

/// <summary>
/// The SQL Server half of the dashboards' reads: each role's reads as one
/// procedure returning a batch of result sets in a single round trip —
/// <c>Dashboard_Client</c>, <c>Dashboard_Freelancer</c>,
/// <c>Dashboard_Waiting</c> and <c>Dashboard_Admin</c> — filling the rows
/// in <c>DashboardService.Rows.cs</c>.
/// </summary>
public sealed partial class DashboardService
{
    private static Task<ClientReads> ClientReadsSqlAsync(Sql sql, Guid userId, DateTimeOffset since, CancellationToken ct) =>
        sql.MultipleAsync(Procedures.DashboardClient, new { userId, since }, async grid => new ClientReads(
            (await grid.ReadAsync<ClientOpportunityRow>()).ToList(),
            (await grid.ReadAsync<DateTimeOffset>()).ToList(),
            (await grid.ReadAsync<DateTimeOffset>()).ToList(),
            (await grid.ReadAsync<CountRow>()).ToDictionary(x => x.OpportunityId, x => x.Count),
            (await grid.ReadAsync<ClientAwardRow>()).ToList(),
            (await grid.ReadAsync<ClientRecentRow>()).ToList(),
            (await grid.ReadAsync<FailedRow>()).ToList()), ct);

    private static Task<FreelancerReads> FreelancerReadsSqlAsync(Sql sql, Guid userId, DateTimeOffset since, CancellationToken ct) =>
        sql.MultipleAsync(Procedures.DashboardFreelancer, new { userId, since }, async grid => new FreelancerReads(
            (await grid.ReadAsync<FreelancerEntryRow>()).ToList(),
            (await grid.ReadAsync<DateTimeOffset>()).ToList(),
            (await grid.ReadAsync<FreelancerAwardRow>()).ToList(),
            (await grid.ReadAsync<FreelancerRecentRow>()).ToList()), ct);

    private static Task<List<WaitingRow>> WaitingSqlAsync(Sql sql, Guid? clientId, CancellationToken ct) =>
        sql.QueryAsync<WaitingRow>(Procedures.DashboardWaiting, new { clientId }, ct);

    private static Task<AdminReads> AdminReadsSqlAsync(Sql sql, DateTimeOffset now, DateTimeOffset since, CancellationToken ct) =>
        sql.MultipleAsync(Procedures.DashboardAdmin, new { since, dayAgo = now.AddDays(-1) }, async grid => new AdminReads(
            (await grid.ReadAsync<RoleCountRow>()).ToList(),
            (await grid.ReadAsync<SignupRow>()).ToList(),
            (await grid.ReadAsync<StatusCountRow>()).ToList(),
            (await grid.ReadAsync<DateTimeOffset>()).ToList(),
            (await grid.ReadAsync<DateTimeOffset>()).ToList(),
            (await grid.ReadAsync<DateTimeOffset>()).ToList(),
            (await grid.ReadAsync<ProvisionCountRow>()).ToList(),
            (await grid.ReadAsync<AdminAwardRow>()).ToList(),
            (await grid.ReadAsync<DeliveryRow>()).ToList(),
            (await grid.ReadAsync<FailedRepoRow>()).ToList(),
            (await grid.ReadAsync<TopClientRow>()).ToList(),
            (await grid.ReadAsync<TopFreelancerRow>()).ToList()), ct);
}
