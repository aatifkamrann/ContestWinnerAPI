namespace WinnersPortal.Services.Settings;

/// <summary>
/// Where the pages and the API are, when they are not on one hostname —
/// <c>web.crm.com</c> and <c>api.crm.com</c>, the two-site IIS layout. Two
/// settings say so, both under Branding: <c>branding.publicUrl</c>, the
/// Web URL every emailed link is already built from, and
/// <c>branding.apiUrl</c>, the API URL the browser reaches the API at. With
/// the API URL blank, or the same origin as the Web URL, the browser is
/// same-origin and none of this exists — behind Caddy, on a single IIS
/// site and in the dev loop the API answers as it always has.
/// </summary>
/// <remarks>
/// Resolved once per request from the settings (their cache makes that
/// two dictionary reads), so a change on the Settings screen is in force
/// on the next request with no restart. Split, it is three things the API
/// has to know: the one origin a page may call it from with its cookie
/// (the CORS policy), the domain the session cookie is scoped to so that
/// the web server sees it too (it reads the cookie to render a signed-in
/// page), and the host a site-relative redirect — Connect GitHub's way
/// back, the sign-in page a download sends a visitor to — is made absolute
/// against, since a relative one would land on the API's own hostname.
/// </remarks>
public sealed class WebOrigin
{
    /// <summary>The setting the pages' address is read from.</summary>
    public const string WebUrlKey = "branding.publicUrl";

    /// <summary>The setting the browser's address for the API is read from; blank means the pages' own origin.</summary>
    public const string ApiUrlKey = "branding.apiUrl";

    /// <summary>Same-origin: no CORS, a host-only cookie, redirects as written.</summary>
    public static readonly WebOrigin None = new(null, null, null);

    private WebOrigin(string? origin, string? apiOrigin, string? cookieDomain)
    {
        Origin = origin;
        ApiOrigin = apiOrigin;
        CookieDomain = cookieDomain;
    }

    /// <summary>The pages' origin, scheme and host (and port) only — <c>https://web.crm.com</c> — or null when same-origin.</summary>
    public string? Origin { get; }

    /// <summary>Where the browser reaches the API — <c>https://api.crm.com</c> — or null when same-origin; the pages stamp it on themselves.</summary>
    public string? ApiOrigin { get; }

    /// <summary>
    /// The domain the session cookie carries so that the pages' host sees
    /// it too: the parent the two hostnames share (<c>crm.com</c> for
    /// <c>web.crm.com</c> and <c>api.crm.com</c>). Null when same-origin,
    /// and for a single-label host or an address, where a host-only cookie
    /// already reaches every port.
    /// </summary>
    public string? CookieDomain { get; }

    /// <summary>True when the pages live on another origin than the API.</summary>
    public bool IsSplit => Origin is not null;

    /// <summary>
    /// The two settings as the browser will see them. The API URL blank,
    /// either value unusable, or both the same origin, and the answer is
    /// same-origin; the screen refuses an unusable value at save time
    /// (<see cref="Problem"/>), so at run time a bad one is merely off.
    /// </summary>
    public static WebOrigin FromSettings(string? webUrl, string? apiUrl)
    {
        if (!TryAbsolute(apiUrl, out var api) || !TryAbsolute(webUrl, out var web)) return None;
        var apiOrigin = api.GetLeftPart(UriPartial.Authority);
        var webOrigin = web.GetLeftPart(UriPartial.Authority);
        if (string.Equals(apiOrigin, webOrigin, StringComparison.OrdinalIgnoreCase)) return None;
        return new WebOrigin(webOrigin, apiOrigin, DomainOf(web));
    }

