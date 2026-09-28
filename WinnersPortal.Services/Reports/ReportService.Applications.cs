using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using WinnersPortal.Domain;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Services.Auth;
using WinnersPortal.Services.Common;
using WinnersPortal.Services.Opportunities;

namespace WinnersPortal.Services.Reports;

/// <summary>
/// The applications report: one row per application, with what the
/// applicant offered, how the portal read it when it was sent, what the
/// client answered and how long that took, and what came of it. The same
/// scopes as the opportunities report — every application for an
/// administrator, the ones to their own opportunities for a client, their own
/// for a freelancer — and the same rule: what a scope withholds, the API
/// withholds. Only an administrator's rows carry the applicant's email;
/// everything else on a row is already on the page its reader would open
/// — the client's Applications box, or the applicant's own application.
/// </summary>
public sealed partial class ReportService
{
    public async Task<Outcome<ApplicationsReportResponse>> ApplicationsAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var me = Principal.UserId(principal)!.Value;
        var scope = ScopeOf(Principal.Role(principal));
        var rows = db.UseDapper
            ? await ApplicationsSqlAsync(db.Sql, scope, me, ct)
            : await Applications(db, scope, me).ToListAsync(ct);

        // What became of the entry each selection made — one query over
        // the report's entries, the milestones claimed counted in it —
        // and which of those entries won, and when the award was paid.
        var entryIds = rows.Where(r => r.EntryId is not null).Select(r => r.EntryId!.Value).ToList();
        var (entryRows, awards) = db.UseDapper
            ? await ApplicationOutcomesSqlAsync(db.Sql, entryIds, ct)
            : await ApplicationOutcomesLinqAsync(db, entryIds, ct);
        var entries = entryRows.ToDictionary(e => e.Id);
        var awardByEntry = awards.GroupBy(a => a.EntryId).ToDictionary(g => g.Key, g => g.First());
        var advantageLabels = ApplicationRules.Advantages.ToDictionary(a => a.Key, a => a.Label);

