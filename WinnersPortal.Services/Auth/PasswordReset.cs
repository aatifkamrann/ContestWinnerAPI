using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace WinnersPortal.Services.Auth;

/// <summary>
/// The forgot-password token: 256 random bits, sent once by email and kept on
/// the user row only as a SHA-256 hash — so a read of the database cannot
/// yield a working link. One token per account; a new request replaces the
/// old one, and a successful reset clears it.
/// </summary>
public static class PasswordReset
{
    /// <summary>
    /// How long a link works. Long enough for a slow mail hop, short enough
    /// that a forgotten inbox is not a standing door into the account.
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    /// <summary>
    /// How long an invitation link works, and longer for the opposite
    /// reason: nobody asked for this one, so it has to survive a weekend
    /// and a working day. It is not a second door standing open either —
    /// until it is used, the account it belongs to has no other way in.
    /// </summary>
    public static readonly TimeSpan InvitationLifetime = TimeSpan.FromDays(7);

    /// <summary>
    /// The floor between two emails to the same address. The forgot form is
    /// open to anyone, so without this it would be a way to flood somebody
    /// else's inbox at one request per click.
    /// </summary>
    public static readonly TimeSpan ReissueInterval = TimeSpan.FromMinutes(1);

    /// <summary>Base64url, unpadded: the token rides in a query string and must survive one unescaped.</summary>
    public static string NewToken() => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    /// <summary>
    /// Was the standing token issued too recently to replace? Pure, so the
    /// throttle is testable. Derived from the expiry rather than stored,
    /// because a second column would be a second thing to keep in step.
    ///
    /// The first clause is what makes that derivation safe now that two
    /// lifetimes exist: an expiry further out than a reset could reach is
    /// an invitation, not a reset sent seconds ago, and reading it as one
    /// would silently refuse the invited person a link for a whole week.
    /// </summary>
    public static bool TooSoonToReissue(DateTimeOffset? existingExpiresUtc, DateTimeOffset nowUtc) =>
        existingExpiresUtc is { } expires
        && expires <= nowUtc + Lifetime
        && expires - Lifetime > nowUtc - ReissueInterval;
}
