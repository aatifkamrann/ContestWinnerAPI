using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using WinnersPortal.Domain;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Services.Auth;
using WinnersPortal.Services.Common;
using WinnersPortal.Services.Opportunities;

namespace WinnersPortal.Services.Reports;

/// <summary>
/// The entries report: one row per entry — somebody competing in an opportunity,
/// whatever became of it — with the board it built (milestones claimed, on
/// time, late, overdue), the activity behind it, its standing while the
/// opportunity runs, how and when it ended, and the award where it won. The
/// same scopes as the other two reports: every entry for an administrator,
/// the entries into their own opportunities for a client, their own for a
/// freelancer — bar a selection the client took back, which their own list
/// leaves out too. Only an administrator's rows carry the entrant's email,
/// and a freelancer's carry no repository handover; everything else on a
/// row is already on the board or the entry list its reader would open.
/// </summary>
public sealed partial class ReportService
{
    public async Task<Outcome<EntriesReportResponse>> EntriesAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var me = Principal.UserId(principal)!.Value;
        var scope = ScopeOf(Principal.Role(principal));
        var rows = db.UseDapper
            ? await EntriesSqlAsync(db.Sql, scope, me, ct)
            : await Entries(db, scope, me).ToListAsync(ct);
        var entryIds = rows.Select(r => r.Id).ToList();
        var opportunityIds = rows.Select(r => r.OpportunityId).Distinct().ToList();

        // Each opportunity's checklist and its dates, so a row's board is read
        // against the milestones its own opportunity set; the application each
        // entry was selected from, where it had one; the award a winning
        // entry holds, and the stars each side gave once it was paid.
        var facts = db.UseDapper
            ? await EntryFactsSqlAsync(db.Sql, entryIds, opportunityIds, ct)
            : await EntryFactsLinqAsync(db, entryIds, opportunityIds, ct);
        var milestones = facts.Milestones
            .GroupBy(m => m.OpportunityId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(m => m.Order).Select(m => new MilestoneDue(m.Order, m.DueUtc)).ToList());
        var applicationByEntry = facts.Applications.GroupBy(a => a.EntryId).ToDictionary(g => g.Key, g => g.First());
        var awardByEntry = facts.Awards.GroupBy(a => a.EntryId).ToDictionary(g => g.Key, g => g.First());
        var ratingsByAward = facts.Ratings.ToLookup(r => r.AwardId);
        // Where each live entry stands — the number the opportunity board
        // orders by and the entrant's own list shows, read the same way.
        var standings = await StandingReader.ForOpportunitiesAsync(
            db,
            rows.Where(r => r.Status == EntryStatus.Active
                    && r.OpportunityStatus is OpportunityStatus.Open or OpportunityStatus.Reviewing)
                .Select(r => r.OpportunityId).Distinct().ToList(),
            now, ct);

