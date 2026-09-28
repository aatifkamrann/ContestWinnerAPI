using WinnersPortal.Services.Auth;
using WinnersPortal.Services.Common;
using WinnersPortal.Services.Settings;

namespace WinnersPortal.Api.Middleware;

/// <summary>
/// While an acceptance of the terms is owed the account is read-only: every
/// writing API call but the session's own answers 403. The rule is
/// <see cref="Terms.GateAllows"/>; this is its place in the pipeline, after
/// authentication, since an owed acceptance belongs to a signed-in account.
/// </summary>
public sealed class TermsGateMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext ctx, AccountGates gates)
    {
        // Only a signed-in writer can owe anything, so the settings read
        // (cached) and the one-column row read happen only for those.
        if (ctx.User.Identity?.IsAuthenticated == true
            && !Terms.GateAllows(ctx.Request.Path, ctx.Request.Method)
            && Principal.UserId(ctx.User) is { } userId
            && await gates.TermsOwedAsync(userId, ctx.RequestAborted))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsJsonAsync(new TermsPendingResponse
            {
                Error = "Accept the current terms of service to continue.",
                TermsPending = true,
            }, ctx.RequestAborted);
            return;
        }
        await next(ctx);
    }
}

/// <summary>The 403 while an acceptance of the terms is owed: the sentence, and the flag the web tier routes on.</summary>
public sealed record TermsPendingResponse : IErrorResponse
{
    public required string Error { get; init; }
    public required bool TermsPending { get; init; }
}
