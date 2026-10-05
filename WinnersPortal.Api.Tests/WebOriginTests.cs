using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using WinnersPortal.Api.Common;
using WinnersPortal.Api.Middleware;
using WinnersPortal.Services.Settings;
using Xunit;

namespace WinnersPortal.Api.Tests;

/// <summary>
/// The pages' origin, when the API is served from a hostname of its own:
/// what the two Branding settings resolve to, what a save refuses, the
/// parent the session cookie is scoped to, which redirects are made
/// absolute, and the CORS policy built from it per request.
/// </summary>
public class WebOriginTests
{
    [Theory]
    [InlineData("https://web.crm.com", null)]
    [InlineData("https://web.crm.com", "")]
    [InlineData("https://web.crm.com", "   ")]
    [InlineData("http://localhost", null)]
    // The same origin, however written: the browser is same-origin.
    [InlineData("https://web.crm.com", "https://web.crm.com")]
    [InlineData("https://web.crm.com/", "HTTPS://Web.CRM.com")]
    [InlineData("http://localhost", "http://localhost/")]
    // A value nothing can be built from is off, not a policy for nobody.
    [InlineData("not a url", "https://api.crm.com")]
    [InlineData("https://web.crm.com", "api.crm.com")]
    public void Blank_or_same_or_unusable_is_same_origin(string? webUrl, string? apiUrl)
    {
        var origin = WebOrigin.FromSettings(webUrl, apiUrl);
        Assert.False(origin.IsSplit);
        Assert.Null(origin.Origin);
        Assert.Null(origin.ApiOrigin);
        Assert.Null(origin.CookieDomain);
        Assert.Equal("/profile", origin.Absolute("/profile"));
    }

    [Theory]
    [InlineData("https://web.crm.com", "https://api.crm.com", "https://web.crm.com", "https://api.crm.com", "crm.com")]
    [InlineData("https://web.crm.com/", "https://api.crm.com/", "https://web.crm.com", "https://api.crm.com", "crm.com")]
    [InlineData("  HTTPS://Web.CRM.com  ", "https://API.crm.com", "https://web.crm.com", "https://api.crm.com", "crm.com")]
    [InlineData("https://portal.crm.co.uk", "https://api.crm.co.uk", "https://portal.crm.co.uk", "https://api.crm.co.uk", "crm.co.uk")]
    [InlineData("https://crm.com", "https://api.crm.com", "https://crm.com", "https://api.crm.com", "crm.com")]
    [InlineData("http://localhost:3100", "http://localhost:8091", "http://localhost:3100", "http://localhost:8091", null)]
    [InlineData("http://10.0.0.5:3000", "http://10.0.0.5:8080", "http://10.0.0.5:3000", "http://10.0.0.5:8080", null)]
    public void Two_origins_resolve_to_the_pages_the_api_and_the_shared_parent(
        string webUrl, string apiUrl, string origin, string api, string? domain)
    {
        var parsed = WebOrigin.FromSettings(webUrl, apiUrl);
        Assert.True(parsed.IsSplit);
        Assert.Equal(origin, parsed.Origin);
        Assert.Equal(api, parsed.ApiOrigin);
        Assert.Equal(domain, parsed.CookieDomain);
    }

