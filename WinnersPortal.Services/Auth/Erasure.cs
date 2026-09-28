using WinnersPortal.Domain;

namespace WinnersPortal.Services.Auth;

/// <summary>
/// What is left of somebody after "delete". An account nothing points at is
/// removed outright; one that opportunities, entries or ratings point at keeps
/// its row and loses its person, and this is the line that decides which
/// half of it goes.
///
/// A <b>record</b> is something somebody else can be owed — an opportunity, an
/// entry, an award, a rating. Those stay, credited to a deleted member,
/// because a paid award with no winner on it is a dispute nobody can
/// settle. Everything else is the person's own and goes with them: the
/// account columns cleared here, and the two rows that are theirs rather
/// than the record's — the devices they registered, and the profile they
/// wrote about themselves, which the delete endpoint removes whole.
///
/// The profile is the case worth stating, because it is neither quite. A
/// headline, an introduction, a city and a portfolio are the person's own
/// words about themselves, written for a page that no longer exists once
/// the account is erased — nothing on the portal reads them afterwards, an
/// entrant list, a standing board and a merit score all showing what
/// somebody <i>did</i> here rather than what they said about themselves. So
/// the row is <b>marked deleted, not dropped</b>: a query filter takes it
/// out of every read the portal makes, and what stays behind is an audit —
/// which administrator ended this account, and on which day — for the
/// deletion that turns out to have been the wrong person, or the one
/// somebody later has to answer for.
///
/// One list on it is not kept: payment details go for good with the
/// account. An account number nobody will read again is a liability rather
/// than an audit trail, and knowing that a member <i>had</i> a bank on file
/// is all the audit ever needed.
/// </summary>
public static class Erasure
{
    /// <summary>
    /// Takes the person out of the account row, in place. Pure, so what an
    /// erasure actually clears is a test rather than a reading of the
    /// endpoint: every column touched here is something the person gave the
    /// portal or chose inside it, and none of it is anybody's record.
    ///
    /// What is deliberately left: the role, the join date, the terms they
    /// accepted, the counters their records are summed into, and the id
    /// every one of those records points at.
    /// </summary>
    public static void Scrub(User user, DateTimeOffset now)
    {
        // Unique (the id is in it) and on a domain nothing delivers to, so
        // the row can never collide with a live account or be written to.
        user.Email = AccountRules.ErasedEmail(user.Id);
        user.DisplayName = AccountRules.ErasedName;
        user.PasswordHash = "";
        user.PasswordResetTokenHash = null;
        user.PasswordResetExpiresUtc = null;

        // The other way they could be reached, and the codes that proved
        // either one. A phone number outlives an address by years.
        user.Phone = null;
        user.EmailCodeHash = null;
        user.PhoneCodeHash = null;
        user.CodeIssuedAtUtc = null;
        user.CodeAttempts = 0;

        // The verdict about the person goes with the person; the row that
        // held the provider's session id goes at the call site (cascade).
        user.IdentityVerifiedAtUtc = null;

        user.GithubUserId = null;
        user.GithubLogin = null;
        user.GithubConnectedAtUtc = null;

        user.ThemeAccent = null;
        user.ThemeStatusColours = null;
        // The picture row itself goes at the call site; the stamp that says
        // there is one goes here, so no name projection offers a face that
        // no longer exists.
        user.AvatarUpdatedAtUtc = null;
        user.NotifyNewOpportunities = false;
        user.NotifyWinners = false;
        user.NotifyByEmail = false;

        // Locked as well as erased: the two guards are read at different
        // places, and an erased row must fail both. An account already
        // locked keeps the moment it was, which is the truer date.
        user.LockedAtUtc ??= now;
        user.LockReason = "Deleted";
        user.ErasedAtUtc = now;
        // Every session the account had ends on its next request.
        user.SessionStamp = Guid.NewGuid();
    }

    /// <summary>
    /// Closes the profile of an erased account. The three columns are the
    /// whole audit: that it was deleted, when, and by which administrator —
    /// deletion being the one administrative act with nothing to inspect
    /// afterwards, because the account it was done to no longer answers for
    /// itself.
    ///
    /// From this moment the row is unreadable through the portal: the query
    /// filter on <c>Profile</c> is what enforces that, not a filter each
    /// reader remembers.
    /// </summary>
    public static void MarkProfileDeleted(Profile profile, DateTimeOffset now, Guid byUserId)
    {
        profile.IsDeleted = true;
        profile.DeletedAtUtc = now;
        profile.DeletedByUserId = byUserId;
    }
}
