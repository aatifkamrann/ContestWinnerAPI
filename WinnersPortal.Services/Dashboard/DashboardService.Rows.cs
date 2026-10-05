using Microsoft.EntityFrameworkCore;
using WinnersPortal.Domain;
using WinnersPortal.Infrastructure.Data;

namespace WinnersPortal.Services.Dashboard;

/// <summary>
/// The rows each dashboard is shaped from, and the LINQ that reads them —
/// what Postgres runs. SQL Server runs the T-SQL in
/// <c>DashboardService.SqlServer.cs</c>, filling the same rows; the
/// shaping in <c>DashboardService.cs</c> never knows which.
/// </summary>
public sealed partial class DashboardService
{
    // ---- client -----------------------------------------------------------

    internal sealed record ClientOpportunityRow(
        Guid Id, string Slug, string Title, OpportunityStatus Status, DateTimeOffset? DeadlineUtc, DateTimeOffset? PublishedAtUtc,
        decimal AwardAmount, string Currency, int Entrants, int Milestones, int RepoIssues);

    internal sealed record CountRow(Guid OpportunityId, int Count);

    internal sealed record ClientAwardRow(
        Guid Id, decimal Amount, string Currency, DateTimeOffset AnnouncedAtUtc, DateTimeOffset? PaidAtUtc, HandoverStatus Handover,
        string? HandoverNote, string? TransferTargetLogin, Guid OpportunityId, string Slug, string Title, string Winner, OpportunityKind Kind, decimal PaidSoFar, int Waiting, int ChangesAsked);

    internal sealed record ClientRecentRow(DateTimeOffset ClaimedAtUtc, string Via, string Who, string Milestone, int Order, string Slug);

    internal sealed record FailedRow(string GithubUsername, string Slug, string Title);

    internal sealed record ClientReads(
        List<ClientOpportunityRow> Opportunities,
        List<DateTimeOffset> EntryStamps,
        List<DateTimeOffset> ClaimStamps,
        Dictionary<Guid, int> ClaimsByOpportunity,
        List<ClientAwardRow> Awards,
        List<ClientRecentRow> Recent,
        List<FailedRow> Failed);

    internal static async Task<ClientReads> ClientReadsLinqAsync(AppDbContext db, Guid userId, DateTimeOffset since, CancellationToken ct)
    {
        var opportunities = await db.Opportunities.AsNoTracking()
            .Where(c => c.ClientId == userId)
            .OrderByDescending(c => c.CreatedAtUtc)
            .Take(200)
            .Select(c => new ClientOpportunityRow(
                c.Id, c.Slug, c.Title, c.Status, c.DeadlineUtc, c.PublishedAtUtc,
                c.AwardAmount, c.Currency,
                c.Entries.Count(e => e.Status == EntryStatus.Active),
                c.Milestones.Count,
                c.Entries.Count(e => e.ProvisionStatus == RepoProvisionStatus.Failed)))
            .ToListAsync(ct);

        var opportunityIds = opportunities.Select(c => c.Id).ToList();

        var entryStamps = await db.Entries.AsNoTracking()
            .Where(e => opportunityIds.Contains(e.OpportunityId) && e.CreatedAtUtc >= since)
            .Select(e => e.CreatedAtUtc)
            .ToListAsync(ct);

        var claimStamps = await db.Checkpoints.AsNoTracking()
            .Where(cp => opportunityIds.Contains(cp.Entry!.OpportunityId) && cp.ClaimedAtUtc >= since)
            .Select(cp => cp.ClaimedAtUtc)
            .ToListAsync(ct);

        // Claims per opportunity — the numerator of every progress bar.
        var claimsByOpportunity = (await db.Checkpoints.AsNoTracking()
                .Where(cp => opportunityIds.Contains(cp.Entry!.OpportunityId))
                .GroupBy(cp => cp.Entry!.OpportunityId)
                .Select(g => new CountRow(g.Key, g.Count()))
                .ToListAsync(ct))
            .ToDictionary(x => x.OpportunityId, x => x.Count);

        var awards = await db.Awards.AsNoTracking()
            .Where(a => a.Opportunity!.ClientId == userId)
            .Select(a => new ClientAwardRow(
                a.Id, a.Amount, a.Currency, a.AnnouncedAtUtc, a.PaidAtUtc, a.Handover,
                a.HandoverNote, a.TransferTargetLogin, a.OpportunityId,
                a.Opportunity!.Slug,
                a.Opportunity!.Title,
                a.Entry!.Freelancer!.DisplayName,
                a.Opportunity!.Kind,
                db.Checkpoints.Where(cp => cp.EntryId == a.EntryId && cp.PaidAtUtc != null).Sum(cp => (decimal?)cp.Milestone!.Amount) ?? 0,
                db.Checkpoints.Count(cp => cp.EntryId == a.EntryId && cp.PaidAtUtc == null && cp.ChangesRequestedAtUtc == null),
                db.Checkpoints.Count(cp => cp.EntryId == a.EntryId && cp.ChangesRequestedAtUtc != null)))
            .ToListAsync(ct);

        var recent = await db.Checkpoints.AsNoTracking()
            .Where(cp => opportunityIds.Contains(cp.Entry!.OpportunityId))
            .OrderByDescending(cp => cp.ClaimedAtUtc)
            .Take(8)
            .Select(cp => new ClientRecentRow(
                cp.ClaimedAtUtc, cp.Via,
                cp.Entry!.Freelancer!.DisplayName,
                cp.Milestone!.Title,
                cp.Milestone!.Order,
                cp.Entry!.Opportunity!.Slug))
            .ToListAsync(ct);

        var failed = await db.Entries.AsNoTracking()
            .Where(e => opportunityIds.Contains(e.OpportunityId) && e.ProvisionStatus == RepoProvisionStatus.Failed)
            .Select(e => new FailedRow(e.GithubUsername, e.Opportunity!.Slug, e.Opportunity!.Title))
            .Take(10)
            .ToListAsync(ct);

        return new ClientReads(opportunities, entryStamps, claimStamps, claimsByOpportunity, awards, recent, failed);
    }

