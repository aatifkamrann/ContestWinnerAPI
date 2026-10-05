using Microsoft.Extensions.Logging.Abstractions;
using WinnersPortal.Services.Help;
using WinnersPortal.Services.Settings;
using Xunit;

namespace WinnersPortal.Api.Tests;

/// <summary>
/// Redis as a settings group (Settings/RedisSettings.cs), off by default:
/// what the two values resolve to, what is refused at save, how the older
/// REDIS_URL is read, when a restart is owed, and that off means no
/// connection at all — the connection used to fall back to localhost:6379
/// and, with AbortOnConnectFail off, came back alive-but-unconnected, so
/// the failure landed later as a SUBSCRIBE timing out at startup.
/// </summary>
public class RedisSettingsTests
{
    private static RedisConnection Open(RedisSettings.Choice choice) =>
        new(choice, NullLogger<RedisConnection>.Instance);

    // ------------------------------------------------------------ resolve

    [Theory]
    [InlineData(null, null)]
    [InlineData("false", "cache:6379")]
    [InlineData("", "cache:6379")]
    [InlineData("true", null)]
    [InlineData("true", "   ")]
    [InlineData("yes", "cache:6379")]
    public void Off_or_no_address_is_no_Redis(string? enabled, string? url)
    {
        var choice = RedisSettings.Resolve(enabled, url, "setting");
        Assert.Null(choice.Active);
        using var redis = Open(choice);
        Assert.Null(redis.Muxer);
    }

    [Theory]
    [InlineData("true", " cache:6379 ", "cache:6379")]
    [InlineData("TRUE", "redis://cache:6379", "redis://cache:6379")]
    [InlineData("true", "a:6379,b:6379,ssl=true", "a:6379,b:6379,ssl=true")]
    public void On_with_an_address_is_that_address_trimmed(string enabled, string url, string active)
    {
        var choice = RedisSettings.Resolve(enabled, url, "setting");
        Assert.Equal(active, choice.Active);
        Assert.False(choice.OnButUnusable);
    }

    [Fact]
    public void On_with_an_address_nothing_could_parse_runs_as_off_and_says_so()
    {
        // StackExchange.Redis forgives a great deal; a port that is not a number it does not.
        var choice = RedisSettings.Resolve("true", "cache:notaport", "setting");
        Assert.Null(choice.Active);
        Assert.True(choice.OnButUnusable);
    }

    [Fact]
    public void On_with_no_address_is_on_but_unusable()
    {
        Assert.True(RedisSettings.Resolve("true", null, "setting").OnButUnusable);
        Assert.False(RedisSettings.Resolve("false", null, "setting").OnButUnusable);
    }

    [Fact]
    public void The_endpoints_never_carry_the_password()
    {
        var choice = RedisSettings.Resolve("true", "cache:6379,password=hunter2", "setting");
        Assert.Equal("cache:6379", choice.Endpoints);
        Assert.Null(RedisSettings.Choice.Off.Endpoints);
    }

    // ----------------------------------------------------------- problems

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("cache:6379")]
    [InlineData("redis://cache:6379")]
    [InlineData("my-cluster.abc.euw1.cache.amazonaws.com:6379,ssl=true,abortConnect=false")]
    public void An_address_StackExchange_can_read_is_accepted(string? value) =>
        Assert.Null(RedisSettings.Problem(RedisSettings.UrlKey, value));

    [Fact]
    public void An_address_nothing_could_parse_is_refused_naming_the_shape()
    {
        var problem = RedisSettings.Problem(RedisSettings.UrlKey, "cache:notaport");
        Assert.NotNull(problem);
        Assert.Contains("host:port", problem);
    }

    [Fact]
    public void An_address_over_the_ceiling_is_refused() =>
        Assert.Contains("at most", RedisSettings.Problem(RedisSettings.UrlKey, new string('a', RedisSettings.MaxUrlLength + 1)));

    [Fact]
    public void Other_keys_are_not_this_groups_business() =>
        Assert.Null(RedisSettings.Problem(RedisSettings.EnabledKey, "anything"));

    [Fact]
    public void The_settings_service_refuses_a_bad_address_at_save()
    {
        var def = SettingsRegistry.Find(RedisSettings.UrlKey)!;
        Assert.NotNull(SettingsService.ValueProblem(def, "cache:notaport"));
        Assert.Null(SettingsService.ValueProblem(def, "cache:6379"));
        Assert.Null(SettingsService.ValueProblem(def, ""));
    }

    // -------------------------------------------------------- legacy pins

    [Fact]
    public void REDIS_URL_alone_is_read_as_both_pins() =>
        Assert.Equal(("true", "cache:6379"), RedisSettings.LegacyPins(" cache:6379 ", null, null));

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("", null, null)]
    [InlineData("cache:6379", "false", null)]
    [InlineData("cache:6379", null, "other:6379")]
    public void REDIS_URL_yields_to_a_pin_that_is_already_set_and_to_nothing(string? legacy, string? enabledPin, string? urlPin) =>
        Assert.Null(RedisSettings.LegacyPins(legacy, enabledPin, urlPin));

    // ------------------------------------------------------------ restart

    [Fact]
    public void A_restart_is_owed_when_what_is_saved_would_change_what_this_process_does()
    {
        var off = RedisSettings.Choice.Off;
        var on = RedisSettings.Resolve("true", "cache:6379", "setting");
        var elsewhere = RedisSettings.Resolve("true", "other:6379", "setting");
        var onUnusable = RedisSettings.Resolve("true", null, "setting");

        Assert.False(RedisSettings.RestartPending(off, off));
        Assert.False(RedisSettings.RestartPending(on, on));
        Assert.True(RedisSettings.RestartPending(off, on));
        Assert.True(RedisSettings.RestartPending(on, off));
        Assert.True(RedisSettings.RestartPending(on, elsewhere));
        // Saved on with nothing usable is what off already does.
        Assert.False(RedisSettings.RestartPending(off, onUnusable));
        Assert.True(RedisSettings.RestartPending(on, onUnusable));
    }

    // -------------------------------------------------------- the registry

    [Fact]
    public void The_group_is_registered_off_by_default_with_help_for_both_fields()
    {
        var enabled = SettingsRegistry.Find(RedisSettings.EnabledKey)!;
        var url = SettingsRegistry.Find(RedisSettings.UrlKey)!;
        Assert.Equal("false", enabled.Default);
        Assert.True(enabled.IsBoolean);
        Assert.Null(url.Default);
        Assert.False(url.IsSecret);
        Assert.Equal(RedisSettings.Group, enabled.Group);
        Assert.Equal(RedisSettings.Group, url.Group);
        Assert.Contains(SettingsRegistry.Groups, g => g.Name == RedisSettings.Group);
        Assert.NotNull(HelpRegistry.Find(HelpRegistry.TopicIdForSetting(RedisSettings.EnabledKey)));
        Assert.NotNull(HelpRegistry.Find(HelpRegistry.TopicIdForSetting(RedisSettings.UrlKey)));
        Assert.Equal("WP_REDIS_URL", SettingsRegistry.EnvVarName(RedisSettings.UrlKey));
        Assert.Equal("WP_REDIS_ENABLED", SettingsRegistry.EnvVarName(RedisSettings.EnabledKey));
    }
}
