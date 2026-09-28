using Microsoft.AspNetCore.Routing.Patterns;
using WinnersPortal.Api.Activity;
using WinnersPortal.Domain;
using WinnersPortal.Services.Activity;
using WinnersPortal.Services.Auth;

namespace WinnersPortal.Api.Middleware;

/// <summary>
/// After the response: a request under <c>/api</c> that did something
/// becomes a row in the activity log. Registered after authentication (so
/// a row knows who) and ahead of the gates, so a request the maintenance,
/// confirmation or terms gate refused is logged with the status it got —
/// those refusals are activity too. The row is dropped on a channel
/// (<see cref="ActivityLog"/>); nobody waits on the write.
/// </summary>
public sealed class ActivityLogMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        await next(ctx);
        Capture(ctx);
    }

    /// <summary>What a completed request leaves behind; exposed for the tests, which hand it a finished context.</summary>
    public static void Capture(HttpContext ctx)
    {
        var path = ctx.Request.Path;
        if (!path.StartsWithSegments("/api")) return;
        // An unknown API path matched nothing; there is no request to name.
        if (ctx.GetEndpoint() is not RouteEndpoint endpoint) return;

        var pattern = ActivityNames.Normalize(PatternText(endpoint.RoutePattern));
        var method = ctx.Request.Method;
        if (!ActivityNames.Worth(method, pattern)) return;

        // What the service serving the request wrote down about it. Sign-in,
        // registration and a reset link know the person before the cookie
        // does; everything else reads the principal.
        var note = ctx.RequestServices.GetService<ActivityNote>();
        var userId = note?.UserId ?? Principal.UserId(ctx.User);

        ctx.RequestServices.GetRequiredService<ActivityLog>().Record(new ActivityEvent
        {
            UserId = userId,
            Visitor = Visitors.VisitorOf(ctx.Request),
            Kind = ActivityKinds.Action,
            Method = method.ToUpperInvariant(),
            Path = ActivityNames.Trim(path.Value, ActivityNames.MaxPath) ?? "/",
            Action = ActivityNames.Trim(note?.Action, ActivityNames.MaxSubject)
                ?? ActivityNames.Describe(method, pattern),
            Page = ActivityNames.PageOf(ctx.Request.Headers.Referer),
            Subject = ActivityNames.Trim(note?.Subject, ActivityNames.MaxSubject)
                ?? ActivityNames.Subject(ctx.Request.RouteValues),
            Detail = ActivityNames.Detail(note?.Detail),
            Status = ctx.Response.StatusCode,
            Ip = ctx.Connection.RemoteIpAddress?.ToString(),
            UserAgent = ActivityNames.Trim(ctx.Request.Headers.UserAgent, ActivityNames.MaxAgent),
            AtUtc = DateTimeOffset.UtcNow,
        });
    }

    /// <summary>
    /// The pattern as text. A controller action's pattern carries its raw
    /// template ("api/admin/users/{id:guid}"); one built from segments does
    /// not always carry text, so it is rebuilt from the segments when it
    /// does not. ActivityNames.Normalize adds the leading slash.
    /// </summary>
    public static string PatternText(RoutePattern pattern)
    {
        if (!string.IsNullOrEmpty(pattern.RawText)) return pattern.RawText;
        var segments = pattern.PathSegments.Select(s => string.Concat(s.Parts.Select(p => p switch
        {
            RoutePatternLiteralPart literal => literal.Content,
            RoutePatternParameterPart parameter => "{" + parameter.Name + "}",
            RoutePatternSeparatorPart separator => separator.Content,
            _ => "",
        })));
        return "/" + string.Join("/", segments);
    }
}