    // ---- freelancer -------------------------------------------------------

    internal sealed record FreelancerEntryRow(
        Guid Id, Guid OpportunityId, EntryStatus Status, RepoProvisionStatus ProvisionStatus, string? RepoFullName,
        DateTimeOffset? LastPushAtUtc, int PushCount, DateTimeOffset CreatedAtUtc, int Claimed,
        string Slug, string Title, OpportunityStatus OpportunityStatus, DateTimeOffset? DeadlineUtc, decimal AwardAmount, string Currency,
        int Milestones, OpportunityDelivery Delivery);

    internal sealed record FreelancerAwardRow(
        decimal Amount, string Currency, DateTimeOffset AnnouncedAtUtc, DateTimeOffset? PaidAtUtc, HandoverStatus Handover,
        string? TransferTargetLogin, Guid EntryId, string Slug, string Title, OpportunityKind Kind, decimal PaidSoFar, int Waiting, int ChangesAsked);

    internal sealed record FreelancerRecentRow(DateTimeOffset ClaimedAtUtc, string Via, string Ref, string Milestone, int Order, string Slug);

    internal sealed record FreelancerReads(
        List<FreelancerEntryRow> Entries,
        List<DateTimeOffset> ClaimStamps,
        List<FreelancerAwardRow> Awards,
        List<FreelancerRecentRow> Recent);

    internal static async Task<FreelancerReads> FreelancerReadsLinqAsync(
        AppDbContext db, Guid userId, DateTimeOffset since, CancellationToken ct)
    {
        var entries = await db.Entries.AsNoTracking()
            .Where(e => e.FreelancerId == userId)
            .OrderByDescending(e => e.CreatedAtUtc)
            .Take(200)
            .Select(e => new FreelancerEntryRow(
                e.Id, e.OpportunityId, e.Status, e.ProvisionStatus, e.RepoFullName, e.LastPushAtUtc, e.PushCount,
                e.CreatedAtUtc,
                e.Checkpoints.Count,
                e.Opportunity!.Slug,
                e.Opportunity!.Title,
                e.Opportunity!.Status,
                e.Opportunity!.DeadlineUtc,
                e.Opportunity!.AwardAmount,
                e.Opportunity!.Currency,
                e.Opportunity!.Milestones.Count,
                e.Opportunity!.Delivery))
            .ToListAsync(ct);

        var claimStamps = await db.Checkpoints.AsNoTracking()
            .Where(cp => cp.Entry!.FreelancerId == userId && cp.ClaimedAtUtc >= since)
            .Select(cp => cp.ClaimedAtUtc)
            .ToListAsync(ct);

        var awards = await db.Awards.AsNoTracking()
            .Where(a => a.Entry!.FreelancerId == userId)
            .Select(a => new FreelancerAwardRow(
                a.Amount, a.Currency, a.AnnouncedAtUtc, a.PaidAtUtc, a.Handover, a.TransferTargetLogin, a.EntryId,
                a.Opportunity!.Slug,
                a.Opportunity!.Title,
                a.Opportunity!.Kind,
                db.Checkpoints.Where(cp => cp.EntryId == a.EntryId && cp.PaidAtUtc != null).Sum(cp => (decimal?)cp.Milestone!.Amount) ?? 0,
                db.Checkpoints.Count(cp => cp.EntryId == a.EntryId && cp.PaidAtUtc == null && cp.ChangesRequestedAtUtc == null),
                db.Checkpoints.Count(cp => cp.EntryId == a.EntryId && cp.ChangesRequestedAtUtc != null)))
            .ToListAsync(ct);

        var recent = await db.Checkpoints.AsNoTracking()
            .Where(cp => cp.Entry!.FreelancerId == userId)
            .OrderByDescending(cp => cp.ClaimedAtUtc)
            .Take(8)
            .Select(cp => new FreelancerRecentRow(
                cp.ClaimedAtUtc, cp.Via, cp.Ref,
                cp.Milestone!.Title,
                cp.Milestone!.Order,
                cp.Entry!.Opportunity!.Slug))
            .ToListAsync(ct);

        return new FreelancerReads(entries, claimStamps, awards, recent);
    }

