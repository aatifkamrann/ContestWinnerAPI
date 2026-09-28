using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using WinnersPortal.Domain;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Services.Auth;
using WinnersPortal.Services.Common;
using WinnersPortal.Services.Opportunities;
using WinnersPortal.Services.Email;
using WinnersPortal.Services.GitHub;
using WinnersPortal.Services.Identity;
using WinnersPortal.Services.Live;
using WinnersPortal.Services.Profiles;
using WinnersPortal.Services.Settings;

namespace WinnersPortal.Services.Admin;

/// <summary>
/// The administrator's hand on other people's accounts: who is here, a lock
/// that keeps someone out without touching what they own, a way back in
/// when a password is lost, a corrected name or address, and the one
/// irreversible act — deletion, which removes an account nothing points at
/// and erases in place one that opportunities, entries or ratings still do.
///
/// Every session-ending change here takes effect on the person's very next
/// request (see <see cref="SessionValidation"/>), not at their next sign-in.
/// </summary>
public sealed class UserAdminService(AppDbContext db, SettingsService settings, EmailWorkSignal emailSignal, GitHubWorkSignal githubSignal, ILiveBoard live, PublicReads publicReads, IdentityProofWorkSignal proofSignal)
{
    private static readonly PasswordHasher<User> Hasher = new();

    public async Task<Outcome<UserListResponse>> ListAsync(ClaimsPrincipal principal, CancellationToken ct) =>
        Outcome.Ok(new UserListResponse { Users = await RowsAsync(db, Principal.UserId(principal)!.Value, only: null, ct) });

    public async Task<Outcome<CreateUserResponse>> CreateAsync(CreateUserRequest request, ClaimsPrincipal principal, CancellationToken ct)
    {
        var role = request.Role?.Trim().ToLowerInvariant();
        if (AccountRules.RoleProblem(role) is { } roleProblem)
            return Outcome.Invalid(roleProblem);

        // The same two rules registration applies, so an administrator
        // can never create what the person could not have typed.
        var email = request.Email?.Trim().ToLowerInvariant() ?? "";
        if (!email.Contains('@'))
            return Outcome.Invalid("A valid email address is required.");
        var displayName = request.DisplayName?.Trim() ?? "";
        if (displayName.Length is < 2 or > 80)
            return Outcome.Invalid("Display name must be 2–80 characters.");
        // Every row, not just the living ones: an erased account keeps a
        // unique address on a domain nothing delivers to, so this can
        // never collide with one, and the unique index is never raced.
        if (await db.Users.AnyAsync(u => u.Email == email, ct))
            return Outcome.Conflict("An account with this email already exists.");

        var invite = string.IsNullOrEmpty(request.Password);
        if (!invite && !PasswordRules.Acceptable(request.Password))
            return Outcome.Invalid(PasswordRules.Error);
        if (invite && !await EmailSender.IsConfiguredAsync(settings, ct))
            return Outcome.Conflict(
                "Email is not configured on this portal, so no invitation can be sent. "
                    + "Set a password here and pass it on instead.");

        var now = DateTimeOffset.UtcNow;
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            DisplayName = displayName,
            PasswordHash = "",
            Role = role!,
            CreatedAtUtc = now,
            // Deliberately not accepted on their behalf. Consent is the
            // person's to give, and the terms gate asks them for it on
            // their very first request — before they can do anything.
            AcceptedTermsVersion = null,
            AcceptedTermsAtUtc = null,
        };

        if (invite)
        {
            var token = PasswordReset.NewToken();
            user.PasswordResetTokenHash = PasswordReset.Hash(token);
            user.PasswordResetExpiresUtc = now + PasswordReset.InvitationLifetime;
            Notify.Queue(db, user, "account_invitation",
                Emails.AccountInvitation(token, role!, PasswordReset.InvitationLifetime.Days));
        }
        else
        {
            user.PasswordHash = Hasher.HashPassword(user, request.Password!);
            // No code for an account made by hand: the administrator is
            // the one vouching that this address belongs to somebody. An
            // invitation is stamped when its link is used, which proves
            // the inbox the way a code would.
            user.EmailConfirmedAtUtc = now;
        }

        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        if (invite) emailSignal.Wake();

