using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using WinnersPortal.Domain;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Services.Auth;
using WinnersPortal.Services.Common;
using WinnersPortal.Services.Opportunities;

namespace WinnersPortal.Services.Reports;

/// <summary>
/// Reports: every opportunity as one table, with the figures a client's own
/// list and the public feed each show only a slice of. Who is asking
/// decides which opportunities are theirs to see — an administrator's report is
/// the portal whole, a client's is what they posted, a freelancer's is
/// what they applied to or entered — and the rows are otherwise the same,
/// bar the client's email, which only an administrator reads. One read,
/// every row, no paging: the screen sorts, groups, totals and exports it,
/// and a portal with more opportunities than a browser can hold in a table is
/// a portal with a different problem. Nothing here changes anything.
/// The applications and entries reports are the rest of this class, in
/// ReportService.Applications.cs and ReportService.Entries.cs.
/// </summary>
public sealed partial class ReportService(AppDbContext db, Preview.BuildHostService buildHost)
{
    /// <summary>Whose opportunities the report covers.</summary>
    public enum Scope { All, Posted, Entered }

    public async Task<Outcome<OpportunitiesReportResponse>> OpportunitiesAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var me = Principal.UserId(principal)!.Value;
        var scope = ScopeOf(Principal.Role(principal));
        var rows = db.UseDapper
            ? await OpportunitiesSqlAsync(db.Sql, scope, me, ct)
            : await Opportunities(db, scope, me).ToListAsync(ct);
        var ids = rows.Select(r => r.Id).ToList();

        // The three figures no opportunity row keeps as a counter, each read
        // as one grouped query over the report's opportunities rather than a
        // subquery per row.
        var (applications, entries, awards) = db.UseDapper
            ? await OpportunityFiguresSqlAsync(db.Sql, ids, ct)
            : await OpportunityFiguresLinqAsync(db, ids, ct);
        var awardByOpportunity = awards
            .GroupBy(a => a.OpportunityId)
            .ToDictionary(g => g.Key, g => g.First());
        // A freelancer's own part in each: what became of their
        // application or entry, and whether the award was theirs.
        var mine = scope == Scope.Entered
            ? await MyPartsAsync(db, ids, me, ct)
            : new Dictionary<Guid, string>();
        // The counts that describe other people's applications and the
        // client's payment are the client's and the administrators' to
        // read, not every entrant's.
        var owner = scope != Scope.Entered;

