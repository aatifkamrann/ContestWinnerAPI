namespace WinnersPortal.Domain;

/// <summary>
/// The long-lived half of a bearer sign-in. An access token lasts an hour
/// and is checked from its signature alone; this row is what lets a client
/// that holds no cookie get the next one without the password. The row
/// keeps only a SHA-256 of the token, so a read of the database cannot mint
/// a sign-in. Rotated on every use: the presented token is spent and a new
/// one issued, and a spent token presented again means the token leaked
/// somewhere, which ends every token this account holds.
/// </summary>
public sealed class RefreshToken
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    /// <summary>SHA-256 of the token, as lower-case hex; unique.</summary>
    public required string TokenHash { get; set; }

    /// <summary>
    /// The account's session stamp when the token was issued. A stamp the
    /// row no longer carries — a password change, a lock, an erasure —
    /// refuses the refresh the same way it refuses an open cookie.
    /// </summary>
    public Guid SessionStamp { get; set; }

    public DateTimeOffset IssuedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }

    /// <summary>Set when the token was exchanged for its successor; null while it still stands.</summary>
    public DateTimeOffset? UsedAtUtc { get; set; }

    /// <summary>Set by a sign-out, a reuse, or an administrator; null while it still stands.</summary>
    public DateTimeOffset? RevokedAtUtc { get; set; }
}