        return Outcome.Ok(new ApplicationsReportResponse
        {
            GeneratedAtUtc = now,
            Scope = ScopeName(scope),
            Rows = rows.Select(r =>
            {
                var entry = r.EntryId is { } eid ? entries.GetValueOrDefault(eid) : null;
                var award = r.EntryId is { } wid ? awardByEntry.GetValueOrDefault(wid) : null;
                var answeredAt = AnsweredAt(r.Status, r.DecidedAtUtc, entry?.CreatedAtUtc);
                var category = OpportunityCategories.Find(r.Category);
                return new ApplicationReportRow
                {
                    Id = r.Id,
                    Status = ApplicationRules.StatusName(r.Status),
                    Result = Result(r.Status, r.OpportunityStatus, award is not null),
                    FreelancerId = r.FreelancerId,
                    FreelancerName = r.FreelancerName,
                    // An address is the administrator's to read: a
                    // client reaches an applicant through the portal,
                    // and a freelancer already knows their own.
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
                    AwardAmount = r.AwardAmount,
                    Currency = r.Currency,
                    MinMeritScore = r.MinMeritScore,
                    DeadlineUtc = r.DeadlineUtc,
                    EntryCloseUtc = Schedule.EntryClosesAt(r.EntryCloseUtc, r.DeadlineUtc),
                    SubmittedAtUtc = r.SubmittedAtUtc,
                    AnsweredAtUtc = answeredAt,
                    // The decision, or whatever changed the answer since:
                    // a removal, a withdrawal, a selection taken back.
                    ChangedAtUtc = r.DecidedAtUtc,
                    DaysToAnswer = DurationDays(r.SubmittedAtUtc, answeredAt),
                    Match = r.MatchAtSubmit,
                    MeritScore = r.MeritAtSubmit,
                    Band = Band(r.EvaluationJson),
                    Commitment = ApplicationRules.CommitmentName(r.Commitment),
                    HoursPerWeek = r.HoursPerWeek,
                    PortfolioCount = r.PortfolioCount,
                    Advantages = r.Advantages
                        .Select(k => advantageLabels.GetValueOrDefault(k))
                        .Where(l => l is not null),
                    GithubUsername = string.IsNullOrEmpty(r.GithubUsername) ? null : r.GithubUsername,
                    EntryStatus = entry is null ? null : EntryNames.StatusName(entry.Status),
                    MilestonesClaimed = entry?.Claimed,
                    PushCount = entry?.PushCount,
                    LastPushAtUtc = entry?.LastPushAtUtc,
                    // The words that ended it, where somebody gave any:
                    // the removal's, sent to the entrant; the
                    // withdrawal's, read by the client.
                    Reason = r.Status switch
                    {
                        ApplicationStatus.Removed => entry?.RemovedReason,
                        ApplicationStatus.Withdrawn => entry?.WithdrawnReason,
                        _ => null,
                    },
                    PaidAtUtc = award?.PaidAtUtc,
                };
            }),
        });
    }

    /// <summary>What became of a selection's entry.</summary>
    internal sealed record EntryOutcome(
        Guid Id, EntryStatus Status, DateTimeOffset CreatedAtUtc, int PushCount, DateTimeOffset? LastPushAtUtc,
        string? WithdrawnReason, string? RemovedReason, int Claimed);

    internal sealed record AwardPaid(Guid EntryId, DateTimeOffset? PaidAtUtc);

    internal sealed record ApplicationOutcomes(List<EntryOutcome> Entries, List<AwardPaid> Awards);

    internal static async Task<ApplicationOutcomes> ApplicationOutcomesLinqAsync(AppDbContext db, List<Guid> entryIds, CancellationToken ct)
    {
        var entries = await db.Entries.AsNoTracking()
            .Where(e => entryIds.Contains(e.Id))
            .Select(e => new EntryOutcome(
                e.Id, e.Status, e.CreatedAtUtc, e.PushCount, e.LastPushAtUtc, e.WithdrawnReason, e.RemovedReason,
                e.Checkpoints.Count()))
            .ToListAsync(ct);
        var awards = await db.Awards.AsNoTracking()
            .Where(a => entryIds.Contains(a.EntryId))
            .Select(a => new AwardPaid(a.EntryId, a.PaidAtUtc))
            .ToListAsync(ct);
        return new ApplicationOutcomes(entries, awards);
    }

    /// <summary>
    /// The row the applications report is built from — the application
    /// with its applicant, opportunity and client. Not sealed: the T-SQL reads
    /// it through a subclass that carries the advantages as their JSON.
    /// </summary>
    internal class ApplicationRow
    {
        public Guid Id { get; set; }
        public ApplicationStatus Status { get; set; }
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
        public decimal AwardAmount { get; set; }
        public string Currency { get; set; } = "";
        public int MinMeritScore { get; set; }
        public DateTimeOffset? DeadlineUtc { get; set; }
        public DateTimeOffset? EntryCloseUtc { get; set; }
        public DateTimeOffset SubmittedAtUtc { get; set; }
        public DateTimeOffset? DecidedAtUtc { get; set; }
        public Guid? EntryId { get; set; }
        public int MatchAtSubmit { get; set; }
        public int MeritAtSubmit { get; set; }
        public string EvaluationJson { get; set; } = "{}";
        public Commitment Commitment { get; set; }
        public int HoursPerWeek { get; set; }
        public int PortfolioCount { get; set; }
        public string[] Advantages { get; set; } = [];
        public string GithubUsername { get; set; } = "";
    }

    /// <summary>
    /// The applications the report covers, whatever became of them, newest
    /// first: every one for an administrator, the ones to opportunities whose
    /// client is asking for a client, their own for a freelancer. Kept as a
    /// query so the tests can render it to SQL without a database.
    /// </summary>
    internal static IQueryable<ApplicationRow> Applications(AppDbContext db, Scope scope, Guid me)
    {
        IQueryable<Application> applications = db.Applications.AsNoTracking();
        applications = scope switch
        {
            Scope.Posted => applications.Where(a => a.Opportunity!.ClientId == me),
            Scope.Entered => applications.Where(a => a.FreelancerId == me),
            _ => applications,
        };
        return applications
            .OrderByDescending(a => a.SubmittedAtUtc)
            .ThenByDescending(a => a.Id)
            .Select(a => new ApplicationRow
            {
                Id = a.Id,
                Status = a.Status,
                FreelancerId = a.FreelancerId,
                FreelancerName = a.Freelancer!.DisplayName,
                FreelancerEmail = a.Freelancer.Email,
                OpportunityId = a.OpportunityId,
                OpportunitySlug = a.Opportunity!.Slug,
                OpportunityTitle = a.Opportunity.Title,
                OpportunityStatus = a.Opportunity.Status,
                ClientId = a.Opportunity.ClientId,
                ClientName = a.Opportunity.Client!.DisplayName,
                Category = a.Opportunity.Category,
                Subcategory = a.Opportunity.Subcategory,
                AwardAmount = a.Opportunity.AwardAmount,
                Currency = a.Opportunity.Currency,
                MinMeritScore = a.Opportunity.MinMeritScore,
                DeadlineUtc = a.Opportunity.DeadlineUtc,
                EntryCloseUtc = a.Opportunity.EntryCloseUtc,
                SubmittedAtUtc = a.SubmittedAtUtc,
                DecidedAtUtc = a.DecidedAtUtc,
                EntryId = a.EntryId,
                MatchAtSubmit = a.MatchAtSubmit,
                MeritAtSubmit = a.MeritAtSubmit,
                EvaluationJson = a.EvaluationJson,
                Commitment = a.Commitment,
                HoursPerWeek = a.HoursPerWeek,
                PortfolioCount = a.PortfolioCount,
                Advantages = a.Advantages,
                GithubUsername = a.GithubUsername,
            });
    }

    /// <summary>
    /// When the client first answered, as far as the record keeps it. A
    /// selection makes an entry, so the entry's making is the selection's
    /// moment — and a selection later withdrawn from, removed or taken back
    /// still counts from it, where the application's own decided stamp has
    /// moved on to whatever happened last. Otherwise the decision; null
    /// while it waits.
    /// </summary>
    internal static DateTimeOffset? AnsweredAt(ApplicationStatus status, DateTimeOffset? decidedAt, DateTimeOffset? entryMadeAt) =>
        status == ApplicationStatus.UnderReview ? null : entryMadeAt ?? decidedAt;

    /// <summary>
    /// What came of an application: a selection's outcome as its opportunity
    /// went (open, reviewing, won, lost, cancelled), "unanswered" for one
    /// still waiting when its opportunity closed, and null for the rest — one
    /// waiting on an open opportunity, one not selected, one withdrawn or removed.
    /// </summary>
    internal static string? Result(ApplicationStatus status, OpportunityStatus opportunityStatus, bool won) =>
        ApplicationRules.Outcome(status, opportunityStatus, won)
        ?? (ApplicationRules.ClosedUndecided(status, opportunityStatus) ? "unanswered" : null);

    /// <summary>The evaluation's word as it was filed — "Strong Candidate" and the rest — or null where none was.</summary>
    internal static string? Band(string evaluationJson) =>
        ApplicationService.StoredEvaluation(evaluationJson).Band is { Length: > 0 } band && band != "Unread"
            ? band
            : null;
}
