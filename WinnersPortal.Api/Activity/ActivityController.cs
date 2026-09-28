using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WinnersPortal.Api.Common;
using WinnersPortal.Domain;
using WinnersPortal.Services.Activity;
using WinnersPortal.Services.Auth;
using WinnersPortal.Services.Common;

namespace WinnersPortal.Api.Activity;

/// <summary>The HTTP edge of <see cref="ActivityService"/>: binds the request, answers with its outcome.</summary>
[ApiController]
public sealed class ActivityController(ActivityService activity) : ControllerBase
{
    // ---------------------------------------------------------- endpoints

    // The browser, on every navigation: which page opened. Anonymous
    // on purpose — a visitor's browsing is the half of the log that
    // says what the public pages are doing. Never anything but a path.
    [HttpPost("api/activity/visit")]
    public IResult PostActivityVisit(VisitRequest request, [FromServices] ActivityLog log)
    {
        var page = ActivityNames.CleanPage(request.Page);
        if (page is null) return Results.BadRequest(new ErrorResponse("A page path on this portal is required."));

        var visitor = Visitors.VisitorOf(HttpContext.Request) ?? Visitors.IssueVisitor(HttpContext);
        log.Record(new ActivityEvent
        {
            UserId = Principal.UserId(HttpContext.User),
            Visitor = visitor,
            Kind = ActivityKinds.Visit,
            Method = "GET",
            Path = page,
            Action = ActivityNames.PageName(page),
            Page = page,
            Subject = null,
            Status = 0,
            Ip = HttpContext.Connection.RemoteIpAddress?.ToString(),
            UserAgent = ActivityNames.Trim(HttpContext.Request.Headers.UserAgent, ActivityNames.MaxAgent),
            AtUtc = DateTimeOffset.UtcNow,
        });
        return Results.NoContent();
    }

    // The administrator's read: newest first, a page at a time, narrowed
    // by who, what kind, when, and a word. The account is joined at read
    // time rather than copied into the row, so a renamed member reads
    // by their current name and an erased one as a deleted member. AI
    // calls narrow further by provider (its settings key) or by one exact
    // provider/model, which the row names in its Subject.
    [HttpGet("api/admin/activity")]
    [Authorize(Policy = "admin")]
    public async Task<IResult> GetAdminActivity(Guid? user, string? visitor, string? kind, string? service, string? who, string? q, DateTimeOffset? from, DateTimeOffset? to, int? page, int? size, string? aiProvider, string? aiModel, CancellationToken ct) =>
        (await activity.ListAsync(user, visitor, kind, service, who, q, from, to, page, size, aiProvider, aiModel, ct)).ToResult();

    // The providers the portal can call and every provider and model the
    // log's AI calls were made with — what the two AI filters offer.
    [HttpGet("api/admin/activity/ai-models")]
    [Authorize(Policy = "admin")]
    public async Task<IResult> GetAdminActivityAiModels(CancellationToken ct) =>
        (await activity.AiModelsAsync(ct)).ToResult();

    // One third-party call's request and response, fetched when its row
    // is opened. Secrets were masked before the row was written.
    [HttpGet("api/admin/activity/{id:long}/exchange")]
    [Authorize(Policy = "admin")]
    public async Task<IResult> GetAdminActivityExchange(long id, CancellationToken ct) =>
        (await activity.ExchangeAsync(id, ct)).ToResult();

    public sealed record VisitRequest(string? Page);
}
