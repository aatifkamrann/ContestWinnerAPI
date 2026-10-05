using Microsoft.AspNetCore.Http;
using WinnersPortal.Services.Settings;
using Xunit;

namespace WinnersPortal.Api.Tests;

/// <summary>
/// The pure halves of the three limits switches. What must not regress is
/// the allowlist: maintenance that blocks the login door locks the admin
/// out with the switch stuck on, and maintenance that blocks the webhook
/// receiver silently loses milestone claims.
/// </summary>
public class LimitsTests
{
    [Theory]
    [InlineData("/api/health")]
    [InlineData("/api/public/branding")]
    [InlineData("/api/auth/login")]
    [InlineData("/api/auth/logout")]
    [InlineData("/api/auth/me")]
    [InlineData("/api/auth/forgot-password")] // the admin who forgot it mid-outage
    [InlineData("/api/auth/reset-password")]
    [InlineData("/api/setup/status")]
    [InlineData("/api/settings")]
    [InlineData("/api/help/topics")]
    [InlineData("/api/webhooks/github")]
    public void Maintenance_keeps_the_survival_paths_open_for_everyone(string path) =>
        Assert.True(MaintenanceGate.Allows(new PathString(path), isAdmin: false));

    [Theory]
    [InlineData("/api/dashboard")]
    [InlineData("/api/opportunities")]
    [InlineData("/api/opportunities/some-slug/applications")]
    [InlineData("/api/entries/mine")]
    [InlineData("/api/applications/mine")]
    [InlineData("/api/auth/register")] // new accounts can wait out an outage
    [InlineData("/api/admin/operations")]
    public void Maintenance_blocks_everything_else_for_non_admins(string path) =>
        Assert.False(MaintenanceGate.Allows(new PathString(path), isAdmin: false));

    [Theory]
    [InlineData("/api/dashboard")]
    [InlineData("/api/admin/operations")]
    [InlineData("/api/auth/register")]
    public void Maintenance_never_blocks_an_administrator(string path) =>
        Assert.True(MaintenanceGate.Allows(new PathString(path), isAdmin: true));

    [Fact]
    public void Maintenance_only_governs_the_api()
    {
        // Next's pages and assets go through the web container; the gate must
        // not answer 503 for anything it does not own.
        Assert.True(MaintenanceGate.Allows(new PathString("/opportunities/some-slug"), isAdmin: false));
        Assert.True(MaintenanceGate.Allows(new PathString("/themes/dark.css"), isAdmin: false));
    }

    [Fact]
    public void Captcha_requires_both_halves_of_the_key_pair()
    {
        // Half a configuration must behave like none: a site key without a
        // secret would render a challenge no server ever verifies, and a
        // secret without a site key would demand tokens no form can produce.
        Assert.True(Captcha.Required("site", "secret"));
        Assert.False(Captcha.Required("site", null));
        Assert.False(Captcha.Required(null, "secret"));
        Assert.False(Captcha.Required("", " "));
        Assert.False(Captcha.Required(null, null));
    }
}
