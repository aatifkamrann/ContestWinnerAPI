using WinnersPortal.Api.Middleware;
using WinnersPortal.Services.Auth;
using WinnersPortal.Services.Common;
using WinnersPortal.Domain;

namespace WinnersPortal.Api.Auth;

/// <summary>
/// The browser's transport for its token: an HttpOnly cookie, so a script
/// on the page can never read it. Same-origin behind Caddy, so the cookie
/// arrives on every request without the page doing anything; the bearer
/// handler reads it when no Authorization header was sent. When the API
/// is served from a hostname of its own (the Branding settings' API URL),
/// the cookie is scoped to the parent both hostnames share, so the web
/// server sees it on its own requests and renders a signed-in page as such.
/// </summary>
public static class SessionCookie
{
    public const string Name = "wp.auth";

    /// <summary>The domain the cookie carries — the shared parent on the two-site layout, else none (host-only).</summary>
    private static string? Domain(HttpContext http) => Origins.Of(http).CookieDomain;

    /// <summary>
    /// A fresh week-long token in the cookie. Persistent gives the cookie
    /// an expiry the browser honours across a close; otherwise it is a
    /// session cookie that goes when the window does, though the token
    /// inside is good for the week either way.
    /// </summary>
    public static void Issue(HttpContext http, TokenService tokens, User user, bool persistent)
    {
        var now = DateTimeOffset.UtcNow;
        var token = tokens.Issue(Principal.Identity(user, persistent), now, TokenService.SessionLifetime);
        http.Response.Cookies.Append(Name, token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = http.Request.IsHttps,
            Path = "/",
            Domain = Domain(http),
            IsEssential = true,
            Expires = persistent ? now + TokenService.SessionLifetime : null,
        });
    }

    /// <summary>
    /// What a service's outcome says about the browser session, written as
    /// the cookie: a sign-in starts one; a change to the signed-in account's
    /// own claims re-issues the one the browser holds — only a cookie
    /// session, since a bearer client picks the change up at its next
    /// refresh — on the keep-me-signed-in choice it already had.
    /// </summary>
    public static void Apply(HttpContext http, TokenService tokens, Outcome outcome)
    {
        if (outcome.SignIn is { } signIn)
            Issue(http, tokens, signIn.Account, signIn.Persistent);
        else if (outcome.Refreshed is { } account && SessionValidation.ViaCookie(http))
            Issue(http, tokens, account, Principal.Persistent(http.User));
    }

    public static void Apply<T>(HttpContext http, TokenService tokens, Outcome<T> outcome) =>
        Apply(http, tokens, outcome.Untyped);

    public static void Clear(HttpContext http) =>
        http.Response.Cookies.Delete(Name, new CookieOptions { Path = "/", HttpOnly = true, SameSite = SameSiteMode.Lax, Domain = Domain(http) });

    /// <summary>The token the request's cookie carries, or null.</summary>
    public static string? Read(HttpContext http) =>
        http.Request.Cookies.TryGetValue(Name, out var token) && !string.IsNullOrEmpty(token) ? token : null;
}
