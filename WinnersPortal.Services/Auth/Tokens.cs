using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using WinnersPortal.Domain;

namespace WinnersPortal.Services.Auth;

/// <summary>
/// Signs and describes the portal's JSON Web Tokens. One key, HMAC-SHA256:
/// this API is the only party that issues or checks a token, so a shared
/// secret is the whole of the arrangement and no public key needs
/// publishing. Issuer, audience, key and the bearer lifetimes are settings
/// (<see cref="JwtSettings"/>), bound once at startup. The key is stored
/// encrypted with the data-protection key ring, so losing that folder signs
/// everybody out, the same day it loses the other encrypted settings.
/// </summary>
public sealed class TokenService
{
    /// <summary>
    /// The browser's ticket: a week, sliding, exactly the cookie's old
    /// lifetime. Long because every request checks the row's stamp anyway
    /// (<see cref="SessionValidation"/>), so the lifetime is not what
    /// bounds a revocation.
    /// </summary>
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(7);

    private readonly SymmetricSecurityKey _key;
    private readonly JsonWebTokenHandler _handler = new() { SetDefaultTimesOnTokenCreation = false };

    public TokenService(JwtConfig config)
    {
        if (config.Key.Length < JwtSettings.MinKeyBytes)
            throw new ArgumentException($"The JWT signing key must be at least {JwtSettings.MinKeyBytes} bytes.", nameof(config));
        Config = config;
        _key = new SymmetricSecurityKey(config.Key);
        ValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = config.Issuer,
            ValidAudience = config.Audience,
            IssuerSigningKey = _key,
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ClockSkew = TimeSpan.FromMinutes(1),
            NameClaimType = Principal.NameClaim,
            RoleClaimType = Principal.RoleClaim,
        };
    }

    public TokenValidationParameters ValidationParameters { get; }

    /// <summary>What this process was bound with at startup; the settings may have moved on since.</summary>
    public JwtConfig Config { get; }

    /// <summary>
    /// A bearer access token's lifetime, an hour unless set. A client that
    /// holds no cookie gets nothing renewed on its behalf, so it exchanges a
    /// refresh token for the next one instead.
    /// </summary>
    public TimeSpan AccessLifetime => Config.AccessLifetime;

    /// <summary>A refresh token's lifetime, a month unless set (<see cref="RefreshTokens"/>).</summary>
    public TimeSpan RefreshLifetime => Config.RefreshLifetime;

    /// <summary>A signed token for this identity, valid from <paramref name="now"/> for <paramref name="lifetime"/>.</summary>
    public string Issue(ClaimsIdentity identity, DateTimeOffset now, TimeSpan lifetime) =>
        _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Config.Issuer,
            Audience = Config.Audience,
            Subject = identity,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = (now + lifetime).UtcDateTime,
            SigningCredentials = new SigningCredentials(_key, SecurityAlgorithms.HmacSha256),
        });

    /// <summary>
    /// The claims a token carries, or null when it is not one of ours: a
    /// bad signature, another issuer, or past its expiry. What the bearer
    /// handler does on every request, exposed for the tests.
    /// </summary>
    public async Task<ClaimsPrincipal?> ReadAsync(string token)
    {
        var result = await _handler.ValidateTokenAsync(token, ValidationParameters);
        return result.IsValid ? new ClaimsPrincipal(result.ClaimsIdentity) : null;
    }

    /// <summary>When the token stops working, read off its claims.</summary>
    public static DateTimeOffset? ExpiresAt(ClaimsPrincipal user) =>
        long.TryParse(user.FindFirstValue(JwtRegisteredClaimNames.Exp), out var exp)
            ? DateTimeOffset.FromUnixTimeSeconds(exp)
            : null;
}
