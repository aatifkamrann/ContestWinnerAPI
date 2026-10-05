using WinnersPortal.Services.Auth;
using Xunit;

namespace WinnersPortal.Api.Tests;

/// <summary>
/// The pure parts of the forgot-password flow. The token is a secret that
/// rides in a URL and is stored only hashed; the throttle is what stops the
/// open forgot form from being a way to flood someone else's inbox.
/// </summary>
public class PasswordResetTests
{
    [Fact]
    public void Tokens_are_long_unique_and_survive_a_query_string()
    {
        var a = PasswordReset.NewToken();
        var b = PasswordReset.NewToken();

        Assert.NotEqual(a, b);
        Assert.Equal(43, a.Length); // 32 bytes as unpadded base64url
        // The three characters a URL would mangle.
        Assert.DoesNotContain('+', a);
        Assert.DoesNotContain('/', a);
        Assert.DoesNotContain('=', a);
    }

    [Fact]
    public void Hash_is_deterministic_hex_and_never_the_token()
    {
        var token = PasswordReset.NewToken();
        var hash = PasswordReset.Hash(token);

        Assert.Equal(hash, PasswordReset.Hash(token));
        Assert.Matches("^[0-9a-f]{64}$", hash);
        Assert.NotEqual(token, hash);
        Assert.NotEqual(hash, PasswordReset.Hash(PasswordReset.NewToken()));
    }

    [Fact]
    public void A_token_issued_moments_ago_is_kept_but_an_older_one_is_replaced()
    {
        var now = new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset IssuedAgo(TimeSpan ago) => now - ago + PasswordReset.Lifetime;

        Assert.False(PasswordReset.TooSoonToReissue(null, now));                        // nothing standing
        Assert.True(PasswordReset.TooSoonToReissue(IssuedAgo(TimeSpan.Zero), now));      // this instant
        Assert.True(PasswordReset.TooSoonToReissue(IssuedAgo(TimeSpan.FromSeconds(30)), now));
        Assert.False(PasswordReset.TooSoonToReissue(IssuedAgo(TimeSpan.FromMinutes(2)), now));
        Assert.False(PasswordReset.TooSoonToReissue(IssuedAgo(TimeSpan.FromHours(5)), now)); // long expired
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("Sh0rt!", false)]              // seven, and everything else met
    [InlineData("Passw0rd", false)]            // no special character
    [InlineData("password1!", false)]          // no uppercase
    [InlineData("PASSWORD1!", false)]          // no lowercase
    [InlineData("Password!!", false)]          // no number
    [InlineData("1234567890", false)]          // long enough, and nothing else
    [InlineData("Passw0rd!", true)]            // nine, and all five met
    [InlineData("a Very l0ng one", true)]      // a space counts as the special
    public void A_reset_holds_the_password_to_registrations_floor(string? password, bool ok) =>
        Assert.Equal(ok, PasswordRules.Acceptable(password));

    [Fact]
    public void A_refusal_names_the_requirements_that_were_missed()
    {
        Assert.Empty(PasswordRules.Unmet("Passw0rd!"));
        Assert.Equal(
            ["An uppercase letter", "A special character"],
            PasswordRules.Unmet("password1"));
        // Every requirement is named in the one-sentence message the API
        // sends a caller that is not the form.
        Assert.Contains("8", PasswordRules.Error);
        Assert.Contains("special character", PasswordRules.Error);
    }
}