        return Outcome.Ok(new EntriesReportResponse
        {
            GeneratedAtUtc = now,
            Scope = ScopeName(scope),
            Rows = rows.Select(r =>
            {
                var application = applicationByEntry.GetValueOrDefault(r.Id);
                var award = awardByEntry.GetValueOrDefault(r.Id);
                var rated = ratingsByAward[award?.Id ?? Guid.Empty].ToList();
                var endedAt = EndedAt(r.Status, r.WithdrawnAtUtc, r.RemovedAtUtc, application?.DecidedAtUtc);
                var asOf = AsOf(now, r.DeadlineUtc, r.CancelledAtUtc, endedAt);
                var dues = milestones.GetValueOrDefault(r.OpportunityId) ?? [];
                var board = Board(dues, r.Claims, asOf);
                DateTimeOffset? firstClaim = r.Claims.Count == 0 ? null : r.Claims.Min(c => c.ClaimedAtUtc);
                standings.TryGetValue(r.Id, out var standing);
                var category = OpportunityCategories.Find(r.Category);
                return new EntryReportRow
                {
                    Id = r.Id,
                    Status = EntryNames.StatusName(r.Status),
                    Result = EntryResult(r.Status, r.OpportunityStatus, award is not null),
                    FreelancerId = r.FreelancerId,
                    FreelancerName = r.FreelancerName,
                    // An address is the administrator's to read, as on
                    // the applications report.
                    FreelancerEmail = scope == Scope.All ? r.FreelancerEmail : null,
                    OpportunityId = r.OpportunityId,
                    OpportunitySlug = r.OpportunitySlug,
                    OpportunityTitle = r.OpportunityTitle,
                    OpportunityStatus = OpportunityNames.StatusName(r.OpportunityStatus),
                    ClientId = r.ClientId,
                    ClientName = r.ClientName,
                    Category = r.Category,
                    CategoryLabel = category?.Label,
                    Subcategory = r.Subcategory,
                    SubcategoryLabel = category is not null
                        ? OpportunityCategories.FindSub(category, r.Subcategory)?.Label
                        : null,
                    Delivery = Delivery.Name(r.Delivery),
                    AwardAmount = r.AwardAmount,
                    Currency = r.Currency,
                    DeadlineUtc = r.DeadlineUtc,
                    EnteredAtUtc = r.CreatedAtUtc,
                    EndedAtUtc = endedAt,
                    // Entered to whichever came first: the entry's end,
                    // the deadline, a cancellation, or now.
                    DaysIn = DurationDays(r.CreatedAtUtc, asOf),
                    AppliedAtUtc = application?.SubmittedAtUtc,
                    Match = application?.MatchAtSubmit,
                    Note = string.IsNullOrWhiteSpace(r.Note) ? null : r.Note,
                    GithubUsername = string.IsNullOrEmpty(r.GithubUsername) ? null : r.GithubUsername,
                    RepoFullName = r.RepoFullName,
                    // An opportunity handed in by upload alone has no repository to set up.
                    RepoSetup = Delivery.UsesRepository(r.Delivery)
                        ? OpportunityNames.ProvisionName(r.ProvisionStatus)
                        : null,
                    MilestoneCount = dues.Count,
                    MilestonesClaimed = board.Done,
                    OnTime = board.OnTime,
                    Late = board.Late,
                    Overdue = board.Overdue,
                    FirstClaimAtUtc = firstClaim,
                    // Every claim's moment, oldest first — what "claimed
                    // between" narrows by, and a dashboard point's day opens.
                    ClaimedAtUtc = r.Claims.Select(c => c.ClaimedAtUtc).Order().ToList(),
                    DaysToFirstClaim = DurationDays(r.CreatedAtUtc, firstClaim),
                    PushCount = r.PushCount,
                    LastPushAtUtc = r.LastPushAtUtc,
                    FilesUploaded = r.FilesUploaded,
                    RepoFileCount = r.TreeFileCount,
                    RepoHas = RepoHas(r.TreeFileCount, r.TreeHasTests, r.TreeHasReadme, r.TreeHasCi, r.TreeHasCompose),
                    StandingScore = standing?.Score,
                    StandingBand = standing is null ? null : Standing.BandName(standing.Band),
                    StandingRank = standing?.Rank,
                    StandingOf = standing?.Of,
                    // The words that ended it, where somebody gave any:
                    // the removal's, sent to the entrant; the
                    // withdrawal's, read by the client.
                    Reason = r.Status switch
                    {
                        EntryStatus.Removed => r.RemovedReason,
                        EntryStatus.Withdrawn => r.WithdrawnReason,
                        _ => null,
                    },
                    AwardAnnouncedAtUtc = award?.AnnouncedAtUtc,
                    AwardPaidAtUtc = award?.PaidAtUtc,
                    // Where the repository has got to on its way to the
                    // winner — the client's to follow and the
                    // administrator's to unstick, as on the opportunity page.
                    Handover = scope != Scope.Entered && award is not null && Delivery.UsesRepository(r.Delivery)
                        ? AwardNames.HandoverName(award.Handover)
                        : null,
                    RatingOfWinner = rated.FirstOrDefault(x => x.OfUserId == r.FreelancerId)?.Stars,
                    RatingOfClient = rated.FirstOrDefault(x => x.OfUserId == r.ClientId)?.Stars,
                };
            }),
        });
    }

    /// <summary>The row the entries report is built from — the entry with its entrant, opportunity, client and claims.</summary>
    internal sealed class EntryRow
    {
        public Guid Id { get; set; }
        public EntryStatus Status { get; set; }
        public Guid FreelancerId { get; set; }
        public string FreelancerName { get; set; } = "";
        public string FreelancerEmail { get; set; } = "";
        public Guid OpportunityId { get; set; }
        public string OpportunitySlug { get; set; } = "";
        public string OpportunityTitle { get; set; } = "";
        public OpportunityStatus OpportunityStatus { get; set; }
        public Guid ClientId { get; set; }
        public string ClientName { get; set; } = "";
        public string? Category { get; set; }
        public string? Subcategory { get; set; }
        public OpportunityDelivery Delivery { get; set; }
        public decimal AwardAmount { get; set; }
        public string Currency { get; set; } = "";
        public DateTimeOffset? DeadlineUtc { get; set; }
        public DateTimeOffset? CancelledAtUtc { get; set; }
        public string? Note { get; set; }
        public string GithubUsername { get; set; } = "";
        public string? RepoFullName { get; set; }
        public RepoProvisionStatus ProvisionStatus { get; set; }
        public int PushCount { get; set; }
        public DateTimeOffset? LastPushAtUtc { get; set; }
        public int? TreeFileCount { get; set; }
        public bool? TreeHasTests { get; set; }
        public bool? TreeHasReadme { get; set; }
        public bool? TreeHasCi { get; set; }
        public bool? TreeHasCompose { get; set; }
        public DateTimeOffset CreatedAtUtc { get; set; }
        public DateTimeOffset? WithdrawnAtUtc { get; set; }
        public string? WithdrawnReason { get; set; }
        public DateTimeOffset? RemovedAtUtc { get; set; }
        public string? RemovedReason { get; set; }
        public int FilesUploaded { get; set; }
        public List<ClaimRow> Claims { get; set; } = [];
    }

    /// <summary>One milestone claimed: which, by its place in the checklist, and when.</summary>
    internal sealed class ClaimRow
    {
        public int Order { get; set; }
        public DateTimeOffset ClaimedAtUtc { get; set; }
    }

    /// <summary>One milestone of an opportunity's checklist, by its place, with its date if the client set one.</summary>
    internal sealed record MilestoneDue(int Order, DateTimeOffset? DueUtc);

    // ---- the facts beside the rows, whichever database read them; the
    // LINQ is here, the T-SQL in ReportService.SqlServer.cs.

    internal sealed record MilestoneRead(Guid OpportunityId, int Order, DateTimeOffset? DueUtc);

    internal sealed record ApplicationOfEntry(Guid EntryId, DateTimeOffset SubmittedAtUtc, DateTimeOffset? DecidedAtUtc, int MatchAtSubmit);

    internal sealed record AwardOfEntry(Guid Id, Guid EntryId, DateTimeOffset AnnouncedAtUtc, DateTimeOffset? PaidAtUtc, HandoverStatus Handover);

    internal sealed record RatingOfAward(Guid AwardId, Guid OfUserId, int Stars);

    internal sealed record EntryFacts(
        List<MilestoneRead> Milestones,
        List<ApplicationOfEntry> Applications,
        List<AwardOfEntry> Awards,
        List<RatingOfAward> Ratings);

    internal static async Task<EntryFacts> EntryFactsLinqAsync(
        AppDbContext db, List<Guid> entryIds, List<Guid> opportunityIds, CancellationToken ct)
    {
        var milestones = await db.Milestones.AsNoTracking()
            .Where(m => opportunityIds.Contains(m.OpportunityId))
            .Select(m => new MilestoneRead(m.OpportunityId, m.Order, m.DueUtc))
            .ToListAsync(ct);
        var applications = await db.Applications.AsNoTracking()
            .Where(a => a.EntryId != null && entryIds.Contains(a.EntryId.Value))
            .Select(a => new ApplicationOfEntry(a.EntryId!.Value, a.SubmittedAtUtc, a.DecidedAtUtc, a.MatchAtSubmit))
            .ToListAsync(ct);
        var awards = await db.Awards.AsNoTracking()
            .Where(a => entryIds.Contains(a.EntryId))
            .Select(a => new AwardOfEntry(a.Id, a.EntryId, a.AnnouncedAtUtc, a.PaidAtUtc, a.Handover))
            .ToListAsync(ct);
        var awardIds = awards.Select(a => a.Id).ToList();
        var ratings = await db.Ratings.AsNoTracking()
            .Where(r => awardIds.Contains(r.AwardId))
            .Select(r => new RatingOfAward(r.AwardId, r.OfUserId, r.Stars))
            .ToListAsync(ct);
        return new EntryFacts(milestones, applications, awards, ratings);
    }

    /// <summary>
    /// The entries the report covers, whatever became of them, newest first:
    /// every one for an administrator, the ones into opportunities whose client
    /// is asking for a client, their own for a freelancer. Kept as a query
    /// so the tests can render it to SQL without a database.
    /// </summary>
    internal static IQueryable<EntryRow> Entries(AppDbContext db, Scope scope, Guid me)
    {
        IQueryable<Entry> entries = db.Entries.AsNoTracking();
        entries = scope switch
        {
            Scope.Posted => entries.Where(e => e.Opportunity!.ClientId == me),
            // A selection taken back is not an entry they made or left:
            // their application, now not selected, says what happened — as
            // on their own list.
            Scope.Entered => entries.Where(e => e.FreelancerId == me && e.Status != EntryStatus.Deselected),
            _ => entries,
        };
        return entries
            .OrderByDescending(e => e.CreatedAtUtc)
            .ThenByDescending(e => e.Id)
            .Select(e => new EntryRow
            {
                Id = e.Id,
                Status = e.Status,
                FreelancerId = e.FreelancerId,
                FreelancerName = e.Freelancer!.DisplayName,
                FreelancerEmail = e.Freelancer.Email,
                OpportunityId = e.OpportunityId,
                OpportunitySlug = e.Opportunity!.Slug,
                OpportunityTitle = e.Opportunity.Title,
                OpportunityStatus = e.Opportunity.Status,
                ClientId = e.Opportunity.ClientId,
                ClientName = e.Opportunity.Client!.DisplayName,
                Category = e.Opportunity.Category,
                Subcategory = e.Opportunity.Subcategory,
                Delivery = e.Opportunity.Delivery,
                AwardAmount = e.Opportunity.AwardAmount,
                Currency = e.Opportunity.Currency,
                DeadlineUtc = e.Opportunity.DeadlineUtc,
                CancelledAtUtc = e.Opportunity.CancelledAtUtc,
                Note = e.Note,
                GithubUsername = e.GithubUsername,
                RepoFullName = e.RepoFullName,
                ProvisionStatus = e.ProvisionStatus,
                PushCount = e.PushCount,
                LastPushAtUtc = e.LastPushAtUtc,
                TreeFileCount = e.TreeFileCount,
                TreeHasTests = e.TreeHasTests,
                TreeHasReadme = e.TreeHasReadme,
                TreeHasCi = e.TreeHasCi,
                TreeHasCompose = e.TreeHasCompose,
                CreatedAtUtc = e.CreatedAtUtc,
                WithdrawnAtUtc = e.WithdrawnAtUtc,
                WithdrawnReason = e.WithdrawnReason,
                RemovedAtUtc = e.RemovedAtUtc,
                RemovedReason = e.RemovedReason,
                // Handed in means confirmed: an unfinished upload is invisible everywhere.
                FilesUploaded = e.Submissions.Count(s => s.UploadedAtUtc != null),
                Claims = e.Checkpoints
                    .Select(cp => new ClaimRow { Order = cp.Milestone!.Order, ClaimedAtUtc = cp.ClaimedAtUtc })
                    .ToList(),
            });
    }

    /// <summary>
    /// What came of an entry that was still in when its opportunity moved on:
    /// competing, in review, won, lost to another entrant, cancelled. Null
    /// for one that ended first — a withdrawal, a removal, a selection taken
    /// back — whose status is the answer.
    /// </summary>
    internal static string? EntryResult(EntryStatus status, OpportunityStatus opportunityStatus, bool won) =>
        status == EntryStatus.Active
            ? ApplicationRules.Outcome(ApplicationStatus.Selected, opportunityStatus, won)
            : null;

    /// <summary>
    /// When an entry stopped competing: the withdrawal, the removal, or —
    /// for a selection taken back, which stamps nothing on the entry — the
    /// application's answer that took it back. Null while it is still in.
    /// </summary>
    internal static DateTimeOffset? EndedAt(
        EntryStatus status, DateTimeOffset? withdrawnAt, DateTimeOffset? removedAt, DateTimeOffset? takenBackAt) =>
        status switch
        {
            EntryStatus.Withdrawn => withdrawnAt,
            EntryStatus.Removed => removedAt,
            EntryStatus.Deselected => takenBackAt,
            _ => null,
        };

    /// <summary>
    /// The moment a row is read at: now, or the earliest of the deadline, a
    /// cancellation and the entry's own end once any of them has passed. An
    /// entrant who left is not charged with the milestones that came due
    /// after they went, and a frozen board reads as it froze.
    /// </summary>
    internal static DateTimeOffset AsOf(
        DateTimeOffset now, DateTimeOffset? deadline, DateTimeOffset? cancelledAt, DateTimeOffset? endedAt)
    {
        var at = now;
        foreach (var bound in new[] { deadline, cancelledAt, endedAt })
            if (bound is { } b && b < at) at = b;
        return at;
    }

    /// <summary>One entry's board, summarised as the opportunity page summarises it, read at <paramref name="asOf"/>.</summary>
    internal static Schedule.Standing Board(
        IReadOnlyList<MilestoneDue> milestones, IReadOnlyList<ClaimRow> claims, DateTimeOffset asOf) =>
        Schedule.Stand(milestones.Select(m => Schedule.StateOf(
            m.DueUtc,
            claims.FirstOrDefault(c => c.Order == m.Order)?.ClaimedAtUtc,
            asOf)));

    /// <summary>
    /// What the repository's file listing showed the last time the worker
    /// read it — tests, a README, CI, a compose file — and null before the first read, so
    /// "found none" and "not looked yet" stay apart.
    /// </summary>
    internal static IReadOnlyList<string>? RepoHas(int? fileCount, bool? tests, bool? readme, bool? ci, bool? compose = null) =>
        fileCount is null
            ? null
            : new[] { tests == true ? "Tests" : null, readme == true ? "README" : null, ci == true ? "CI" : null, compose == true ? "Compose" : null }
                .OfType<string>()
                .ToList();
}
