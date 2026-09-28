using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using WinnersPortal.Domain;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Services.Ai;
using WinnersPortal.Services.Common;
using WinnersPortal.Services.Opportunities;
using WinnersPortal.Services.Profiles;

namespace WinnersPortal.Services.Activity;

/// <summary>
/// The activity log's two doors on the API: the visit endpoint the browser
/// calls as it navigates (signed in or not), and the administrator's read
/// of all of it. Only the read is gated; the log is of everybody. The third
/// door, the middleware that records every request worth a row as it
/// completes, is ActivityLogMiddleware; the fourth, the handler on every
/// outgoing HTTP client that records each call to a third-party API, is
/// ExternalCallRecorder.
/// </summary>
public sealed partial class ActivityService(AppDbContext db)
{
    public const int PageSizeMax = 200;
    public const int PageSizeDefault = 50;

    public async Task<Outcome<ActivityLogResponse>> ListAsync(Guid? user, string? visitor, string? kind, string? service, string? who, string? q, DateTimeOffset? from, DateTimeOffset? to, int? page, int? size, string? aiProvider, string? aiModel, CancellationToken ct)
    {
        var filter = new ActivityFilter(user, visitor, kind, who, q, from, to, service, aiProvider, aiModel);
        var pageSize = Math.Clamp(size ?? PageSizeDefault, 1, PageSizeMax);
        var pageIndex = Math.Max(page ?? 1, 1);
        var (total, rows) = db.UseDapper
            ? await PageSqlAsync(db.Sql, filter, pageIndex, pageSize, ct)
            : (await Filter(db, filter).CountAsync(ct), await PageQuery(db, filter, pageIndex, pageSize).ToListAsync(ct));

        return Outcome.Ok(new ActivityLogResponse
        {
            Total = total,
            Page = pageIndex,
            Size = pageSize,
            Rows = rows.Select(r => new ActivityLogRow
            {
                Id = r.Id,
                AtUtc = r.AtUtc,
                Kind = r.Kind,
                Action = r.Action,
                Method = r.Method,
                Path = r.Path,
                Page = r.Page,
                Subject = r.Subject,
                Detail = r.Detail,
                Status = r.Status,
                Ip = r.Ip,
                UserAgent = r.UserAgent,
                Visitor = r.Visitor,
                UserId = r.UserId,
                Service = r.Service,
                DurationMs = r.DurationMs,
                User = r.UserName is null ? null : new ActivityLogUser
                {
                    Id = r.UserId,
                    DisplayName = r.UserName,
                    Email = r.UserEmail,
                    Role = r.UserRole,
                    Erased = r.UserErasedAtUtc != null,
                    AvatarUrl = AvatarRules.Url(r.UserId!.Value, r.UserAvatarUpdatedAtUtc),
                },
            }),
        });
    }

    /// <summary>
    /// What one third-party call sent and got back — read on its own, when
    /// the row is opened, because a page of fifty bodies is a lot to fetch
    /// for rows nobody opens. Any other kind of row has no exchange to show.
    /// </summary>
    public async Task<Outcome<ActivityExchangeResponse>> ExchangeAsync(long id, CancellationToken ct)
    {
        var row = db.UseDapper
            ? await ExchangeSqlAsync(db.Sql, id, ct)
            : await db.ActivityEvents.AsNoTracking()
                .Where(e => e.Id == id && e.Kind == ActivityKinds.External)
                .Select(e => new ExchangeRow(e.Id, e.Request, e.Response))
                .SingleOrDefaultAsync(ct);
        if (row is null) return Outcome.NotFound();
        return Outcome.Ok(new ActivityExchangeResponse { Id = row.Id, Request = row.Request, Response = row.Response });
    }

    internal sealed record ExchangeRow(long Id, string? Request, string? Response);

    /// <summary>The AI provider filter: a provider key the portal knows, else not applied.</summary>
    public static string? AiProviderFilter(string? provider) => AiProviders.Find(provider)?.Key;

    /// <summary>The AI model filter: a whole "provider/model" call name, else not applied.</summary>
    public static string? AiModelFilter(string? model) =>
        AiProviders.ParseCallName(model?.Trim()) is { } call && model!.Trim().Length <= ActivityEvent.MaxSubject
            ? AiProviders.CallName(call.Provider, call.Model)
            : null;

