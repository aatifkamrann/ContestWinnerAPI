using System.Security.Cryptography;
using WinnersPortal.Services.Auth;
using WinnersPortal.Domain;
using Xunit;

namespace WinnersPortal.Api.Tests;

/// <summary>
/// The JWT the portal signs: what it carries, what it refuses, and how the
/// bearer handler's helpers read it back. No web host — the token service
/// and the handler's validation parameters are the whole of it.
/// </summary>
public class TokensTests
{
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);

    private static User Someone() => new()
    {
        Id = Guid.NewGuid(),
        Email = "someone@example.com",
        DisplayName = "Someone",
        PasswordHash = "x",
        Role = Roles.Freelancer,
    };

    [Fact]
    public async Task A_token_round_trips_its_claims()
    {
        var tokens = new TokenService(JwtConfig.WithKey(Key));
        var user = Someone();
        var now = DateTimeOffset.UtcNow;

        var token = tokens.Issue(Principal.Identity(user, persistent: true), now, TokenService.SessionLifetime);
        var principal = await tokens.ReadAsync(token);

        Assert.NotNull(principal);
        Assert.Equal(user.Id, Principal.UserId(principal));
        Assert.Equal(user.Email, Principal.Email(principal));
        Assert.Equal(user.DisplayName, Principal.Name(principal));
        Assert.Equal(Roles.Freelancer, Principal.Role(principal));
        Assert.Equal(user.SessionStamp, Principal.Stamp(principal));
        Assert.True(Principal.Persistent(principal));
        // The role claim is the one authorization policies read.
        Assert.True(principal.IsInRole(Roles.Freelancer));
        Assert.Equal(user.DisplayName, principal.Identity!.Name);
        // Three parts, base64url: a JWT, not an opaque ticket.
        Assert.Equal(3, token.Split('.').Length);
    }

    [Fact]
    public async Task A_bearer_token_is_not_persistent_and_expires_in_an_hour()
    {
        var tokens = new TokenService(JwtConfig.WithKey(Key));
        var now = DateTimeOffset.UtcNow;

        var principal = await tokens.ReadAsync(
            tokens.Issue(Principal.Identity(Someone()), now, tokens.AccessLifetime));

        Assert.NotNull(principal);
        Assert.False(Principal.Persistent(principal));
        var expires = TokenService.ExpiresAt(principal);
        Assert.NotNull(expires);
        Assert.Equal(TimeSpan.FromHours(1), tokens.AccessLifetime);
        Assert.InRange(expires.Value, now + tokens.AccessLifetime - TimeSpan.FromSeconds(2), now + tokens.AccessLifetime + TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task An_expired_token_is_refused()
    {
        var tokens = new TokenService(JwtConfig.WithKey(Key));
        var issued = DateTimeOffset.UtcNow - TimeSpan.FromDays(8);

        var token = tokens.Issue(Principal.Identity(Someone()), issued, TokenService.SessionLifetime);

        Assert.Null(await tokens.ReadAsync(token));
    }

    [Fact]
    public async Task A_token_signed_with_another_key_is_refused()
    {
        var theirs = new TokenService(JwtConfig.WithKey(RandomNumberGenerator.GetBytes(32)));
        var ours = new TokenService(JwtConfig.WithKey(Key));

        var token = theirs.Issue(Principal.Identity(Someone()), DateTimeOffset.UtcNow, TokenService.SessionLifetime);

        Assert.Null(await ours.ReadAsync(token));
    }

    [Fact]
    public async Task A_tampered_token_is_refused()
    {
        var tokens = new TokenService(JwtConfig.WithKey(Key));
        var token = tokens.Issue(Principal.Identity(Someone()), DateTimeOffset.UtcNow, TokenService.SessionLifetime);

        var parts = token.Split('.');
        // Flip a character of the payload; the signature no longer matches.
        var payload = parts[1].ToCharArray();
        payload[5] = payload[5] == 'a' ? 'b' : 'a';
        parts[1] = new string(payload);

        Assert.Null(await tokens.ReadAsync(string.Join('.', parts)));
    }

    [Fact]
    public void A_short_key_is_refused_at_construction()
    {
        Assert.Throws<ArgumentException>(() => new TokenService(JwtConfig.WithKey(RandomNumberGenerator.GetBytes(16))));
    }

    [Theory]
    [InlineData("SomebodyElse", "WinnersPortal")]
    [InlineData("WinnersPortal", "SomebodyElse")]
    public async Task A_token_naming_another_issuer_or_audience_is_refused(string issuer, string audience)
    {
        // What a changed issuer or audience does at the restart that binds it:
        // the same key, but every token issued before names the old values.
        var before = new TokenService(JwtConfig.WithKey(Key) with { Issuer = issuer, Audience = audience });
        var after = new TokenService(JwtConfig.WithKey(Key));

        var token = before.Issue(Principal.Identity(Someone()), DateTimeOffset.UtcNow, TokenService.SessionLifetime);

        Assert.NotNull(await before.ReadAsync(token));
        Assert.Null(await after.ReadAsync(token));
    }

    [Fact]
    public async Task The_bound_values_are_what_tokens_carry()
    {
        var config = new JwtConfig("Portal-A", "Apps-B", Key, TimeSpan.FromMinutes(15), TimeSpan.FromDays(7));
        var tokens = new TokenService(config);
        var now = DateTimeOffset.UtcNow;

        var principal = await tokens.ReadAsync(tokens.Issue(Principal.Identity(Someone()), now, tokens.AccessLifetime));

        Assert.NotNull(principal);
        Assert.Equal("Portal-A", principal.FindFirst("iss")?.Value);
        Assert.Equal("Apps-B", principal.FindFirst("aud")?.Value);
        Assert.Equal(TimeSpan.FromDays(7), tokens.RefreshLifetime);
        var expires = TokenService.ExpiresAt(principal)!.Value;
        Assert.InRange(expires, now + TimeSpan.FromMinutes(15) - TimeSpan.FromSeconds(2), now + TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(2));
    }
}
