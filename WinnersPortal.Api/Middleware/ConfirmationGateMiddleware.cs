using WinnersPortal.Services.Auth;
using WinnersPortal.Services.Common;

namespace WinnersPortal.Api.Middleware;

/// <summary>
/// An account that has not yet proved an email or a phone can do nothing
/// but prove one: every call under /api but the session's own answers 403.
/// The rule is <see cref="Confirmation.GateAllows"/>; this is its place in
/// the pipeline, after authentication.
/// </summary>
public sealed class ConfirmationGateMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext ctx, AccountGates gates)
    {
        // Only a signed-in caller can owe this, so the two-column read
        // happens only for those, and only off the allow list.
        if (ctx.User.Identity?.IsAuthenticated == true
            && !Confirmation.GateAllows(ctx.Request.Path)
            && Principal.UserId(ctx.User) is { } userId
            && await gates.ConfirmationOwedAsync(userId, ctx.RequestAborted) is { } message)
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsJsonAsync(new ConfirmationPendingResponse
            {
                Error = message,
                ConfirmationPending = true,
            }, ctx.RequestAborted);
            return;
        }
        await next(ctx);
    }
}

/// <summary>The 403 an unconfirmed account gets: what to prove, and the flag the web tier routes on.</summary>
public sealed record ConfirmationPendingResponse : IErrorResponse
{
    public required string Error { get; init; }
    public required bool ConfirmationPending { get; init; }
}