        return Outcome.Ok(new OpportunitiesReportResponse
        {
            GeneratedAtUtc = now,
            Scope = ScopeName(scope),
            Rows = rows.Select(r =>
            {
                applications.TryGetValue(r.Id, out var apps);
                entries.TryGetValue(r.Id, out var ents);
                awardByOpportunity.TryGetValue(r.Id, out var award);
                var starts = Schedule.StartsAt(r.StartsAtUtc, r.PublishedAtUtc);
                return new OpportunityReportRow
                {
                    Id = r.Id,
                    Slug = r.Slug,
                    Title = r.Title,
                    Status = OpportunityNames.StatusName(r.Status),
                    ClientId = r.ClientId,
                    ClientName = r.ClientName,
                    // An address is the administrator's to read; a
                    // client already knows their own and a freelancer
                    // is not owed the client's.
                    ClientEmail = scope == Scope.All ? r.ClientEmail : null,
                    Category = r.Category,
                    CategoryLabel = OpportunityCategories.Find(r.Category)?.Label,
                    Subcategory = r.Subcategory,
                    SubcategoryLabel = OpportunityCategories.Find(r.Category) is { } cat
                        ? OpportunityCategories.FindSub(cat, r.Subcategory)?.Label
                        : null,
                    Delivery = Delivery.Name(r.Delivery),
                    AwardAmount = r.AwardAmount,
                    Currency = r.Currency,
                    MinMeritScore = r.MinMeritScore,
                    Skills = r.Skills,
                    CreatedAtUtc = r.CreatedAtUtc,
                    PublishedAtUtc = r.PublishedAtUtc,
                    StartsAtUtc = starts,
                    DeadlineUtc = r.DeadlineUtc,
                    EntryCloseUtc = Schedule.EntryClosesAt(r.EntryCloseUtc, r.DeadlineUtc),
                    CancelledAtUtc = r.CancelledAtUtc,
                    // The build window in days, start to deadline, a
                    // tenth of a day fine; null while a draft has no
                    // deadline yet.
                    DurationDays = DurationDays(starts, r.DeadlineUtc),
                    ApplicationCount = apps?.Total ?? 0,
                    ApplicationsWaiting = owner ? apps?.Waiting ?? 0 : (int?)null,
                    ApplicationsSelected = owner ? apps?.Selected ?? 0 : (int?)null,
                    EntrantCount = r.ActiveEntryCount,
                    EntriesTotal = owner ? ents?.Total ?? 0 : (int?)null,
                    EntriesWithdrawn = owner ? ents?.Withdrawn ?? 0 : (int?)null,
                    EntriesRemoved = owner ? ents?.Removed ?? 0 : (int?)null,
                    MilestoneCount = r.MilestoneCount,
                    WinnerName = award?.Winner,
                    AwardAnnouncedAtUtc = award?.AnnouncedAtUtc,
                    // The winner is owed their own paid date.
                    AwardPaidAtUtc = owner || mine.GetValueOrDefault(r.Id) == "won" ? award?.PaidAtUtc : null,
                    MyStatus = mine.GetValueOrDefault(r.Id),
                };
            }),
        });
    }

    /// <summary>The whole portal for an administrator; what they posted for a client; what they applied to or entered for a freelancer.</summary>
    internal static Scope ScopeOf(string? role) => role switch
    {
        "admin" => Scope.All,
        "client" => Scope.Posted,
        _ => Scope.Entered,
    };

    internal static string ScopeName(Scope scope) => scope switch
    {
        Scope.All => "all",
        Scope.Posted => "posted",
        _ => "entered",
    };

    /// <summary>The row the report is built from — the opportunity and its client, newest first.</summary>
    internal sealed class Row
    {
        public Guid Id { get; set; }
        public string Slug { get; set; } = "";
        public string Title { get; set; } = "";
        public OpportunityStatus Status { get; set; }
        public Guid ClientId { get; set; }
        public string ClientName { get; set; } = "";
        public string ClientEmail { get; set; } = "";
        public string? Category { get; set; }
        public string? Subcategory { get; set; }
        public OpportunityDelivery Delivery { get; set; }
        public decimal AwardAmount { get; set; }
        public string Currency { get; set; } = "";
        public int MinMeritScore { get; set; }
        public List<string> Skills { get; set; } = [];
        public DateTimeOffset CreatedAtUtc { get; set; }
        public DateTimeOffset? PublishedAtUtc { get; set; }
        public DateTimeOffset? StartsAtUtc { get; set; }
        public DateTimeOffset? DeadlineUtc { get; set; }
        public DateTimeOffset? EntryCloseUtc { get; set; }
        public DateTimeOffset? CancelledAtUtc { get; set; }
        public int ActiveEntryCount { get; set; }
        public int MilestoneCount { get; set; }
    }

    /// <summary>
    /// The opportunities the report covers, whatever their state, with the client
    /// joined — the one query the report pays for the rows themselves. A
    /// freelancer's are the ones an application or an entry of theirs
    /// belongs to, so an opportunity they withdrew from or were removed from is
    /// still a row: it happened. Kept as a query so the tests can render it
    /// to SQL without a database.
    /// </summary>
    internal static IQueryable<Row> Opportunities(AppDbContext db, Scope scope, Guid me)
    {
        IQueryable<Opportunity> opportunities = db.Opportunities.AsNoTracking();
        opportunities = scope switch
        {
            Scope.Posted => opportunities.Where(c => c.ClientId == me),
            Scope.Entered => opportunities.Where(c =>
                db.Applications.Any(a => a.OpportunityId == c.Id && a.FreelancerId == me)
                || db.Entries.Any(e => e.OpportunityId == c.Id && e.FreelancerId == me)),
            _ => opportunities,
        };
        return opportunities
            .OrderByDescending(c => c.CreatedAtUtc)
            .ThenByDescending(c => c.Id)
            .Select(c => new Row
            {
                Id = c.Id,
                Slug = c.Slug,
                Title = c.Title,
                Status = c.Status,
                ClientId = c.ClientId,
                ClientName = c.Client!.DisplayName,
                ClientEmail = c.Client.Email,
                Category = c.Category,
                Subcategory = c.Subcategory,
                Delivery = c.Delivery,
                AwardAmount = c.AwardAmount,
                Currency = c.Currency,
                MinMeritScore = c.MinMeritScore,
                Skills = c.Skills.OrderBy(s => s.Order).Select(s => s.Name).ToList(),
                CreatedAtUtc = c.CreatedAtUtc,
                PublishedAtUtc = c.PublishedAtUtc,
                StartsAtUtc = c.StartsAtUtc,
                DeadlineUtc = c.DeadlineUtc,
                EntryCloseUtc = c.EntryCloseUtc,
                CancelledAtUtc = c.CancelledAtUtc,
                ActiveEntryCount = c.ActiveEntryCount,
                MilestoneCount = c.MilestoneCount,
            });
    }

    /// <summary>
    /// Where a freelancer stands in each of the report's opportunities: their
    /// application's state where they sent one, their latest entry's where
    /// they entered without one, and "won" over either once the award was
    /// theirs.
    /// </summary>
    internal static async Task<Dictionary<Guid, string>> MyPartsAsync(
        AppDbContext db, List<Guid> ids, Guid me, CancellationToken ct)
    {
        var (entries, applications, won) = db.UseDapper
            ? await MyPartsSqlAsync(db.Sql, ids, me, ct)
            : await MyPartsLinqAsync(db, ids, me, ct);

        var parts = new Dictionary<Guid, string>();
        foreach (var e in entries) parts[e.OpportunityId] = Part(e.Status);
        foreach (var a in applications) parts[a.OpportunityId] = Part(a.Status);
        foreach (var id in won) parts[id] = "won";
        return parts;
    }

    // ---- the figures beside the rows, whichever database read them; the
    // LINQ is here, the T-SQL in ReportService.SqlServer.cs.

    internal sealed record ApplicationFigures(Guid Key, int Total, int Waiting, int Selected);

    internal sealed record EntryFigures(Guid Key, int Total, int Withdrawn, int Removed);

    /// <summary>One award, oldest first where an opportunity somehow has two.</summary>
    internal sealed record AwardFigure(Guid OpportunityId, string Winner, DateTimeOffset AnnouncedAtUtc, DateTimeOffset? PaidAtUtc);

    internal sealed record OpportunityFigures(
        Dictionary<Guid, ApplicationFigures> Applications,
        Dictionary<Guid, EntryFigures> Entries,
        List<AwardFigure> Awards);

    internal static async Task<OpportunityFigures> OpportunityFiguresLinqAsync(AppDbContext db, List<Guid> ids, CancellationToken ct)
    {
        var applications = await db.Applications.AsNoTracking()
            .Where(a => ids.Contains(a.OpportunityId))
            .GroupBy(a => a.OpportunityId)
            .Select(g => new ApplicationFigures(
                g.Key,
                g.Count(),
                g.Count(a => a.Status == ApplicationStatus.UnderReview),
                g.Count(a => a.Status == ApplicationStatus.Selected)))
            .ToDictionaryAsync(x => x.Key, ct);
        var entries = await db.Entries.AsNoTracking()
            .Where(e => ids.Contains(e.OpportunityId))
            .GroupBy(e => e.OpportunityId)
            .Select(g => new EntryFigures(
                g.Key,
                g.Count(),
                g.Count(e => e.Status == EntryStatus.Withdrawn),
                g.Count(e => e.Status == EntryStatus.Removed)))
            .ToDictionaryAsync(x => x.Key, ct);
        var awards = await db.Awards.AsNoTracking()
            .Where(a => ids.Contains(a.OpportunityId))
            .OrderBy(a => a.AnnouncedAtUtc)
            .Select(a => new AwardFigure(a.OpportunityId, a.Entry!.Freelancer!.DisplayName, a.AnnouncedAtUtc, a.PaidAtUtc))
            .ToListAsync(ct);
        return new OpportunityFigures(applications, entries, awards);
    }

    internal sealed record PartEntry(Guid OpportunityId, EntryStatus Status);

    internal sealed record PartApplication(Guid OpportunityId, ApplicationStatus Status);

    /// <summary>A freelancer's entries (oldest first, so the latest wins), applications, and the opportunities they won.</summary>
    internal sealed record MyParts(List<PartEntry> Entries, List<PartApplication> Applications, List<Guid> Won);

    internal static async Task<MyParts> MyPartsLinqAsync(AppDbContext db, List<Guid> ids, Guid me, CancellationToken ct)
    {
        var entries = await db.Entries.AsNoTracking()
            .Where(e => e.FreelancerId == me && ids.Contains(e.OpportunityId))
            .OrderBy(e => e.CreatedAtUtc)
            .Select(e => new PartEntry(e.OpportunityId, e.Status))
            .ToListAsync(ct);
        var applications = await db.Applications.AsNoTracking()
            .Where(a => a.FreelancerId == me && ids.Contains(a.OpportunityId))
            .Select(a => new PartApplication(a.OpportunityId, a.Status))
            .ToListAsync(ct);
        var won = await db.Awards.AsNoTracking()
            .Where(a => a.Entry!.FreelancerId == me && ids.Contains(a.OpportunityId))
            .Select(a => a.OpportunityId)
            .ToListAsync(ct);
        return new MyParts(entries, applications, won);
    }

    internal static string Part(ApplicationStatus status) => status switch
    {
        ApplicationStatus.UnderReview => "under_review",
        ApplicationStatus.Selected => "selected",
        ApplicationStatus.NotSelected => "not_selected",
        ApplicationStatus.Removed => "removed",
        _ => "withdrawn",
    };

    internal static string Part(EntryStatus status) => status switch
    {
        EntryStatus.Active => "selected",
        EntryStatus.Deselected => "not_selected",
        EntryStatus.Removed => "removed",
        _ => "withdrawn",
    };

    /// <summary>Start to deadline in days, to a tenth; null until both are known. Never negative — a deadline before the start reads as zero.</summary>
    internal static double? DurationDays(DateTimeOffset? starts, DateTimeOffset? deadline) =>
        starts is { } s && deadline is { } d
            ? Math.Max(0, Math.Round((d - s).TotalDays, 1))
            : null;
}