        return Outcome.Ok(new CreateUserResponse
        {
            User = await RowAsync(db, Principal.UserId(principal)!.Value, user.Id, ct),
            Invited = invite,
        });
    }

    public async Task<Outcome<UserRow>> UpdateAsync(Guid id, UpdateUserRequest request, ClaimsPrincipal principal, CancellationToken ct)
    {
        var user = await LiveAsync(db, id, ct);
        if (user is null) return Outcome.NotFound();

        // The same two rules registration applies, so an administrator
        // can never save what the person could not have typed.
        var email = request.Email?.Trim().ToLowerInvariant() ?? "";
        if (!email.Contains('@'))
            return Outcome.Invalid("A valid email address is required.");
        var (displayName, nameProblem) = AccountRules.CleanDisplayName(request.DisplayName);
        if (nameProblem is not null) return Outcome.Invalid(nameProblem);
        if (email != user.Email && await db.Users.AnyAsync(u => u.Email == email && u.Id != id, ct))
            return Outcome.Conflict("Another account already uses this email address.");

        user.Email = email;
        user.DisplayName = displayName;
        await db.SaveChangesAsync(ct);
        return Outcome.Ok((await RowAsync(db, Principal.UserId(principal)!.Value, id, ct))!);
    }

    public async Task<Outcome<UserRow>> LockAsync(Guid id, LockUserRequest request, ClaimsPrincipal principal, CancellationToken ct)
    {
        var user = await LiveAsync(db, id, ct);
        if (user is null) return Outcome.NotFound();
        var meId = Principal.UserId(principal)!.Value;

        var (ok, reason) = AccountRules.CleanLockReason(request.Reason);
        if (!ok)
            return Outcome.Invalid(
                $"Keep the reason under {AccountRules.MaxLockReason} characters — it is a note to yourself.");
        if (AccountRules.RemovalProblem("lock", id == meId, user.Role == Roles.Admin,
                await OtherActiveAdminsAsync(db, id, ct)) is { } problem)
            return Outcome.Conflict(problem);

        // Locking a locked account only rewrites the note.
        user.LockedAtUtc ??= DateTimeOffset.UtcNow;
        user.LockReason = reason;
        await db.SaveChangesAsync(ct);
        publicReads.Clear(); // off the leaderboard and Talent from the next page
        return Outcome.Ok((await RowAsync(db, meId, id, ct))!);
    }

    public async Task<Outcome<UserRow>> UnlockAsync(Guid id, ClaimsPrincipal principal, CancellationToken ct)
    {
        var user = await LiveAsync(db, id, ct);
        if (user is null) return Outcome.NotFound();
        user.LockedAtUtc = null;
        user.LockReason = null;
        await db.SaveChangesAsync(ct);
        publicReads.Clear(); // back on the public lists from the next page
        return Outcome.Ok((await RowAsync(db, Principal.UserId(principal)!.Value, id, ct))!);
    }

    public async Task<Outcome> SetPasswordAsync(Guid id, SetPasswordRequest request, ClaimsPrincipal principal, CancellationToken ct)
    {
        var user = await LiveAsync(db, id, ct);
        if (user is null) return Outcome.NotFound();
        if (!PasswordRules.Acceptable(request.Password))
            return Outcome.Invalid(PasswordRules.Error);

        user.PasswordHash = Hasher.HashPassword(user, request.Password!);
        // A standing reset link would be a second, older way in.
        user.PasswordResetTokenHash = null;
        user.PasswordResetExpiresUtc = null;
        // Whoever held the old password holds no session now.
        user.SessionStamp = Guid.NewGuid();
        await db.SaveChangesAsync(ct);

        // Except the administrator changing their own: this session is
        // re-issued with the new stamp rather than ended mid-click.
        return id == Principal.UserId(principal)
            ? Outcome.NoContent().WithRefreshed(user)
            : Outcome.NoContent();
    }

    public async Task<Outcome<ResetLinkResponse>> SendResetLinkAsync(Guid id, CancellationToken ct)
    {
        var user = await LiveAsync(db, id, ct);
        if (user is null) return Outcome.NotFound();
        if (!AccountRules.CanSignIn(user))
            return Outcome.Conflict(
                "This account is locked — the link would lead to a closed door. Unlock it first.");
        if (!await EmailSender.IsConfiguredAsync(settings, ct))
            return Outcome.Conflict(
                "Email is not configured on this portal, so no link can be sent. "
                    + "Set a password here and pass it on instead.");

        var now = DateTimeOffset.UtcNow;
        var token = PasswordReset.NewToken();
        // An account that has never held a password is being invited,
        // not reset — a first-time reader told "someone asked to reset
        // your password" would reasonably decide this was a phishing
        // mail. This is also how an expired invitation is re-sent.
        var invite = string.IsNullOrEmpty(user.PasswordHash);
        var lifetime = invite ? PasswordReset.InvitationLifetime : PasswordReset.Lifetime;
        user.PasswordResetTokenHash = PasswordReset.Hash(token);
        user.PasswordResetExpiresUtc = now + lifetime;
        Notify.Queue(db, user,
            invite ? "account_invitation" : "password_reset",
            invite
                ? Emails.AccountInvitation(token, user.Role, lifetime.Days)
                : Emails.PasswordReset(token));
        await db.SaveChangesAsync(ct);
        emailSignal.Wake();
        return Outcome.Ok(new ResetLinkResponse
        {
            SentTo = user.Email,
            ExpiresAtUtc = user.PasswordResetExpiresUtc,
            Invited = invite,
        });
    }

    public async Task<Outcome<EraseUserResponse>> EraseAsync(Guid id, ClaimsPrincipal principal, CancellationToken ct)
    {
        var user = await LiveAsync(db, id, ct);
        if (user is null) return Outcome.NotFound();
        var meId = Principal.UserId(principal)!.Value;
        if (AccountRules.RemovalProblem("delete", id == meId, user.Role == Roles.Admin,
                await OtherActiveAdminsAsync(db, id, ct)) is { } problem)
            return Outcome.Conflict(problem);

        var now = DateTimeOffset.UtcNow;
        var opportunities = await db.Opportunities.CountAsync(c => c.ClientId == id, ct);
        var entries = await db.Entries.CountAsync(e => e.FreelancerId == id, ct);
        var ratings = await db.Ratings.CountAsync(r => r.ByUserId == id || r.OfUserId == id, ct);

        // Mail not yet sent to the old address is mail to nobody now;
        // devices are the person's, not the record's. Both go with
        // either outcome, in the same save.
        var oldEmail = user.Email;
        db.EmailMessages.RemoveRange(await db.EmailMessages
            .Where(m => m.ToEmail == oldEmail && m.Status == EmailStatus.Pending)
            .ToListAsync(ct));
        db.PushDevices.RemoveRange(await db.PushDevices.Where(d => d.UserId == id).ToListAsync(ct));
        // The inbox is what the person was told; nobody is left to read it.
        db.Notifications.RemoveRange(await db.Notifications.Where(n => n.UserId == id).ToListAsync(ct));
        // A standing sign-in is the person's too; the stamp roll below
        // would refuse it anyway, but a credential nobody can use is
        // not worth keeping.
        db.RefreshTokens.RemoveRange(await db.RefreshTokens.Where(t => t.UserId == id).ToListAsync(ct));

        // The activity log keeps what the account did; the address and
        // the browser it was done from, any reason they typed, and what a
        // third-party call made on their behalf carried, are the person's,
        // and go with them. Either outcome, so it is done before the two part.
        await db.ActivityEvents.Where(a => a.UserId == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.Ip, (string?)null)
                .SetProperty(a => a.UserAgent, (string?)null)
                .SetProperty(a => a.Detail, (string?)null)
                .SetProperty(a => a.Request, (string?)null)
                .SetProperty(a => a.Response, (string?)null), ct);
        // A withdrawal's reason is theirs too. The entry stays, as a
        // record; what they wrote on the way out does not.
        await db.Entries.Where(e => e.FreelancerId == id && e.WithdrawnReason != null)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.WithdrawnReason, (string?)null), ct);

        if (AccountRules.LeavesNoTrace(opportunities, entries, ratings))
        {
            // Profiles this administrator closed name them; on Postgres the
            // key nulls itself, on SQL Server the nulling is the portal's
            // (AppDbContext, Profile.DeletedByUserId), so it is done here
            // on both — a deletion must not depend on somebody's history.
            await db.Profiles.IgnoreQueryFilters()
                .Where(p => p.DeletedByUserId == id)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.DeletedByUserId, (Guid?)null), ct);
            db.Users.Remove(user);
            await db.SaveChangesAsync(ct);
            // The verification went with the account; its stored document
            // images are left without one, which the proof worker deletes.
            proofSignal.Wake();
            return Outcome.Ok(new EraseUserResponse { Outcome = "deleted", OpportunitiesCancelled = 0, EntriesWithdrawn = 0 });
        }

        // Erase in place. What the person left running is closed on the
        // way out, by the same machinery a client or entrant would use
        // themselves — with the reason every entrant is owed.
        var touched = new List<string>();
        var liveOpportunities = await db.Opportunities
            .Where(c => c.ClientId == id && (c.Status == OpportunityStatus.Open || c.Status == OpportunityStatus.Reviewing))
            .ToListAsync(ct);
        foreach (var opportunity in liveOpportunities)
        {
            await CancelService.CancelAsync(db, opportunity,
                "The client's account was removed by a portal administrator.", ct);
            touched.Add(opportunity.Slug);
        }
        var activeEntries = await db.Entries.Include(e => e.Opportunity)
            .Where(e => e.FreelancerId == id && e.Status == EntryStatus.Active
                && (e.Opportunity!.Status == OpportunityStatus.Open || e.Opportunity.Status == OpportunityStatus.Reviewing))
            .ToListAsync(ct);
        foreach (var entry in activeEntries)
        {
            entry.Status = EntryStatus.Withdrawn;
            entry.WithdrawnAtUtc = now;
            touched.Add(entry.Opportunity!.Slug);
        }
        // The applications those entries were selected from read Withdrawn,
        // as a withdrawal from the opportunity page leaves them.
        var withdrawnIds = activeEntries.Select(e => e.Id).ToList();
        foreach (var application in await db.Applications
                     .Where(a => a.EntryId != null && withdrawnIds.Contains(a.EntryId.Value))
                     .ToListAsync(ct))
        {
            application.Status = ApplicationStatus.Withdrawn;
            application.DecidedAtUtc = now;
        }
        // Applications still waiting on a decision are answered here,
        // so no client is left a row to decide for a person who is gone.
        var openApplications = await db.Applications.Include(a => a.Opportunity)
            .Where(a => a.FreelancerId == id && a.Status == ApplicationStatus.UnderReview)
            .ToListAsync(ct);
        foreach (var application in openApplications)
        {
            application.Status = ApplicationStatus.NotSelected;
            application.DecidedAtUtc = now;
            application.DecidedByUserId = meId;
            touched.Add(application.Opportunity!.Slug);
        }

        // The profile is closed rather than dropped: the portfolio the
        // person wrote — headline, introduction, city, skills, languages
        // and past work — leaves every query the portal makes the moment
        // the row is marked (the filter is in AppDbContext), and the row
        // itself stays to say which administrator did this and when.
        // Payment details are the exception: an account number kept
        // where nobody will read it again is a liability, not an audit,
        // so those rows go for good. The profile row is loaded past its
        // own filter, which is the only place that happens.
        var profile = await db.Profiles.IgnoreQueryFilters()
            .SingleOrDefaultAsync(p => p.UserId == id, ct);
        if (profile is not null) Erasure.MarkProfileDeleted(profile, now, meId);
        db.ProfilePayments.RemoveRange(
            await db.ProfilePayments.Where(x => x.UserId == id).ToListAsync(ct));

        // The picture is the person too — and so are the pictures of
        // their work, which the marked profile would otherwise keep
        // serving to anybody still holding one of their addresses.
        db.UserAvatars.RemoveRange(
            await db.UserAvatars.Where(a => a.UserId == id).ToListAsync(ct));
        db.ProfileImages.RemoveRange(
            await db.ProfileImages.Where(x => x.UserId == id).ToListAsync(ct));
        // Their score by the day, likewise: the leaderboard stops listing an
        // erased account, and a history nothing reads is the person's, not a record.
        await db.MeritSnapshots.Where(s => s.UserId == id).ExecuteDeleteAsync(ct);

        // The verdict stays as the record that a check was passed; the proof
        // behind it — the decision with what it read off the document, and
        // the stored images of the document and the face — is the person,
        // and goes: the decision here, the images by the proof worker.
        var verification = await db.IdentityVerifications.SingleOrDefaultAsync(v => v.UserId == id, ct);
        if (verification is not null) IdentityService.ClearProof(verification);
        await db.IdentityDocuments.Where(d => d.UserId == id && d.RemovedAtUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.RemovedAtUtc, now), ct);

        // And the account itself keeps its id and loses its person.
        Erasure.Scrub(user, now);
        await db.SaveChangesAsync(ct);

        foreach (var opportunityId in activeEntries.Select(e => e.OpportunityId).Distinct())
            await Recount.OpportunityAsync(db, opportunityId, ct); // the card just lost an entrant
        publicReads.Clear(); // the person leaves every public list with the next page
        githubSignal.Wake(); // the worker archives withdrawn and cancelled repos — read-only, never deleted
        emailSignal.Wake();
        proofSignal.Wake(); // the stored identity images go
        foreach (var slug in touched.Distinct())
            await live.OpportunityChangedAsync(slug, ct);
        return Outcome.Ok(new EraseUserResponse
        {
            Outcome = "erased",
            OpportunitiesCancelled = liveOpportunities.Count,
            EntriesWithdrawn = activeEntries.Count,
        });
    }

    /// <summary>An account that still exists as a person. Erased rows are records, not accounts.</summary>
    private static Task<User?> LiveAsync(AppDbContext db, Guid id, CancellationToken ct) =>
        db.Users.SingleOrDefaultAsync(u => u.Id == id && u.ErasedAtUtc == null, ct);

    /// <summary>Administrators other than this one who can still sign in — the guard against locking the last door.</summary>
    private static Task<int> OtherActiveAdminsAsync(AppDbContext db, Guid id, CancellationToken ct) =>
        db.Users.CountAsync(u => u.Role == Roles.Admin && u.Id != id && u.LockedAtUtc == null && u.ErasedAtUtc == null, ct);

    private static async Task<UserRow?> RowAsync(AppDbContext db, Guid meId, Guid id, CancellationToken ct) =>
        (await RowsAsync(db, meId, id, ct)).SingleOrDefault();

    /// <summary>One account's row as the list shows it — what an action on the row answers with.</summary>
    public async Task<Outcome<UserRow>> RowAsync(Guid id, ClaimsPrincipal principal, CancellationToken ct) =>
        await RowAsync(db, Principal.UserId(principal)!.Value, id, ct) is { } row ? Outcome.Ok(row) : Outcome.NotFound();

    /// <summary>
    /// Every living account with the counts the screen decides by: what the
    /// person has on record (which decides whether delete removes or
    /// erases) and what they have running (which the erasure closes). Seven
    /// grouped queries rather than seven per row — a portal's user list is
    /// small, but the screen loads it whole.
    /// </summary>
    private static async Task<List<UserRow>> RowsAsync(AppDbContext db, Guid meId, Guid? only, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var users = await db.Users.AsNoTracking()
            .Where(u => u.ErasedAtUtc == null && (only == null || u.Id == only))
            .OrderByDescending(u => u.CreatedAtUtc)
            .Select(u => new
            {
                u.Id, u.Email, u.DisplayName, u.Role, u.CreatedAtUtc, u.LockedAtUtc, u.LockReason,
                u.GithubLogin, u.PasswordResetExpiresUtc, u.AvatarUpdatedAtUtc, u.IdentityVerifiedAtUtc,
                // As a boolean in the query, so the hash itself never leaves
                // the database to answer a question about whether it exists.
                PasswordSet = u.PasswordHash != "",
            })
            .ToListAsync(ct);

        var opportunities = await CountByAsync(db.Opportunities.Select(c => c.ClientId), ct);
        var liveOpportunities = await CountByAsync(db.Opportunities
            .Where(c => c.Status == OpportunityStatus.Open || c.Status == OpportunityStatus.Reviewing)
            .Select(c => c.ClientId), ct);
        var entries = await CountByAsync(db.Entries.Select(e => e.FreelancerId), ct);
        var activeEntries = await CountByAsync(db.Entries
            .Where(e => e.Status == EntryStatus.Active
                && (e.Opportunity!.Status == OpportunityStatus.Open || e.Opportunity.Status == OpportunityStatus.Reviewing))
            .Select(e => e.FreelancerId), ct);
        var awardsWon = await CountByAsync(db.Awards.Select(a => a.Entry!.FreelancerId), ct);
        var ratingsOf = await CountByAsync(db.Ratings.Select(r => r.OfUserId), ct);
        var ratingsBy = await CountByAsync(db.Ratings.Select(r => r.ByUserId), ct);

        return users.Select(u => new UserRow(
            u.Id, u.Email, u.DisplayName, u.Role, u.CreatedAtUtc, u.LockedAtUtc, u.LockReason, u.GithubLogin,
            AvatarUrl: AvatarRules.Url(u.Id, u.AvatarUpdatedAtUtc),
            ResetPending: u.PasswordResetExpiresUtc is { } expires && expires > now,
            PasswordSet: u.PasswordSet,
            IdentityVerified: u.IdentityVerifiedAtUtc is not null,
            Opportunities: opportunities.GetValueOrDefault(u.Id),
            LiveOpportunities: liveOpportunities.GetValueOrDefault(u.Id),
            Entries: entries.GetValueOrDefault(u.Id),
            ActiveEntries: activeEntries.GetValueOrDefault(u.Id),
            AwardsWon: awardsWon.GetValueOrDefault(u.Id),
            Ratings: ratingsOf.GetValueOrDefault(u.Id) + ratingsBy.GetValueOrDefault(u.Id),
            IsYou: u.Id == meId)).ToList();
    }

    private static async Task<Dictionary<Guid, int>> CountByAsync(IQueryable<Guid> ids, CancellationToken ct) =>
        await ids.GroupBy(id => id)
            .Select(g => new { g.Key, N = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.N, ct);
}

public sealed record UserRow(
    Guid Id,
    string Email,
    string DisplayName,
    string Role,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? LockedAtUtc,
    string? LockReason,
    string? GithubLogin,
    /// <summary>The picture beside the name, where there is one — versioned, so the list may cache it.</summary>
    string? AvatarUrl,
    bool ResetPending,
    /// <summary>
    /// False for an account created by invitation that nobody has claimed.
    /// With <see cref="ResetPending"/> it tells the two apart: a live link
    /// is an invitation in flight, an expired one is a door nobody can open.
    /// </summary>
    bool PasswordSet,
    /// <summary>The provider approved the person behind the account; an administrator may take it back.</summary>
    bool IdentityVerified,
    int Opportunities,
    int LiveOpportunities,
    int Entries,
    int ActiveEntries,
    int AwardsWon,
    int Ratings,
    bool IsYou);

/// <summary>A password of null or empty asks for an invitation instead.</summary>
public sealed record CreateUserRequest(string? DisplayName, string? Email, string? Role, string? Password);

public sealed record UpdateUserRequest(string? DisplayName, string? Email);

public sealed record LockUserRequest(string? Reason);

public sealed record SetPasswordRequest(string? Password);
