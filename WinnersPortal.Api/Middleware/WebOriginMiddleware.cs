using WinnersPortal.Services.Settings;

namespace WinnersPortal.Api.Middleware;

/// <summary>
/// Resolves, once per request, where the pages and the API are from the
/// two Branding settings, and leaves the answer on the request for the
/// CORS policy, the session cookie and every redirect to a page to read
/// without asking again. Cheap by construction: the settings serve both
/// values from their in-memory cache, invalidated when either is saved —
/// so a new address is in force on the very next request.
/// </summary>
public sealed class WebOriginMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext ctx, SettingsService settings)
    {
        ctx.Items[Origins.ItemKey] = WebOrigin.FromSettings(
            await settings.GetAsync(WebOrigin.WebUrlKey, ctx.RequestAborted),
            await settings.GetAsync(WebOrigin.ApiUrlKey, ctx.RequestAborted));
        await next(ctx);
    }
}

/// <summary>The request's resolved <see cref="WebOrigin"/>; same-origin where nothing resolved it (tests, a context of one's own).</summary>
public static class Origins
{
    public const string ItemKey = "WinnersPortal.WebOrigin";

    public static WebOrigin Of(HttpContext ctx) =>
        ctx.Items.TryGetValue(ItemKey, out var value) && value is WebOrigin origin ? origin : WebOrigin.None;
}
