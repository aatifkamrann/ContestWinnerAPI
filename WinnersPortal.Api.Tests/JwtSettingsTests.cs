using System.Security.Cryptography;
using WinnersPortal.Services.Auth;
using WinnersPortal.Services.Settings;
using Xunit;

namespace WinnersPortal.Api.Tests;

/// <summary>
/// The JWT settings group: what may be saved, and the key an install
/// without one starts from. A value that would not bind is refused when it
/// is saved, since at the restart that binds it nobody is watching.
/// </summary>
public class JwtSettingsTests
{
    private static SettingDefinition Def(string key) => SettingsRegistry.Find(key)!;

    [Theory]
    [InlineData("5")]
    [InlineData("60")]
    [InlineData("1440")]
    [InlineData("")]
    [InlineData(null)]
    public void An_access_lifetime_in_range_or_cleared_is_accepted(string? value) =>
        Assert.Null(SettingsService.ValueProblem(Def(JwtSettings.AccessMinutesKey), value));

    [Theory]
    [InlineData("4")]
    [InlineData("1441")]
    [InlineData("sixty")]
    [InlineData("60.5")]
    [InlineData("-60")]
    public void An_access_lifetime_out_of_range_or_not_whole_is_refused(string value) =>
        Assert.NotNull(SettingsService.ValueProblem(Def(JwtSettings.AccessMinutesKey), value));

    [Theory]
    [InlineData("1", true)]
    [InlineData("365", true)]
    [InlineData("0", false)]
    [InlineData("366", false)]
    [InlineData("a month", false)]
    public void A_refresh_lifetime_is_whole_days_from_one_to_a_year(string value, bool accepted) =>
        Assert.Equal(accepted, SettingsService.ValueProblem(Def(JwtSettings.RefreshDaysKey), value) is null);

    [Fact]
    public void An_issuer_or_audience_may_be_cleared_but_not_run_on()
    {
        Assert.Null(SettingsService.ValueProblem(Def(JwtSettings.IssuerKey), ""));
        Assert.Null(SettingsService.ValueProblem(Def(JwtSettings.AudienceKey), "Mobile apps"));
        Assert.NotNull(SettingsService.ValueProblem(Def(JwtSettings.IssuerKey), new string('x', JwtSettings.MaxNameLength + 1)));
        Assert.NotNull(SettingsService.ValueProblem(Def(JwtSettings.AudienceKey), new string('x', JwtSettings.MaxNameLength + 1)));
    }

    [Fact]
    public void A_signing_key_is_replaced_never_removed_and_never_short()
    {
        var key = Def(JwtSettings.SigningKeyKey);

        Assert.Null(SettingsService.ValueProblem(key, JwtSettings.NewKey()));
        Assert.Null(SettingsService.ValueProblem(key, Convert.ToBase64String(RandomNumberGenerator.GetBytes(64))));
        Assert.NotNull(SettingsService.ValueProblem(key, null));
        Assert.NotNull(SettingsService.ValueProblem(key, ""));
        Assert.NotNull(SettingsService.ValueProblem(key, Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))));
        Assert.NotNull(SettingsService.ValueProblem(key, "correct horse battery staple"));
    }

    [Fact]
    public void A_made_key_decodes_to_enough_bytes_to_sign_with()
    {
        var made = JwtSettings.DecodeKey(JwtSettings.NewKey());

        Assert.NotNull(made);
        Assert.Equal(JwtSettings.MinKeyBytes, made.Length);
        Assert.NotEqual(JwtSettings.NewKey(), JwtSettings.NewKey());
    }

    [Fact]
    public void The_first_key_is_the_old_file_when_it_is_there_else_a_new_one()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wp-jwt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var (made, fromNothing) = JwtSettings.FirstKey(dir);
            Assert.False(fromNothing);
            Assert.NotNull(JwtSettings.DecodeKey(made));
            // The settings keep the key now; nothing is written beside the ring.
            Assert.False(File.Exists(Path.Combine(dir, JwtSettings.KeyFileName)));

            // An install that ran an earlier release keeps its key, and so
            // keeps everybody signed in across the upgrade.
            var old = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            File.WriteAllText(Path.Combine(dir, JwtSettings.KeyFileName), old + "\n");
            Assert.Equal((old, true), JwtSettings.FirstKey(dir));

            // A file that is not a usable key is not adopted.
            File.WriteAllText(Path.Combine(dir, JwtSettings.KeyFileName), "short");
            var (replaced, fromFile) = JwtSettings.FirstKey(dir);
            Assert.False(fromFile);
            Assert.NotEqual(old, replaced);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void The_settings_screen_lists_every_jwt_key_under_its_own_group()
    {
        string[] keys =
        [
            JwtSettings.IssuerKey, JwtSettings.AudienceKey, JwtSettings.SigningKeyKey,
            JwtSettings.AccessMinutesKey, JwtSettings.RefreshDaysKey,
        ];

        Assert.Contains(SettingsRegistry.Groups, g => g.Name == JwtSettings.Group);
        Assert.All(keys, k => Assert.Equal(JwtSettings.Group, Def(k).Group));
        // The key is the one secret, and has no default: the first start stores one.
        Assert.True(Def(JwtSettings.SigningKeyKey).IsSecret);
        Assert.Null(Def(JwtSettings.SigningKeyKey).Default);
        Assert.Equal($"{JwtSettings.DefaultAccessMinutes}", Def(JwtSettings.AccessMinutesKey).Default);
        Assert.Equal($"{JwtSettings.DefaultRefreshDays}", Def(JwtSettings.RefreshDaysKey).Default);
    }
}
