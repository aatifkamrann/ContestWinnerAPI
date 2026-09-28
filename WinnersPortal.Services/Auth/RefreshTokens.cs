using WinnersPortal.Services.Opportunities;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using WinnersPortal.Domain;

namespace WinnersPortal.Services.Auth;

/// <summary>
/// The rules of the refresh token, pure so they are testable: what one
/// looks like, how the row keeps it, and what presenting it means depending
/// on the state the row is in. How long one lasts is a setting
/// (<see cref="JwtSettings.RefreshDaysKey"/>), a month unless changed.
/// </summary>
public static class RefreshTokens
{
    /// <summary>Base64url, unpadded: 256 random bits that ride in a JSON body.</summary>
    public static string NewToken() => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    public enum Presented
    {
        /// <summary>Good: unspent, unrevoked, in date, and the account's stamp unchanged.</summary>
        Valid,
        /// <summary>Past its lifetime, or the account's stamp rolled since it was issued.</summary>
        Expired,
        /// <summary>Withdrawn by a sign-out, or by an administrator revoking every session.</summary>
        Revoked,
        /// <summary>
        /// Already exchanged once. A token is spent the moment it is used,
        /// so a second presentation means two parties hold it — the
        /// legitimate client and whoever copied it — and the account's
        /// tokens are all ended rather than guess which is which.
        /// </summary>
        Reused,
    }

    public static Presented Classify(RefreshToken row, Guid currentStamp, DateTimeOffset nowUtc)
    {
        if (row.RevokedAtUtc is not null) return Presented.Revoked;
        if (row.UsedAtUtc is not null) return Presented.Reused;
        if (row.ExpiresAtUtc <= nowUtc || row.SessionStamp != currentStamp) return Presented.Expired;
        return Presented.Valid;
    }

    /// <summary>
    /// A new row for this account, good for <paramref name="lifetime"/>, and
    /// the token it stands for — kept only here, never on the row.
    /// </summary>
    public static (RefreshToken Row, string Token) Issue(User user, DateTimeOffset nowUtc, TimeSpan lifetime)
    {
        var token = NewToken();
        return (new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = Hash(token),
            SessionStamp = user.SessionStamp,
            IssuedAtUtc = nowUtc,
            ExpiresAtUtc = nowUtc + lifetime,
        }, token);
    }
}
