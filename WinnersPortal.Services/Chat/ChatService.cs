using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using WinnersPortal.Domain;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Services.Auth;
using WinnersPortal.Services.Common;
using WinnersPortal.Services.Email;
using WinnersPortal.Services.Live;
using WinnersPortal.Services.Opportunities;
using WinnersPortal.Services.Profiles;

namespace WinnersPortal.Services.Chat;

/// <summary>
/// The conversations between a client and the entrants of their
/// opportunities: the list of them from one member's seat, one of them a
/// page at a time, a line added, and the other side's lines marked read.
/// The conversation is the entry (<see cref="ChatMessage"/>), so every
/// door here is the same question — is the caller the entrant or the
/// client of this entry — and a caller who is neither is told there is
/// nothing there. Each write rings both members' rooms (<see cref="ILiveChat"/>);
/// the dock and the Messages page refetch. Either party may report the
/// conversation, which tells every administrator and nobody else.
/// </summary>
public sealed partial class ChatService(AppDbContext db, ILiveChat live, EmailWorkSignal emailSignal)
{
    /// <summary>
    /// Every conversation the caller is party to, newest first: the active
    /// entries — a conversation exists before its first line — and the
    /// closed ones with something said in them. On SQL Server, Chat_Threads.
    /// </summary>
    public async Task<Outcome<ChatThreadsResponse>> ThreadsAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        var me = Principal.UserId(principal)!.Value;
        var rows = db.UseDapper
            ? await ThreadsSqlAsync(db.Sql, me, ChatRules.MaxThreads, ct)
            : await ThreadsLinqAsync(db, me, ChatRules.MaxThreads, ct);
        return Outcome.Ok(new ChatThreadsResponse
        {
            Items = rows.Select(r => View(r, me)).ToList(),
            Unread = rows.Sum(r => r.Unread),
        });
    }

    /// <summary>
    /// One page of a conversation, newest first, from the cursor the page
    /// before handed back, with the conversation itself. On SQL Server,
    /// Chat_Thread then Chat_Page.
    /// </summary>
    public async Task<Outcome<ChatThreadResponse>> ThreadAsync(Guid entryId, string? cursor, int? take, ClaimsPrincipal principal, CancellationToken ct)
    {
        var me = Principal.UserId(principal)!.Value;
        var thread = await GateAsync(entryId, me, ct);
        if (thread is null) return Outcome.NotFound();

        var size = Math.Clamp(take ?? ChatRules.PageSize, 1, ChatRules.MaxPageSize);
        var before = Cursor.Decode(cursor);
        // One more than the page, to know whether there is another.
        var page = db.UseDapper
            ? await PageSqlAsync(db.Sql, entryId, before, size + 1, ct)
            : await PageLinqAsync(db, entryId, before, size + 1, ct);
        var more = page.Count > size;
        var rows = more ? page[..size] : page;
        return Outcome.Ok(new ChatThreadResponse
        {
            Thread = View(thread, me),
            Items = rows.Select(m => View(m, me)).ToList(),
            NextCursor = more ? new Cursor(rows[^1].CreatedAtUtc, rows[^1].Id).Encode() : null,
        });
    }

    /// <summary>
    /// A line added, where the conversation is still open. Both rooms are
    /// rung: the other side's for the line, the sender's own for their
    /// other tabs.
    /// </summary>
    public async Task<Outcome<ChatMessageView>> SendAsync(Guid entryId, SendMessageRequest request, ClaimsPrincipal principal, CancellationToken ct)
    {
        var me = Principal.UserId(principal)!.Value;
        var thread = await GateAsync(entryId, me, ct);
        if (thread is null) return Outcome.NotFound();
        if (!ChatRules.CanSend(thread.EntryStatus, thread.OpportunityStatus)) return Outcome.Conflict(ChatRules.Closed);
        var (body, error) = ChatRules.Check(request.Body);
        if (error is not null) return Outcome.Invalid(error);

        var message = new ChatMessage
        {
            Id = Guid.NewGuid(),
            EntryId = entryId,
            SenderId = me,
            Body = body!,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        db.ChatMessages.Add(message);
        await db.SaveChangesAsync(ct);

        await live.ChatChangedAsync(thread.OtherId, entryId, ct);
        await live.ChatChangedAsync(me, entryId, ct);
        return Outcome.Ok(View(new MessageRow(message.Id, message.SenderId, message.Body, message.CreatedAtUtc, null), me));
    }

    /// <summary>
    /// The other side's lines in a conversation marked read, as of now.
    /// The caller's own lines are never touched — their read stamp is the
    /// other side's to set. On SQL Server, Chat_MarkRead.
    /// </summary>
    public async Task<Outcome<ChatReadResponse>> ReadAsync(Guid entryId, ClaimsPrincipal principal, CancellationToken ct)
    {
        var me = Principal.UserId(principal)!.Value;
        var thread = await GateAsync(entryId, me, ct);
        if (thread is null) return Outcome.NotFound();

        var readAt = DateTimeOffset.UtcNow;
        var changed = db.UseDapper
            ? await db.Sql.ExecuteAsync(Procedures.ChatMarkRead, new { entryId, userId = me, readAt }, ct)
            : await db.ChatMessages
                .Where(m => m.EntryId == entryId && m.SenderId != me && m.ReadAtUtc == null)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.ReadAtUtc, readAt), ct);
        if (changed > 0)
        {
            // The sender sees their line read; the reader's other tabs drop the badge.
            await live.ChatChangedAsync(thread.OtherId, entryId, ct);
            await live.ChatChangedAsync(me, entryId, ct);
        }
        return Outcome.Ok(new ChatReadResponse { Changed = changed });
    }

    /// <summary>
    /// The conversation reported by one of its two parties: why, and in
    /// their words what. Every administrator is emailed with a line in the
    /// bell; the other side is not told. One open report per reporter per
    /// conversation, and only once something has been said.
    /// </summary>
    public async Task<Outcome<ChatReportResponse>> ReportAsync(Guid entryId, ReportConversationRequest request, ClaimsPrincipal principal, CancellationToken ct)
    {
        var me = Principal.UserId(principal)!.Value;
        var thread = await GateAsync(entryId, me, ct);
        if (thread is null) return Outcome.NotFound();
        if (ChatRules.ReportProblem(thread.LastAtUtc is not null, thread.MyReportAtUtc is not null) is { } problem)
            return Outcome.Conflict(problem);
        var (reason, details, error) = ChatRules.CheckReport(request.Reason, request.Details);
        if (error is not null) return Outcome.Invalid(error);

        var now = DateTimeOffset.UtcNow;
        db.ChatReports.Add(new ChatReport
        {
            Id = Guid.NewGuid(),
            EntryId = entryId,
            ReporterId = me,
            Reason = reason!,
            Details = details,
            CreatedAtUtc = now,
        });

        // The email is the report: who, about whom, why, in their words.
        var reporterName = await db.Users.AsNoTracking().Where(u => u.Id == me).Select(u => u.DisplayName).SingleAsync(ct);
        var email = Emails.ChatReported(entryId, thread.Title, reporterName,
            me == thread.FreelancerId ? "entrant" : "client", thread.OtherName, ChatRules.ReasonLabel(reason!), details);
        foreach (var admin in await db.Users.AsNoTracking()
                     .Where(u => u.Role == Roles.Admin && u.ErasedAtUtc == null).ToListAsync(ct))
            Notify.Queue(db, admin, "chat_reported", email);
        await db.SaveChangesAsync(ct);
        emailSignal.Wake();

        // The reporter's other tabs show it reported; the other side hears nothing.
        await live.ChatChangedAsync(me, entryId, ct);
        return Outcome.Ok(new ChatReportResponse { ReportedAtUtc = now });
    }

    // ------------------------------------------------------------- rows

    /// <summary>
    /// One conversation from one member's seat, or null where the entry is
    /// not theirs to read — the one door every call here goes through.
    /// On SQL Server, Chat_Thread.
    /// </summary>
    private async Task<ThreadRow?> GateAsync(Guid entryId, Guid me, CancellationToken ct) =>
        db.UseDapper
            ? await ThreadSqlAsync(db.Sql, entryId, me, ct)
            : await ThreadLinqAsync(db, entryId, me, ct);

    internal sealed record ThreadRow(
        Guid EntryId, EntryStatus EntryStatus, Guid FreelancerId, Guid ClientId,
        string Slug, string Title, OpportunityStatus OpportunityStatus,
        Guid OtherId, string OtherName, DateTimeOffset? OtherAvatarUpdatedAtUtc,
        string? LastBody, DateTimeOffset? LastAtUtc, Guid? LastSenderId, int Unread,
        DateTimeOffset? MyReportAtUtc);

    internal sealed record MessageRow(Guid Id, Guid SenderId, string Body, DateTimeOffset CreatedAtUtc, DateTimeOffset? ReadAtUtc);

    internal static ChatThreadView View(ThreadRow r, Guid me) => new()
    {
        EntryId = r.EntryId,
        Opportunity = new ChatOpportunityView
        {
            Slug = r.Slug,
            Title = r.Title,
            Status = OpportunityNames.StatusName(r.OpportunityStatus),
        },
        Other = new ChatPartyView
        {
            Id = r.OtherId,
            DisplayName = r.OtherName,
            AvatarUrl = AvatarRules.Url(r.OtherId, r.OtherAvatarUpdatedAtUtc),
        },
        Seat = me == r.FreelancerId ? Roles.Freelancer : Roles.Client,
        EntryStatus = EntryNames.StatusName(r.EntryStatus),
        CanSend = ChatRules.CanSend(r.EntryStatus, r.OpportunityStatus),
        Last = r.LastBody is null || r.LastAtUtc is null
            ? null
            : new ChatLastLine { Excerpt = ChatRules.Excerpt(r.LastBody), AtUtc = r.LastAtUtc.Value, Mine = r.LastSenderId == me },
        Unread = r.Unread,
        ReportedAtUtc = r.MyReportAtUtc,
    };

    internal static ChatMessageView View(MessageRow m, Guid me) => new()
    {
        Id = m.Id,
        Body = m.Body,
        SenderId = m.SenderId,
        Mine = m.SenderId == me,
        CreatedAtUtc = m.CreatedAtUtc,
        ReadAtUtc = m.ReadAtUtc,
    };

    // ------------------------------------------------------------- LINQ
    // The Postgres path, and the diagnostic run on SQL Server. Each is
    // one statement, the shape of the procedure beside it.

    internal static Task<List<ThreadRow>> ThreadsLinqAsync(AppDbContext db, Guid me, int take, CancellationToken ct) =>
        ThreadsQuery(db, me)
            .Where(e => e.Status == EntryStatus.Active || db.ChatMessages.Any(m => m.EntryId == e.Id))
            // The last line's time, or the entry's own where nothing was said yet.
            .OrderByDescending(e => db.ChatMessages.Where(m => m.EntryId == e.Id).Max(m => (DateTimeOffset?)m.CreatedAtUtc) ?? e.CreatedAtUtc)
            .ThenByDescending(e => e.Id)
            .Take(take)
            .Select(Row(db, me))
            .ToListAsync(ct);

    internal static Task<ThreadRow?> ThreadLinqAsync(AppDbContext db, Guid entryId, Guid me, CancellationToken ct) =>
        ThreadsQuery(db, me)
            .Where(e => e.Id == entryId)
            .Select(Row(db, me))
            .SingleOrDefaultAsync(ct);

    private static IQueryable<Entry> ThreadsQuery(AppDbContext db, Guid me) =>
        db.Entries.AsNoTracking()
            .Where(e => e.FreelancerId == me || e.Opportunity!.ClientId == me);

    // An expression, so EF translates it inline where it is selected. The
    // other side is picked per row, so the same shape serves both seats.
    private static System.Linq.Expressions.Expression<Func<Entry, ThreadRow>> Row(AppDbContext db, Guid me) => e => new ThreadRow(
        e.Id, e.Status, e.FreelancerId, e.Opportunity!.ClientId,
        e.Opportunity.Slug, e.Opportunity.Title, e.Opportunity.Status,
        e.FreelancerId == me ? e.Opportunity.ClientId : e.FreelancerId,
        e.FreelancerId == me ? e.Opportunity.Client!.DisplayName : e.Freelancer!.DisplayName,
        e.FreelancerId == me ? e.Opportunity.Client!.AvatarUpdatedAtUtc : e.Freelancer!.AvatarUpdatedAtUtc,
        db.ChatMessages.Where(m => m.EntryId == e.Id).OrderByDescending(m => m.CreatedAtUtc).ThenByDescending(m => m.Id).Select(m => m.Body).FirstOrDefault(),
        db.ChatMessages.Where(m => m.EntryId == e.Id).OrderByDescending(m => m.CreatedAtUtc).ThenByDescending(m => m.Id).Select(m => (DateTimeOffset?)m.CreatedAtUtc).FirstOrDefault(),
        db.ChatMessages.Where(m => m.EntryId == e.Id).OrderByDescending(m => m.CreatedAtUtc).ThenByDescending(m => m.Id).Select(m => (Guid?)m.SenderId).FirstOrDefault(),
        db.ChatMessages.Count(m => m.EntryId == e.Id && m.SenderId != me && m.ReadAtUtc == null),
        db.ChatReports.Where(r => r.EntryId == e.Id && r.ReporterId == me && r.ResolvedAtUtc == null).Max(r => (DateTimeOffset?)r.CreatedAtUtc));

    internal static async Task<List<MessageRow>> PageLinqAsync(AppDbContext db, Guid entryId, Cursor? before, int take, CancellationToken ct)
    {
        var lines = db.ChatMessages.AsNoTracking().Where(m => m.EntryId == entryId);
        if (before is { } c)
            lines = lines.Where(m => m.CreatedAtUtc < c.At || (m.CreatedAtUtc == c.At && m.Id.CompareTo(c.Id) < 0));
        return await lines
            .OrderByDescending(m => m.CreatedAtUtc).ThenByDescending(m => m.Id)
            .Take(take)
            .Select(m => new MessageRow(m.Id, m.SenderId, m.Body, m.CreatedAtUtc, m.ReadAtUtc))
            .ToListAsync(ct);
    }
}