    /// <summary>
    /// Why a value cannot be stored under one of the two settings, or null:
    /// the Web URL must be an absolute http or https address, the API URL an
    /// origin — scheme and host, nothing after — since a policy built from
    /// anything else would silently admit nobody. Blank is allowed for both;
    /// the API URL blank is the common case.
    /// </summary>
    public static string? Problem(string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        if (key == WebUrlKey)
            return TryAbsolute(text, out _)
                ? null
                : "Web URL must be an absolute http or https address, such as https://web.crm.com.";
        if (key == ApiUrlKey)
        {
            if (!TryAbsolute(text, out var uri))
                return "API URL must be an absolute http or https address, such as https://api.crm.com.";
            if (uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
                || !string.IsNullOrEmpty(uri.UserInfo))
                return "API URL is an origin — scheme and host only, such as https://api.crm.com — with nothing after the host.";
        }
        return null;
    }

    /// <summary>
    /// Why the two settings cannot stand together, or null: with an API URL
    /// on another origin than the Web URL, the API's host must be under the
    /// Web URL's domain — <c>api.crm.com</c> beside <c>web.crm.com</c>, or
    /// beside <c>crm.com</c> itself — because the session cookie is scoped
    /// to that domain and a browser will not share a cookie across two. A
    /// single-label or address host (<c>localhost</c>, <c>10.0.0.5</c>) has
    /// no domain to share, so the two must then be the same host, on any
    /// ports. Refused at save rather than found later as every page
    /// rendering signed-out.
    /// </summary>
    public static string? PairProblem(string? webUrl, string? apiUrl)
    {
        if (!TryAbsolute(apiUrl, out var api) || !TryAbsolute(webUrl, out var web)) return null;
        if (string.Equals(api.GetLeftPart(UriPartial.Authority), web.GetLeftPart(UriPartial.Authority),
                StringComparison.OrdinalIgnoreCase))
            return null;
        var parent = DomainOf(web);
        var ok = parent is null
            ? string.Equals(api.Host, web.Host, StringComparison.OrdinalIgnoreCase)
            : string.Equals(api.Host, parent, StringComparison.OrdinalIgnoreCase)
              || api.Host.EndsWith("." + parent, StringComparison.OrdinalIgnoreCase);
        if (ok) return null;
        var under = parent ?? web.Host;
        return $"API URL must be a hostname under the Web URL's domain: with Web URL {web.GetLeftPart(UriPartial.Authority)} "
            + $"the API can be at {(parent is null ? under : "api." + parent)} or another name under {under}, not {api.Host}. "
            + "A browser will not share the session cookie across two domains, so pages would render signed-out. "
            + "Give the API an alias under that domain, or leave API URL blank and proxy /api/* on the pages' own site.";
    }

    /// <summary>
    /// A site-relative location made absolute against the pages' origin;
    /// anything else — a signed storage URL, GitHub's authorize page, a
    /// protocol-relative address — passes through as written.
    /// </summary>
    public string Absolute(string location) =>
        Origin is not null && location.StartsWith('/') && !location.StartsWith("//", StringComparison.Ordinal)
            ? Origin + location
            : location;

    private static bool TryAbsolute(string? value, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(value)) return false;
        return Uri.TryCreate(value.Trim(), UriKind.Absolute, out uri!)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    /// <summary>
    /// The parent the pages' host and the API's share, on the rule the two-site
    /// layout follows: each is one label under one domain, so the parent is
    /// the pages' host less its first label — <c>web.crm.com</c> gives
    /// <c>crm.com</c>, which <c>api.crm.com</c> is under. Pages at the apex
    /// (<c>crm.com</c>, the API at <c>api.crm.com</c>) give the apex itself.
    /// A single label (<c>localhost</c>) or an address gives nothing: there
    /// is no parent, and a host-only cookie is what works there.
    /// </summary>
    public static string? DomainOf(Uri uri)
    {
        if (uri.HostNameType != UriHostNameType.Dns) return null;
        var labels = uri.Host.Split('.');
        return labels.Length switch
        {
            < 2 => null,
            2 => uri.Host,
            _ => string.Join('.', labels.Skip(1)),
        };
    }
}
