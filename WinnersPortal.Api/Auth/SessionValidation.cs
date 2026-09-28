using Microsoft.AspNetCore.Authentication.JwtBearer;
using WinnersPortal.Services.Auth;

namespace WinnersPortal.Api.Auth;

/// <summary>
/// The bearer handler's two hooks. Where the token comes from: the
/// Authorization header when one was sent, else the sign-in cookie, else —
/// for the live hub only, whose socket cannot set a header — the
/// <c>access_token</c> query string SignalR uses. And what a valid
/// signature is not yet proof of: the token is a week-long ticket, so
/// without a look at the row a locked account would stay signed in for as
/// long as it kept clicking; with it, a lock, an erasure, or a new session
/// stamp takes effect on the very next request. One primary-key read per
/// authenticated request is the price.
/// </summary>
public static class SessionValidation
{
    /// <summary>Set on the request when its token came from the cookie rather than a header.</summary>
    public const string ViaCookieItem = "wp:viaCookie";

    public const string HubPath = "/api/live";

    public static Task OnMessageReceived(MessageReceivedContext ctx)
    {
        var http = ctx.HttpContext;
        if (http.Request.Headers.ContainsKey("Authorization")) return Task.CompletedTask;

        if (SessionCookie.Read(http) is { } cookie)
        {
            ctx.Token = cookie;
            http.Items[ViaCookieItem] = true;
        }
        else if (http.Request.Path.StartsWithSegments(HubPath)
                 && http.Request.Query.TryGetValue("access_token", out var query)
                 && !string.IsNullOrEmpty(query))
        {
            ctx.Token = query;
        }
        return Task.CompletedTask;
    }

    public static async Task OnTokenValidated(TokenValidatedContext ctx)
    {
        var http = ctx.HttpContext;
        var principal = ctx.Principal!;
        var gates = http.RequestServices.GetRequiredService<AccountGates>();
        var user = await gates.SessionAccountAsync(principal, http.RequestAborted);
        if (user is null)
        {
            Reject(ctx);
            return;
        }

        // The cookie slides: past the halfway point of its week, the
        // browser gets a fresh one on the way out, on the same
        // keep-me-signed-in choice. A bearer token is never renewed here —
        // its holder exchanges the refresh token instead.
        if (ViaCookie(http)
            && TokenService.ExpiresAt(principal) is { } expires
            && expires - DateTimeOffset.UtcNow < TokenService.SessionLifetime / 2)
        {
            var tokens = http.RequestServices.GetRequiredService<TokenService>();
            SessionCookie.Issue(http, tokens, user, Principal.Persistent(principal));
        }
    }

    /// <summary>
    /// A cookie that does not even parse — a ticket from before tokens, or
    /// one signed with a key this deployment no longer has — is cleared,
    /// so the browser stops sending it. A bad bearer header is the client's
    /// own to fix.
    /// </summary>
    public static Task OnAuthenticationFailed(AuthenticationFailedContext ctx)
    {
        if (ViaCookie(ctx.HttpContext)) SessionCookie.Clear(ctx.HttpContext);
        return Task.CompletedTask;
    }

    public static bool ViaCookie(HttpContext http) => http.Items.ContainsKey(ViaCookieItem);

    private static void Reject(TokenValidatedContext ctx)
    {
        ctx.Fail("The session is no longer valid.");
        // Clear the cookie too, so the browser stops presenting a ticket
        // that will be refused every time.
        if (ViaCookie(ctx.HttpContext)) SessionCookie.Clear(ctx.HttpContext);
    }
}
