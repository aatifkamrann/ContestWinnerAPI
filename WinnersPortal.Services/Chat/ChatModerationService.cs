using System.Linq.Expressions;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using WinnersPortal.Domain;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Services.Activity;
using WinnersPortal.Services.Auth;
using WinnersPortal.Services.Common;
using WinnersPortal.Services.Email;
using WinnersPortal.Services.Live;
using WinnersPortal.Services.Opportunities;
using WinnersPortal.Services.Profiles;

namespace WinnersPortal.Services.Chat;

/// <summary>
/// The administrator's view of the conversations between clients and
/// entrants, for moderation: every conversation with something said in it,
/// newest first, narrowed to one member or to the words typed, and one of
/// them read a page at a time. Reading only — nothing is marked read,
/// nobody's room is rung, and no line can be added. The list carries who,
/// about what, how many lines and when the last was said, never what was
/// said; the words are on the conversation itself, whose every opening is
/// a row in the activity log ("Read a conversation") naming the
/// opportunity and the two people. A member's report brings one here; the
/// administrator marks the conversation's open reports reviewed, which tells
/// each reporter it was looked at and nothing more.
/// </summary>
public sealed partial class ChatModerationService(AppDbContext db, ActivityNote note, ILiveChat live, EmailWorkSignal emailSignal)
{
    public async Task<Outcome<AdminConversationsResponse>> ListAsync(string? q, Guid? member, bool reported, string? cursor, int? take, CancellationToken ct)
    {
        var size = Math.Clamp(take ?? ChatRules.AdminPageSize, 1, ChatRules.MaxPageSize);
        var like = string.IsNullOrWhiteSpace(q) ? null : Search.TitlePattern(q);
        var before = Cursor.Decode(cursor);
        // One more than the page, to know whether there is another.
        var page = db.UseDapper
            ? await ListSqlAsync(db.Sql, like, member, reported, before, size + 1, ct)
            : await ListLinqAsync(db, like, member, reported, before, size + 1, ct);
        var more = page.Count > size;
        var rows = more ? page[..size] : page;
        return Outcome.Ok(new AdminConversationsResponse
        {
            Items = rows.Select(View).ToList(),
            NextCursor = more && rows[^1].LastAtUtc is { } at ? new Cursor(at, rows[^1].EntryId).Encode() : null,
        });
    }

    /// <summary>
    /// One conversation, newest lines first, from the cursor the page before
    /// handed back. Any entry's — one where nothing was said yet reads as
    /// empty. The activity row's subject names it, so the log says whose
    /// conversation an administrator read without anyone opening the path.
    /// The first page carries the conversation's reports, open ones first.
    /// </summary>
    public async Task<Outcome<AdminConversationResponse>> ReadAsync(Guid entryId, string? cursor, int? take, CancellationToken ct)
    {
        var row = db.UseDapper
            ? await OneSqlAsync(db.Sql, entryId, ct)
            : await OneLinqAsync(db, entryId, ct);
        if (row is null) return Outcome.NotFound();
        note.Subject = ChatRules.ModerationSubject(row.Title, row.ClientName, row.FreelancerName);

        var size = Math.Clamp(take ?? ChatRules.PageSize, 1, ChatRules.MaxPageSize);
        var before = Cursor.Decode(cursor);
        var page = db.UseDapper
            ? await PageSqlAsync(db.Sql, entryId, before, size + 1, ct)
            : await ChatService.PageLinqAsync(db, entryId, before, size + 1, ct);
        var more = page.Count > size;
        var lines = more ? page[..size] : page;
        var reports = before is null ? await ReportsAsync(entryId, ct) : [];
        return Outcome.Ok(new AdminConversationResponse
        {
            Conversation = View(row),
            Reports = reports.Select(r => View(r, row.FreelancerId)).ToList(),
            Items = lines.Select(m => new AdminMessageView
            {
                Id = m.Id,
                Body = m.Body,
                From = ChatRules.SeatOf(m.SenderId, row.FreelancerId),
                CreatedAtUtc = m.CreatedAtUtc,
                ReadAtUtc = m.ReadAtUtc,
            }).ToList(),
            NextCursor = more ? new Cursor(lines[^1].CreatedAtUtc, lines[^1].Id).Encode() : null,
        });
    }