    // ---- waiting applications (client and admin) --------------------------

    internal sealed record WaitingRow(Guid OpportunityId, string Slug, string Title, DateTimeOffset SubmittedAtUtc);

    internal static async Task<List<WaitingRow>> WaitingLinqAsync(AppDbContext db, Guid? clientId, CancellationToken ct)
    {
        var query = db.Applications.AsNoTracking()
            .Where(a => a.Opportunity!.Status == OpportunityStatus.Open && a.Status == ApplicationStatus.UnderReview);
        if (clientId is { } id) query = query.Where(a => a.Opportunity!.ClientId == id);
        return await query
            .Select(a => new WaitingRow(a.OpportunityId, a.Opportunity!.Slug, a.Opportunity.Title, a.SubmittedAtUtc))
            .ToListAsync(ct);
    }

    // ---- admin ------------------------------------------------------------

    internal sealed record RoleCountRow(string Role, int Count);

    internal sealed record SignupRow(string Role, DateTimeOffset CreatedAtUtc);

    internal sealed record StatusCountRow(OpportunityStatus Status, int Count);

    internal sealed record ProvisionCountRow(RepoProvisionStatus Status, int Count);

    internal sealed record AdminAwardRow(
        decimal Amount, string Currency, DateTimeOffset AnnouncedAtUtc, DateTimeOffset? PaidAtUtc, HandoverStatus Handover,
        string? TransferTargetLogin, string? HandoverNote, string Slug, string Title, string Client, Guid ClientId,
        string Winner, Guid WinnerId, OpportunityKind Kind, decimal PaidSoFar, int Waiting, int ChangesAsked);

    internal sealed record DeliveryRow(string Event, string? HandledNote, DateTimeOffset ReceivedAtUtc, string? RepoFullName);

    internal sealed record FailedRepoRow(string GithubUsername, string? ProvisionNote, int ProvisionAttempts, string Slug, string Title);

    internal sealed record TopClientRow(Guid ClientId, string Name, int Opportunities, int Entrants, DateTimeOffset LastPosted);

    internal sealed record TopFreelancerRow(Guid FreelancerId, string Name, int Entries, int Claims, int Pushes);

    internal sealed record AdminReads(
        List<RoleCountRow> UsersByRole,
        List<SignupRow> Signups,
        List<StatusCountRow> OpportunitiesByStatus,
        List<DateTimeOffset> PublishStamps,
        List<DateTimeOffset> EntryStamps,
        List<DateTimeOffset> ClaimStamps,
        List<ProvisionCountRow> ProvisionByStatus,
        List<AdminAwardRow> Awards,
        List<DeliveryRow> Deliveries,
        List<FailedRepoRow> FailedRepos,
        List<TopClientRow> TopClients,
        List<TopFreelancerRow> TopFreelancers);