    /// <summary>
    /// Every provider and model an AI call on the log was made with, most
    /// recently used first, with how many calls each has — what the
    /// activity screen's Provider and Model filters offer. A call recorded
    /// before rows carried their model is not among them.
    /// </summary>
    public async Task<Outcome<ActivityAiModelsResponse>> AiModelsAsync(CancellationToken ct)
    {
        var rows = db.UseDapper
            ? await AiModelsSqlAsync(db.Sql, ct)
            : await AiModelsQuery(db).ToListAsync(ct);
        return Outcome.Ok(new ActivityAiModelsResponse
        {
            Providers = AiProviders.All.Select(p => new ActivityAiProvider { Key = p.Key, Label = p.Label }),
            Models = rows
                .Select(r => (Row: r, Call: AiProviders.ParseCallName(r.Subject)))
                .Where(x => x.Call is not null)
                .OrderByDescending(x => x.Row.LastAtUtc)
                .Select(x => new ActivityAiModel
                {
                    Id = x.Row.Subject,
                    Provider = x.Call!.Value.Provider,
                    ProviderLabel = AiProviders.Find(x.Call.Value.Provider)?.Label ?? x.Call.Value.Provider,
                    Model = x.Call.Value.Model,
                    Calls = x.Row.Calls,
                    LastAtUtc = x.Row.LastAtUtc,
                })
                .ToList(),
        });
    }

    public sealed record AiModelRow(string Subject, int Calls, DateTimeOffset LastAtUtc);

    /// <summary>The LINQ twin of <c>Activity_AiModels</c>: AI calls grouped by the provider/model their row names.</summary>
    public static IQueryable<AiModelRow> AiModelsQuery(AppDbContext db) =>
        db.ActivityEvents.AsNoTracking()
            .Where(e => e.Service == ExternalServices.Ai && e.Subject != null)
            .GroupBy(e => e.Subject!)
            .Select(g => new AiModelRow(g.Key, g.Count(), g.Max(e => e.AtUtc)));

    /// <summary>The kinds a filter may ask for; anything else is "every kind".</summary>
    public static bool KnownKind(string? kind) =>
        kind is ActivityKinds.Visit or ActivityKinds.Action or ActivityKinds.External;

    // -------------------------------------------------------------- query
    // Static and typed so a test can ask EF for the SQL without a database:
    // the ILIKE over a left-joined, nullable account is the part of this
    // most likely to stop translating when a column moves.

    public sealed record ActivityFilter(
        Guid? User, string? Visitor, string? Kind, string? Who, string? Q, DateTimeOffset? From, DateTimeOffset? To,
        string? Service = null, string? AiProvider = null, string? AiModel = null);

    /// <summary>
    /// A class with an initializer rather than a record: EF Core reads
    /// member access through an object initializer, and not through a
    /// constructor, so <c>x.Event.UserId</c> only translates this way.
    /// </summary>
    public sealed class ActivityJoin
    {
        public required ActivityEvent Event { get; init; }
        public User? User { get; init; }
    }

    public sealed record ActivityRowData(
        long Id, DateTimeOffset AtUtc, string Kind, string Action, string Method, string Path, string? Page,
        string? Subject, string? Detail, int Status, string? Ip, string? UserAgent, string? Visitor, Guid? UserId,
        string? UserName, string? UserEmail, string? UserRole, DateTimeOffset? UserErasedAtUtc,
        DateTimeOffset? UserAvatarUpdatedAtUtc, string? Service, int? DurationMs);

