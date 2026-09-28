using WinnersPortal.Services.Common;
using WinnersPortal.Services.Settings;
using WinnersPortal.Domain;

namespace WinnersPortal.Api.Middleware;

/// <summary>
/// limits.maintenanceMode at the door: 503 for everyone but administrators
/// and the paths that must survive an outage. The rule is
/// <see cref="MaintenanceGate.Allows"/>; this is only its place in the
/// pipeline, after authentication so the admin exemption can see the role.
/// </summary>
public sealed class MaintenanceGateMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext ctx, SettingsService settings, AppPause pause)
    {
        // The pause first: a database move has every write wait for the
        // restart, administrators included, and it is a flag in memory —
        // never a setting, which the copy would carry to the new database.
        if (pause.IsPaused && !MaintenanceGate.AllowsWhilePaused(ctx.Request.Path, ctx.Request.Method))
        {
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            ctx.Response.Headers.RetryAfter = "60";
            await ctx.Response.WriteAsJsonAsync(new MaintenanceResponse
            {
                Error = pause.Reason ?? "The portal is moving to another database and will restart in a moment.",
                Maintenance = true,
            }, ctx.RequestAborted);
            return;
        }

        // Cheap by construction: SettingsService serves this from its
        // in-memory cache, invalidated over Redis when the switch flips.
        if (!MaintenanceGate.Allows(ctx.Request.Path, ctx.User.IsInRole(Roles.Admin)))
        {
            if (string.Equals(await settings.GetAsync("limits.maintenanceMode", ctx.RequestAborted),
                    "true", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                ctx.Response.Headers.RetryAfter = "300";
                await ctx.Response.WriteAsJsonAsync(new MaintenanceResponse
                {
                    Error = "The portal is down for maintenance and will be back shortly. "
                        + "Administrators can still sign in.",
                    Maintenance = true,
                }, ctx.RequestAborted);
                return;
            }
        }
        await next(ctx);
    }
}

/// <summary>The 503 while maintenance mode is on: the sentence, and the flag the web tier routes on.</summary>
public sealed record MaintenanceResponse : IErrorResponse
{
    public required string Error { get; init; }
    public required bool Maintenance { get; init; }
}