    [Theory]
    [InlineData(WebOrigin.ApiUrlKey, null, null)]
    [InlineData(WebOrigin.ApiUrlKey, "", null)]
    [InlineData(WebOrigin.ApiUrlKey, "https://api.crm.com", null)]
    [InlineData(WebOrigin.ApiUrlKey, "https://api.crm.com/", null)]
    [InlineData(WebOrigin.ApiUrlKey, "api.crm.com", "absolute")]
    [InlineData(WebOrigin.ApiUrlKey, "ftp://api.crm.com", "absolute")]
    [InlineData(WebOrigin.ApiUrlKey, "https://api.crm.com/api", "origin")]
    [InlineData(WebOrigin.ApiUrlKey, "https://api.crm.com/?x=1", "origin")]
    [InlineData(WebOrigin.ApiUrlKey, "https://me@api.crm.com", "origin")]
    [InlineData(WebOrigin.WebUrlKey, "https://web.crm.com", null)]
    [InlineData(WebOrigin.WebUrlKey, "https://web.crm.com/portal", null)]
    [InlineData(WebOrigin.WebUrlKey, "web.crm.com", "absolute")]
    [InlineData("branding.portalName", "anything at all", null)]
    public void A_save_refuses_what_the_browser_could_not_use(string key, string? value, string? expected)
    {
        var problem = WebOrigin.Problem(key, value);
        if (expected is null) Assert.Null(problem);
        else Assert.Contains(expected, problem!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    // Blank, same origin, or unusable: nothing to judge.
    [InlineData("https://web.crm.com", null, null)]
    [InlineData("https://web.crm.com", "https://web.crm.com", null)]
    [InlineData("not a url", "https://api.crm.com", null)]
    // Under one parent, in every shape the layout allows.
    [InlineData("https://web.crm.com", "https://api.crm.com", null)]
    [InlineData("https://crm.com", "https://api.crm.com", null)]
    [InlineData("https://web.crm.com", "https://crm.com", null)]
    [InlineData("https://web.crm.com", "https://api.eu.crm.com", null)]
    [InlineData("https://portal.crm.co.uk", "https://api.crm.co.uk", null)]
    [InlineData("http://localhost:3100", "http://localhost:8091", null)]
    [InlineData("http://10.0.0.5:3000", "http://10.0.0.5:8080", null)]
    // Two domains: the cookie could not reach both.
    [InlineData("https://web.crm.com", "https://api.crmpro.com", "api.crmpro.com")]
    [InlineData("https://web.crm.com", "https://api.notcrm.com", "api.notcrm.com")]
    [InlineData("https://crm.com", "https://api.crmpro.com", "api.crmpro.com")]
    [InlineData("http://localhost:3100", "http://127.0.0.1:8091", "127.0.0.1")]
    [InlineData("https://web.crm.com", "https://web.crm.com.evil.example", "web.crm.com.evil.example")]
    public void Two_hostnames_must_share_a_domain(string webUrl, string apiUrl, string? refusedHost)
    {
        var problem = WebOrigin.PairProblem(webUrl, apiUrl);
        if (refusedHost is null) Assert.Null(problem);
        else
        {
            Assert.Contains(refusedHost, problem!);
            Assert.Contains("session cookie", problem);
        }
    }

    [Fact]
    public void The_settings_registry_refuses_the_same_values()
    {
        var api = SettingsRegistry.Find(WebOrigin.ApiUrlKey)!;
        Assert.Equal("branding", api.Group);
        Assert.NotNull(SettingsService.ValueProblem(api, "https://api.crm.com/api"));
        Assert.Null(SettingsService.ValueProblem(api, "https://api.crm.com"));
        Assert.Null(SettingsService.ValueProblem(api, ""));
        var web = SettingsRegistry.Find(WebOrigin.WebUrlKey)!;
        Assert.NotNull(SettingsService.ValueProblem(web, "web.crm.com"));
        Assert.Null(SettingsService.ValueProblem(web, "https://web.crm.com"));
    }

    [Theory]
    [InlineData("/profile?github=connected", "https://web.crm.com/profile?github=connected")]
    [InlineData("/login?next=%2Fapi%2Fattachments%2F1%2Fdownload", "https://web.crm.com/login?next=%2Fapi%2Fattachments%2F1%2Fdownload")]
    [InlineData("https://github.com/login/oauth/authorize?client_id=x", "https://github.com/login/oauth/authorize?client_id=x")]
    [InlineData("https://s3.eu-west-1.amazonaws.com/winnersportal/k?X-Amz-Signature=s", "https://s3.eu-west-1.amazonaws.com/winnersportal/k?X-Amz-Signature=s")]
    [InlineData("//evil.example/x", "//evil.example/x")]
    public void Only_a_site_relative_location_is_sent_to_the_pages(string location, string expected)
    {
        Assert.Equal(expected, Split().Absolute(location));
    }

    [Fact]
    public async Task A_redirect_at_the_edge_lands_on_the_pages_when_they_are_elsewhere()
    {
        var split = await RedirectAsync(Split(), "/client/opportunities?github=error");
        Assert.Equal(302, split.Response.StatusCode);
        Assert.Equal("https://web.crm.com/client/opportunities?github=error", split.Response.Headers.Location.ToString());

        var same = await RedirectAsync(WebOrigin.None, "/client/opportunities?github=error");
        Assert.Equal(302, same.Response.StatusCode);
        Assert.Equal("/client/opportunities?github=error", same.Response.Headers.Location.ToString());

        var absolute = await RedirectAsync(Split(), "https://github.com/login/oauth/authorize?state=s");
        Assert.Equal("https://github.com/login/oauth/authorize?state=s", absolute.Response.Headers.Location.ToString());
    }

    [Fact]
    public async Task A_request_nothing_resolved_is_same_origin()
    {
        var ctx = Context();
        Assert.False(Origins.Of(ctx).IsSplit);
        await new WebRedirect("/somewhere").ExecuteAsync(ctx);
        Assert.Equal("/somewhere", ctx.Response.Headers.Location.ToString());
    }

    [Fact]
    public async Task The_cors_policy_is_the_pages_origin_with_credentials_and_nothing_at_all_same_origin()
    {
        var provider = new SettingsCorsPolicyProvider();

        var split = Context();
        split.Items[Origins.ItemKey] = Split();
        var policy = await provider.GetPolicyAsync(split, null);
        Assert.NotNull(policy);
        Assert.Equal(["https://web.crm.com"], policy!.Origins);
        Assert.True(policy.SupportsCredentials);
        Assert.True(policy.AllowAnyHeader);
        Assert.True(policy.AllowAnyMethod);
        Assert.Contains("Content-Disposition", policy.ExposedHeaders);

        // The same origin again is the same policy object; another origin
        // — the setting changed — is another policy, with no restart.
        var again = Context();
        again.Items[Origins.ItemKey] = Split();
        Assert.Same(policy, await provider.GetPolicyAsync(again, null));
        var moved = Context();
        moved.Items[Origins.ItemKey] = WebOrigin.FromSettings("https://portal.other.com", "https://api.other.com");
        Assert.Equal(["https://portal.other.com"], (await provider.GetPolicyAsync(moved, null))!.Origins);

        Assert.Null(await provider.GetPolicyAsync(Context(), null));
    }

    private static WebOrigin Split() => WebOrigin.FromSettings("https://web.crm.com", "https://api.crm.com");

    private static HttpContext Context() =>
        new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };

    private static async Task<HttpContext> RedirectAsync(WebOrigin origin, string location)
    {
        var ctx = Context();
        ctx.Items[Origins.ItemKey] = origin;
        await new WebRedirect(location).ExecuteAsync(ctx);
        return ctx;
    }
}