    /// <summary>Every row the filter admits, joined to its account where one still exists.</summary>
    public static IQueryable<ActivityJoin> Filter(AppDbContext db, ActivityFilter f)
    {
        var rows = from e in db.ActivityEvents.AsNoTracking()
                   join u in db.Users.AsNoTracking() on e.UserId equals u.Id into uu
                   from u in uu.DefaultIfEmpty()
                   select new ActivityJoin { Event = e, User = u };

        if (f.User is { } userId) rows = rows.Where(x => x.Event.UserId == userId);
        if (ActivityNames.IsVisitorId(f.Visitor)) rows = rows.Where(x => x.Event.Visitor == f.Visitor);
        if (KnownKind(f.Kind)) rows = rows.Where(x => x.Event.Kind == f.Kind);
        if (ExternalServices.IsKnown(f.Service)) rows = rows.Where(x => x.Event.Service == f.Service);
        // An AI call's row names its provider and model, "openai/gpt-5.5"
        // (AiProviders.CallName); the two filters read that and nothing else.
        if (AiProviderFilter(f.AiProvider) is { } provider)
        {
            var prefix = provider + "/";
            rows = rows.Where(x => x.Event.Service == ExternalServices.Ai
                && x.Event.Subject != null && x.Event.Subject.StartsWith(prefix));
        }
        if (AiModelFilter(f.AiModel) is { } model)
            rows = rows.Where(x => x.Event.Service == ExternalServices.Ai && x.Event.Subject == model);
        switch (f.Who)
        {
            case "members": rows = rows.Where(x => x.Event.UserId != null); break;
            case "visitors": rows = rows.Where(x => x.Event.UserId == null); break;
        }
        if (f.From is { } since) rows = rows.Where(x => x.Event.AtUtc >= since);
        if (f.To is { } until) rows = rows.Where(x => x.Event.AtUtc < until);
        if (!string.IsNullOrWhiteSpace(f.Q))
            rows = rows.Where(TextMatch(Search.TitlePattern(f.Q), db.IsSqlServer));
        return rows;
    }

    /// <summary>
    /// The words typed against every text column, case aside: ILIKE on
    /// Postgres, LIKE on SQL Server, where the default collation already
    /// ignores case. Two copies of one predicate, because the function
    /// name is the only difference and an expression tree cannot switch it.
    /// </summary>
    public static Expression<Func<ActivityJoin, bool>> TextMatch(string like, bool sqlServer) => sqlServer
        ? x =>
            EF.Functions.Like(x.Event.Action, like, Search.Escape)
            || EF.Functions.Like(x.Event.Path, like, Search.Escape)
            || (x.Event.Page != null && EF.Functions.Like(x.Event.Page, like, Search.Escape))
            || (x.Event.Subject != null && EF.Functions.Like(x.Event.Subject, like, Search.Escape))
            || (x.Event.Detail != null && EF.Functions.Like(x.Event.Detail, like, Search.Escape))
            || (x.Event.Ip != null && EF.Functions.Like(x.Event.Ip, like, Search.Escape))
            || (x.Event.Visitor != null && EF.Functions.Like(x.Event.Visitor, like, Search.Escape))
            || (x.User != null && (EF.Functions.Like(x.User.DisplayName, like, Search.Escape)
                || EF.Functions.Like(x.User.Email, like, Search.Escape)))
        : x =>
            EF.Functions.ILike(x.Event.Action, like, Search.Escape)
            || EF.Functions.ILike(x.Event.Path, like, Search.Escape)
            || (x.Event.Page != null && EF.Functions.ILike(x.Event.Page, like, Search.Escape))
            || (x.Event.Subject != null && EF.Functions.ILike(x.Event.Subject, like, Search.Escape))
            || (x.Event.Detail != null && EF.Functions.ILike(x.Event.Detail, like, Search.Escape))
            || (x.Event.Ip != null && EF.Functions.ILike(x.Event.Ip, like, Search.Escape))
            || (x.Event.Visitor != null && EF.Functions.ILike(x.Event.Visitor, like, Search.Escape))
            || (x.User != null && (EF.Functions.ILike(x.User.DisplayName, like, Search.Escape)
                || EF.Functions.ILike(x.User.Email, like, Search.Escape)));

    /// <summary>One page of the filtered rows, newest first, flattened for the wire.</summary>
    public static IQueryable<ActivityRowData> PageQuery(AppDbContext db, ActivityFilter f, int pageIndex, int pageSize) =>
        Filter(db, f)
            .OrderByDescending(x => x.Event.AtUtc).ThenByDescending(x => x.Event.Id)
            .Skip((pageIndex - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new ActivityRowData(
                x.Event.Id, x.Event.AtUtc, x.Event.Kind, x.Event.Action, x.Event.Method, x.Event.Path, x.Event.Page,
                x.Event.Subject, x.Event.Detail, x.Event.Status, x.Event.Ip, x.Event.UserAgent, x.Event.Visitor, x.Event.UserId,
                x.User == null ? null : x.User.DisplayName,
                x.User == null ? null : x.User.Email,
                x.User == null ? null : x.User.Role,
                x.User == null ? null : x.User.ErasedAtUtc,
                x.User == null ? null : x.User.AvatarUpdatedAtUtc,
                x.Event.Service, x.Event.DurationMs));
}
