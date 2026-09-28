using System.Collections.Concurrent;
using Microsoft.AspNetCore.Cors.Infrastructure;
using WinnersPortal.Api.Middleware;

namespace WinnersPortal.Api.Common;

/// <summary>
/// The CORS policy, from the settings rather than from startup: the one
/// origin the pages live on when the API is served from a hostname of its
/// own, with credentials, any header and any method, and the download's
/// file name exposed. Same-origin there is no policy at all, and the CORS
/// middleware passes every request through untouched — which is also what
/// a changed Web URL becomes on the next request, no restart.
/// </summary>
public sealed class SettingsCorsPolicyProvider : ICorsPolicyProvider
{
    private readonly ConcurrentDictionary<string, CorsPolicy> _policies = new(StringComparer.Ordinal);

    public Task<CorsPolicy?> GetPolicyAsync(HttpContext context, string? policyName)
    {
        var origin = Origins.Of(context);
        if (!origin.IsSplit) return Task.FromResult<CorsPolicy?>(null);
        var policy = _policies.GetOrAdd(origin.Origin!, o => new CorsPolicyBuilder(o)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials()
            .WithExposedHeaders("Content-Disposition")
            .Build());
        return Task.FromResult<CorsPolicy?>(policy);
    }
}