    internal static async Task<AdminReads> AdminReadsLinqAsync(
        AppDbContext db, DateTimeOffset now, DateTimeOffset since, CancellationToken ct)
    {
        // Living accounts, as Users lists them — the People tile opens that
        // list, and an erased account is a tombstone kept for the records it
        // touched, not a person on the portal.
        var usersByRole = await db.Users.AsNoTracking()
            .Where(u => u.ErasedAtUtc == null)
            .GroupBy(u => u.Role)
            .Select(g => new RoleCountRow(g.Key, g.Count()))
            .ToListAsync(ct);

        var signups = await db.Users.AsNoTracking()
            .Where(u => u.ErasedAtUtc == null && u.CreatedAtUtc >= since)
            .Select(u => new SignupRow(u.Role, u.CreatedAtUtc))
            .ToListAsync(ct);

        var opportunitiesByStatus = await db.Opportunities.AsNoTracking()
            .GroupBy(c => c.Status)
            .Select(g => new StatusCountRow(g.Key, g.Count()))
            .ToListAsync(ct);

        var publishStamps = await db.Opportunities.AsNoTracking()
            .Where(c => c.PublishedAtUtc >= since)
            .Select(c => c.PublishedAtUtc!.Value)
            .ToListAsync(ct);

        var entryStamps = await db.Entries.AsNoTracking()
            .Where(e => e.CreatedAtUtc >= since)
            .Select(e => e.CreatedAtUtc)
            .ToListAsync(ct);

        var claimStamps = await db.Checkpoints.AsNoTracking()
            .Where(cp => cp.ClaimedAtUtc >= since)
            .Select(cp => cp.ClaimedAtUtc)
            .ToListAsync(ct);

        var provisionByStatus = await db.Entries.AsNoTracking()
            .Where(e => e.Status == EntryStatus.Active
                // Repositories only — an upload-only entry has none to be pending.
                && e.Opportunity!.Delivery != OpportunityDelivery.Upload)
            .GroupBy(e => e.ProvisionStatus)
            .Select(g => new ProvisionCountRow(g.Key, g.Count()))
            .ToListAsync(ct);

        var awards = await db.Awards.AsNoTracking()
            .Select(a => new AdminAwardRow(
                a.Amount, a.Currency, a.AnnouncedAtUtc, a.PaidAtUtc, a.Handover,
                a.TransferTargetLogin, a.HandoverNote,
                a.Opportunity!.Slug,
                a.Opportunity!.Title,
                a.Opportunity!.Client!.DisplayName,
                a.Opportunity!.ClientId,
                a.Entry!.Freelancer!.DisplayName,
                a.Entry!.FreelancerId,
                a.Opportunity!.Kind,
                db.Checkpoints.Where(cp => cp.EntryId == a.EntryId && cp.PaidAtUtc != null).Sum(cp => (decimal?)cp.Milestone!.Amount) ?? 0,
                db.Checkpoints.Count(cp => cp.EntryId == a.EntryId && cp.PaidAtUtc == null && cp.ChangesRequestedAtUtc == null),
                db.Checkpoints.Count(cp => cp.EntryId == a.EntryId && cp.ChangesRequestedAtUtc != null)))
            .ToListAsync(ct);

        var deliveries = await db.WebhookDeliveries.AsNoTracking()
            .Where(w => w.ReceivedAtUtc >= now.AddDays(-1))
            .Select(w => new DeliveryRow(w.Event, w.HandledNote, w.ReceivedAtUtc, w.RepoFullName))
            .ToListAsync(ct);

        var failedRepos = await db.Entries.AsNoTracking()
            .Where(e => e.ProvisionStatus == RepoProvisionStatus.Failed)
            .OrderByDescending(e => e.ProvisionAttemptedAtUtc)
            .Take(10)
            .Select(e => new FailedRepoRow(
                e.GithubUsername, e.ProvisionNote, e.ProvisionAttempts,
                e.Opportunity!.Slug, e.Opportunity!.Title))
            .ToListAsync(ct);

        // ---- the client side of the marketplace, portal-wide -------------
        // Anonymous until the rows are back: EF Core will not order by a
        // member it projected through a constructor.
        var clientRows = await db.Opportunities.AsNoTracking()
            .GroupBy(c => new { c.ClientId, Name = c.Client!.DisplayName })
            .Select(g => new
            {
                g.Key.ClientId,
                g.Key.Name,
                Opportunities = g.Count(),
                Entrants = g.Sum(c => c.Entries.Count(e => e.Status == EntryStatus.Active)),
                LastPosted = g.Max(c => c.CreatedAtUtc),
            })
            .OrderByDescending(x => x.Opportunities)
            .Take(8)
            .ToListAsync(ct);

        // ---- the freelancer side, portal-wide ----------------------------
        var freelancerRows = await db.Entries.AsNoTracking()
            .Where(e => e.Status == EntryStatus.Active)
            .GroupBy(e => new { e.FreelancerId, Name = e.Freelancer!.DisplayName })
            .Select(g => new
            {
                g.Key.FreelancerId,
                g.Key.Name,
                Entries = g.Count(),
                Claims = g.Sum(e => e.Checkpoints.Count),
                Pushes = g.Sum(e => e.PushCount),
            })
            .OrderByDescending(x => x.Entries)
            .Take(8)
            .ToListAsync(ct);

        return new AdminReads(
            usersByRole, signups, opportunitiesByStatus, publishStamps, entryStamps, claimStamps, provisionByStatus,
            awards, deliveries, failedRepos,
            clientRows.Select(c => new TopClientRow(c.ClientId, c.Name, c.Opportunities, c.Entrants, c.LastPosted)).ToList(),
            freelancerRows.Select(f => new TopFreelancerRow(f.FreelancerId, f.Name, f.Entries, f.Claims, f.Pushes)).ToList());
    }
}
