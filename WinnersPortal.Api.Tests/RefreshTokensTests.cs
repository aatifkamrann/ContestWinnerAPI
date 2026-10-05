using WinnersPortal.Services.Auth;
using WinnersPortal.Domain;
using Xunit;

namespace WinnersPortal.Api.Tests;

/// <summary>
/// The refresh token's rules: a secret kept only hashed, a month long,
/// single-use, and dead the moment the account's session stamp rolls.
/// </summary>
public class RefreshTokensTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The shipped lifetime; the JWT settings can set another.</summary>
    private static readonly TimeSpan Month = TimeSpan.FromDays(JwtSettings.DefaultRefreshDays);

    private static User Someone() => new()
    {
        Id = Guid.NewGuid(),
        Email = "someone@example.com",
        DisplayName = "Someone",
        PasswordHash = "x",
        Role = Roles.Client,
    };

    [Fact]
    public void Issue_keeps_only_the_hash_and_the_stamp_of_the_day()
    {
        var user = Someone();

        var (row, token) = RefreshTokens.Issue(user, Now, Month);

        Assert.Equal(43, token.Length); // 32 bytes as unpadded base64url
        Assert.NotEqual(token, row.TokenHash);
        Assert.Equal(RefreshTokens.Hash(token), row.TokenHash);
        Assert.Equal(64, row.TokenHash.Length); // SHA-256 as hex
        Assert.Equal(user.Id, row.UserId);
        Assert.Equal(user.SessionStamp, row.SessionStamp);
        Assert.Equal(Now, row.IssuedAtUtc);
        Assert.Equal(Now + Month, row.ExpiresAtUtc);
        Assert.Null(row.UsedAtUtc);
        Assert.Null(row.RevokedAtUtc);
    }

    [Fact]
    public void Two_issues_never_share_a_token()
    {
        var user = Someone();
        Assert.NotEqual(RefreshTokens.Issue(user, Now, Month).Token, RefreshTokens.Issue(user, Now, Month).Token);
    }

    [Fact]
    public void A_fresh_token_is_valid_until_its_month_is_up()
    {
        var user = Someone();
        var (row, _) = RefreshTokens.Issue(user, Now, Month);

        Assert.Equal(RefreshTokens.Presented.Valid, RefreshTokens.Classify(row, user.SessionStamp, Now));
        Assert.Equal(RefreshTokens.Presented.Valid, RefreshTokens.Classify(row, user.SessionStamp, Now + TimeSpan.FromDays(29)));
        Assert.Equal(RefreshTokens.Presented.Expired, RefreshTokens.Classify(row, user.SessionStamp, Now + Month));
    }

    [Fact]
    public void A_rolled_session_stamp_expires_every_token_issued_before_it()
    {
        var user = Someone();
        var (row, _) = RefreshTokens.Issue(user, Now, Month);

        user.SessionStamp = Guid.NewGuid(); // a password change, a lock, an erasure

        Assert.Equal(RefreshTokens.Presented.Expired, RefreshTokens.Classify(row, user.SessionStamp, Now));
    }

    [Fact]
    public void A_spent_token_reads_as_reused_and_a_withdrawn_one_as_revoked()
    {
        var user = Someone();
        var (spent, _) = RefreshTokens.Issue(user, Now, Month);
        spent.UsedAtUtc = Now + TimeSpan.FromMinutes(1);
        var (withdrawn, _) = RefreshTokens.Issue(user, Now, Month);
        withdrawn.RevokedAtUtc = Now + TimeSpan.FromMinutes(1);
        // Revoked outranks reused: a token ended on purpose is not evidence of a leak.
        var (both, _) = RefreshTokens.Issue(user, Now, Month);
        both.UsedAtUtc = Now;
        both.RevokedAtUtc = Now;

        Assert.Equal(RefreshTokens.Presented.Reused, RefreshTokens.Classify(spent, user.SessionStamp, Now + TimeSpan.FromHours(1)));
        Assert.Equal(RefreshTokens.Presented.Revoked, RefreshTokens.Classify(withdrawn, user.SessionStamp, Now + TimeSpan.FromHours(1)));
        Assert.Equal(RefreshTokens.Presented.Revoked, RefreshTokens.Classify(both, user.SessionStamp, Now + TimeSpan.FromHours(1)));
    }
}
