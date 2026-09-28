namespace WinnersPortal.Domain;

public static class Roles
{
    public const string Admin = "admin";
    public const string Client = "client";
    public const string Freelancer = "freelancer";
}

/// <summary>
/// Everyone who signs in: the administrator the setup wizard creates, the
/// clients who post opportunities, and the freelancers who enter them. The role is
/// chosen at registration and does not change — a person who is both hires
/// with one account and builds with another.
/// </summary>
public sealed class User
{
    // Column limits: the one place a length is stated; the rules and the
    // DbContext both read it here.
    public const int MaxPhoneLength = 16;
    public const int MaxLockReason = 200;

    public Guid Id { get; set; }

    /// <summary>Stored lower-cased; unique.</summary>
    public required string Email { get; set; }

    public required string DisplayName { get; set; }

    /// <summary>
    /// When the picture beside the name was last set; null for an account
    /// without one. The picture itself is a row of its own (UserAvatar) so
    /// it never rides along with the account — this stamp is what every
    /// place that shows a name carries, and it versions the picture's URL.
    /// </summary>
    public DateTimeOffset? AvatarUpdatedAtUtc { get; set; }

    public required string PasswordHash { get; set; }

    /// <summary>One of <see cref="Roles"/>.</summary>
    public required string Role { get; set; }

    // ---- forgot password ---------------------------------------------
    // The emailed link carries the token; the row holds only its SHA-256,
    // so a read of the database cannot mint a working link. One token per
    // account — a new request replaces the old — and a completed reset
    // clears both columns, which is what makes the link single-use.

    public string? PasswordResetTokenHash { get; set; }
    public DateTimeOffset? PasswordResetExpiresUtc { get; set; }

    // ---- confirmed contact ---------------------------------------------
    // Registration proves the person can be reached before the account can
    // do anything: a code to the email, another to the phone where the
    // portal can text, and whichever comes back stamps its column. Both
    // null is an account still at the door (Auth/Confirmation.cs). Accounts
    // an administrator or the wizard made, and invitations used, are
    // stamped without a code — somebody vouched.

    /// <summary>International form, digits behind a plus; null when none was given.</summary>
    public string? Phone { get; set; }

    public DateTimeOffset? EmailConfirmedAtUtc { get; set; }
    public DateTimeOffset? PhoneConfirmedAtUtc { get; set; }

    // The standing codes, hashed, one per channel — null where none went
    // out. Issued together; the lifetime and the resend floor both read
    // off the one issue time, and the attempt count is what actually guards
    // a six-digit code.
    public string? EmailCodeHash { get; set; }
    public string? PhoneCodeHash { get; set; }
    public DateTimeOffset? CodeIssuedAtUtc { get; set; }
    public int CodeAttempts { get; set; }

    // ---- identity verification ------------------------------------------
    // Stamped when the provider approves the person behind the account,
    // cleared when a later verdict or an administrator takes it back. The
    // one column the three doors read (Identity/IdentityRules.cs); the
    // session and the verdict's detail live on IdentityVerification.
    public DateTimeOffset? IdentityVerifiedAtUtc { get; set; }

    // ---- administration ------------------------------------------------
    // An administrator's hand on the account. A lock keeps the person out
    // without touching anything they own; an erasure is what "delete" means
    // for an account that opportunities, entries or ratings still point at -- the
    // person's details go, the records stay. Both are checked on every
    // request the cookie makes, not only at the next sign-in.

    public DateTimeOffset? LockedAtUtc { get; set; }

    /// <summary>Why, for the administrator's own reference; never shown to the person.</summary>
    public string? LockReason { get; set; }

    public DateTimeOffset? ErasedAtUtc { get; set; }

    /// <summary>
    /// Rides in the sign-in cookie and is compared on every request. A new
    /// value signs every session of this account out at once -- the effect
    /// of an administrator setting the password, or of a reset link used.
    /// </summary>
    public Guid SessionStamp { get; set; } = Guid.NewGuid();

    // ---- connected GitHub identity ------------------------------------
    // Collected by the App's user-authorisation flow, never typed: a typo is
    // invisible until a transfer fails, and logins are mutable. The numeric
    // id never changes; the login is a display cache refreshed on connect.

    public long? GithubUserId { get; set; }
    public string? GithubLogin { get; set; }
    public DateTimeOffset? GithubConnectedAtUtc { get; set; }

    /// <summary>
    /// The user's chosen accent colour (#RRGGBB, uppercase), or null for the
    /// portal default. Colour only — light/dark mode is a per-device
    /// preference and deliberately never stored here.
    /// </summary>
    public string? ThemeAccent { get; set; }

    /// <summary>
    /// The user's badge colour per opportunity status, as compact JSON
    /// ({"draft":"#RRGGBB",...}) holding only the statuses they changed, or
    /// null for the stock set. The accent's rule applies: colour only.
    /// </summary>
    public string? ThemeStatusColours { get; set; }

    // ---- terms of service ----------------------------------------------
    // Which version of the terms this account accepted, and when. A fact
    // about the account, not the browser, so it follows the person across
    // devices. Compared numerically with legal.termsVersion: a raised
    // version makes the next visit a prompt, a lowered one undoes nothing,
    // and null is an account older than the terms, which owes the same.

    public int? AcceptedTermsVersion { get; set; }
    public DateTimeOffset? AcceptedTermsAtUtc { get; set; }

    // ---- notifications — what to hear about, and how -------------------
    // Opt-in: both topics start off, so nobody is mailed about opportunities
    // they never asked to follow. Email is a channel preference, on by
    // default so that ticking a topic is enough to start hearing; push is
    // a property of a browser and lives per device in PushDevice.

    /// <summary>Tell me when an opportunity opens for entry.</summary>
    public bool NotifyNewOpportunities { get; set; }

    /// <summary>Tell me when a winner is announced.</summary>
    public bool NotifyWinners { get; set; }

    /// <summary>…by email. The one-click unsubscribe in every such email clears this.</summary>
    public bool NotifyByEmail { get; set; } = true;

    // ---- trust counters — maintained on write, shown on every feed card --
    // The counterweight to "no deposit, open entry" has to render per card,
    // and per-card subqueries against Awards and Ratings are the feed's
    // scaling cliff. Recomputed from truth by Recount at every write site.

    /// <summary>Awards this user paid as a client. Written when a payment is confirmed.</summary>
    public int AwardsPaidCount { get; set; }

    /// <summary>Ratings *of* this user, either role. Written at the rating upsert.</summary>
    public int RatingCount { get; set; }

    /// <summary>Sum of those stars — an integer, so the average never rots in
    /// a stored float; cards divide at read time.</summary>
    public int RatingSum { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }
}
