using WinnersPortal.Api.Middleware;

namespace WinnersPortal.Api.Common;

/// <summary>
/// A redirect the browser follows to a page: the location a service named,
/// made absolute against the pages' origin when the API is served from a
/// hostname of its own, so Connect GitHub comes back to the portal and not
/// to <c>api.crm.com/profile</c>. An absolute location — a signed storage
/// URL, GitHub's authorize page — is sent as it is. Same-origin, this is
/// <see cref="Results.Redirect(string, bool, bool)"/> and nothing more.
/// </summary>
public sealed class WebRedirect(string location) : IResult
{
    public string Location { get; } = location;

    public Task ExecuteAsync(HttpContext httpContext) =>
        Results.Redirect(Origins.Of(httpContext).Absolute(Location)).ExecuteAsync(httpContext);
}
