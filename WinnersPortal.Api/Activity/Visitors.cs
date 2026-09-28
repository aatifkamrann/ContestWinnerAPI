using System.Security.Cryptography;
using WinnersPortal.Services.Activity;

namespace WinnersPortal.Api.Activity;

/// <summary>
/// The browser session's id, so a visitor's pages thread together and
/// stay threaded once they sign in. A session cookie — no expiry, so it
/// dies with the browser — and HttpOnly, because nothing in the page needs
/// to read it. Issued by the visit endpoint, the first request every page
/// makes; read by that endpoint and by the middleware that records actions.
/// </summary>
public static class Visitors
{
    public const string VisitorCookie = "wp.visitor";

    public static string? VisitorOf(HttpRequest request) =>
        request.Cookies.TryGetValue(VisitorCookie, out var v) && ActivityNames.IsVisitorId(v) ? v : null;

    public static string IssueVisitor(HttpContext http)
    {
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        http.Response.Cookies.Append(VisitorCookie, id, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = http.Request.IsHttps,
            Path = "/",
            // No Expires or MaxAge: a session cookie, gone when the browser closes.
        });
        return id;
    }
}