    /// <summary>
    /// The conversation's open reports marked reviewed, all at once, with
    /// the administrator's note for the next one to read. Each reporter is
    /// emailed that it was reviewed — not what was done — and may report it
    /// again; their open tabs lose the "reported" mark.
    /// </summary>
    public async Task<Outcome<ResolveReportsResponse>> ResolveAsync(Guid entryId, ResolveReportsRequest request, ClaimsPrincipal principal, CancellationToken ct)
    {
        var row = db.UseDapper
            ? await OneSqlAsync(db.Sql, entryId, ct)
            : await OneLinqAsync(db, entryId, ct);
        if (row is null) return Outcome.NotFound();
        note.Subject = ChatRules.ModerationSubject(row.Title, row.ClientName, row.FreelancerName);
        var (text, error) = ChatRules.CheckResolution(request.Note);
        if (error is not null) return Outcome.Invalid(error);

        var reporters = (await ReportsAsync(entryId, ct))
            .Where(r => r.ResolvedAtUtc is null).Select(r => r.ReporterId).Distinct().ToList();
        if (reporters.Count == 0) return Outcome.Conflict(ChatRules.NothingToResolve);

        var me = Principal.UserId(principal)!.Value;
        var now = DateTimeOffset.UtcNow;
        var changed = db.UseDapper
            ? await db.Sql.ExecuteAsync(Procedures.ChatResolveReports, new { entryId, by = me, at = now, note = text }, ct)
            : await db.ChatReports
                .Where(r => r.EntryId == entryId && r.ResolvedAtUtc == null)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.ResolvedAtUtc, now)
                    .SetProperty(r => r.ResolvedById, me)
                    .SetProperty(r => r.Resolution, text), ct);
        // Another administrator got there first.
        if (changed == 0) return Outcome.Conflict(ChatRules.NothingToResolve);

        foreach (var reporter in await db.Users.AsNoTracking()
                     .Where(u => reporters.Contains(u.Id) && u.ErasedAtUtc == null).ToListAsync(ct))
            Notify.Queue(db, reporter, "chat_report_reviewed", Emails.ChatReportReviewed(entryId, row.Title,
                reporter.Id == row.FreelancerId ? row.ClientName : row.FreelancerName));
        await db.SaveChangesAsync(ct);
        emailSignal.Wake();
        foreach (var reporter in reporters) await live.ChatChangedAsync(reporter, entryId, ct);
        return Outcome.Ok(new ResolveReportsResponse { Resolved = changed });
    }

    /// <summary>
    /// The reports nobody has reviewed yet, portal-wide: the dashboard's
    /// attention line. On SQL Server, Chat_ReportsOpen.
    /// </summary>
    internal static Task<OpenReportsRow> OpenReportsAsync(AppDbContext db, CancellationToken ct) =>
        db.UseDapper ? OpenSqlAsync(db.Sql, ct) : OpenReportsLinqAsync(db, ct);

    private Task<List<ReportRow>> ReportsAsync(Guid entryId, CancellationToken ct) =>
        db.UseDapper ? ReportsSqlAsync(db.Sql, entryId, ct) : ReportsLinqAsync(db, entryId, ct);

    // ------------------------------------------------------------- rows

    internal sealed record ConversationRow(
        Guid EntryId, EntryStatus EntryStatus, string Slug, string Title, OpportunityStatus OpportunityStatus,
        Guid ClientId, string ClientName, DateTimeOffset? ClientAvatarUpdatedAtUtc,
        Guid FreelancerId, string FreelancerName, DateTimeOffset? FreelancerAvatarUpdatedAtUtc,
        int Messages, DateTimeOffset? LastAtUtc, Guid? LastSenderId, int OpenReports);

    internal sealed record ReportRow(
        Guid Id, Guid ReporterId, string ReporterName, string Reason, string? Details, DateTimeOffset CreatedAtUtc,
        DateTimeOffset? ResolvedAtUtc, string? ResolvedByName, string? Resolution);

    internal sealed record OpenReportsRow(int Conversations, int Reports, DateTimeOffset? OldestUtc);

    internal static AdminReportView View(ReportRow r, Guid freelancerId) => new()
    {
        Id = r.Id,
        From = ChatRules.SeatOf(r.ReporterId, freelancerId),
        ReporterName = r.ReporterName,
        Reason = r.Reason,
        ReasonLabel = ChatRules.ReasonLabel(r.Reason),
        Details = r.Details,
        CreatedAtUtc = r.CreatedAtUtc,
        ResolvedAtUtc = r.ResolvedAtUtc,
        ResolvedByName = r.ResolvedByName,
        Resolution = r.Resolution,
    };

    internal static AdminConversationView View(ConversationRow r) => new()
    {
        EntryId = r.EntryId,
        Opportunity = new ChatOpportunityView
        {
            Slug = r.Slug,
            Title = r.Title,
            Status = OpportunityNames.StatusName(r.OpportunityStatus),
        },
        Client = new ChatPartyView
        {
            Id = r.ClientId,
            DisplayName = r.ClientName,
            AvatarUrl = AvatarRules.Url(r.ClientId, r.ClientAvatarUpdatedAtUtc),
        },
        Freelancer = new ChatPartyView
        {
            Id = r.FreelancerId,
            DisplayName = r.FreelancerName,
            AvatarUrl = AvatarRules.Url(r.FreelancerId, r.FreelancerAvatarUpdatedAtUtc),
        },
        EntryStatus = EntryNames.StatusName(r.EntryStatus),
        Open = ChatRules.CanSend(r.EntryStatus, r.OpportunityStatus),
        Messages = r.Messages,
        LastAtUtc = r.LastAtUtc,
        LastFrom = r.LastSenderId is { } sender ? ChatRules.SeatOf(sender, r.FreelancerId) : null,
        OpenReports = r.OpenReports,
    };

    // ------------------------------------------------------------- LINQ
    // The Postgres path, and the diagnostic run on SQL Server; the shape of
    // Chat_AdminList and Chat_AdminThread beside them.

    internal static Task<List<ConversationRow>> ListLinqAsync(
        AppDbContext db, string? like, Guid? member, bool reported, Cursor? before, int take, CancellationToken ct)
    {
        var rows = db.Entries.AsNoTracking().Where(e => db.ChatMessages.Any(m => m.EntryId == e.Id));
        if (member is { } id)
            rows = rows.Where(e => e.FreelancerId == id || e.Opportunity!.ClientId == id);
        if (like is not null)
            rows = rows.Where(Match(like, db.IsSqlServer));
        if (reported)
            rows = rows.Where(e => db.ChatReports.Any(r => r.EntryId == e.Id && r.ResolvedAtUtc == null));
        if (before is { } c)
            rows = rows.Where(e =>
                db.ChatMessages.Where(m => m.EntryId == e.Id).Max(m => (DateTimeOffset?)m.CreatedAtUtc) < c.At
                || (db.ChatMessages.Where(m => m.EntryId == e.Id).Max(m => (DateTimeOffset?)m.CreatedAtUtc) == c.At
                    && e.Id.CompareTo(c.Id) < 0));
        return rows
            .OrderByDescending(e => db.ChatMessages.Where(m => m.EntryId == e.Id).Max(m => (DateTimeOffset?)m.CreatedAtUtc))
            .ThenByDescending(e => e.Id)
            .Take(take)
            .Select(Row(db))
            .ToListAsync(ct);
    }

    internal static Task<ConversationRow?> OneLinqAsync(AppDbContext db, Guid entryId, CancellationToken ct) =>
        db.Entries.AsNoTracking()
            .Where(e => e.Id == entryId)
            .Select(Row(db))
            .SingleOrDefaultAsync(ct);

    /// <summary>
    /// The words typed against the opportunity's title and both people's
    /// names and addresses, case aside: ILIKE on Postgres, LIKE on SQL
    /// Server, whose default collation already ignores case. Never against
    /// the lines themselves — what was said is read one conversation at a
    /// time, each reading logged.
    /// </summary>
    private static Expression<Func<Entry, bool>> Match(string like, bool sqlServer) => sqlServer
        ? e =>
            EF.Functions.Like(e.Opportunity!.Title, like, Search.Escape)
            || EF.Functions.Like(e.Opportunity.Client!.DisplayName, like, Search.Escape)
            || EF.Functions.Like(e.Opportunity.Client.Email, like, Search.Escape)
            || EF.Functions.Like(e.Freelancer!.DisplayName, like, Search.Escape)
            || EF.Functions.Like(e.Freelancer.Email, like, Search.Escape)
        : e =>
            EF.Functions.ILike(e.Opportunity!.Title, like, Search.Escape)
            || EF.Functions.ILike(e.Opportunity.Client!.DisplayName, like, Search.Escape)
            || EF.Functions.ILike(e.Opportunity.Client.Email, like, Search.Escape)
            || EF.Functions.ILike(e.Freelancer!.DisplayName, like, Search.Escape)
            || EF.Functions.ILike(e.Freelancer.Email, like, Search.Escape);

    private static Expression<Func<Entry, ConversationRow>> Row(AppDbContext db) => e => new ConversationRow(
        e.Id, e.Status, e.Opportunity!.Slug, e.Opportunity.Title, e.Opportunity.Status,
        e.Opportunity.ClientId, e.Opportunity.Client!.DisplayName, e.Opportunity.Client.AvatarUpdatedAtUtc,
        e.FreelancerId, e.Freelancer!.DisplayName, e.Freelancer.AvatarUpdatedAtUtc,
        db.ChatMessages.Count(m => m.EntryId == e.Id),
        db.ChatMessages.Where(m => m.EntryId == e.Id).OrderByDescending(m => m.CreatedAtUtc).ThenByDescending(m => m.Id).Select(m => (DateTimeOffset?)m.CreatedAtUtc).FirstOrDefault(),
        db.ChatMessages.Where(m => m.EntryId == e.Id).OrderByDescending(m => m.CreatedAtUtc).ThenByDescending(m => m.Id).Select(m => (Guid?)m.SenderId).FirstOrDefault(),
        db.ChatReports.Count(r => r.EntryId == e.Id && r.ResolvedAtUtc == null));

    internal static Task<List<ReportRow>> ReportsLinqAsync(AppDbContext db, Guid entryId, CancellationToken ct) =>
        db.ChatReports.AsNoTracking()
            .Where(r => r.EntryId == entryId)
            .OrderBy(r => r.ResolvedAtUtc == null ? 0 : 1)
            .ThenByDescending(r => r.CreatedAtUtc).ThenByDescending(r => r.Id)
            .Select(r => new ReportRow(r.Id, r.ReporterId, r.Reporter!.DisplayName, r.Reason, r.Details, r.CreatedAtUtc,
                r.ResolvedAtUtc, r.ResolvedBy == null ? null : r.ResolvedBy.DisplayName, r.Resolution))
            .ToListAsync(ct);

    internal static async Task<OpenReportsRow> OpenReportsLinqAsync(AppDbContext db, CancellationToken ct)
    {
        var open = db.ChatReports.AsNoTracking().Where(r => r.ResolvedAtUtc == null);
        var conversations = await open.Select(r => r.EntryId).Distinct().CountAsync(ct);
        var reports = await open.CountAsync(ct);
        var oldest = await open.MinAsync(r => (DateTimeOffset?)r.CreatedAtUtc, ct);
        return new OpenReportsRow(conversations, reports, oldest);
    }
}
