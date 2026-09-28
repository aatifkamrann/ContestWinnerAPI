using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using WinnersPortal.Domain;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Services.Activity;
using WinnersPortal.Services.Common;
using WinnersPortal.Services.Email;
using WinnersPortal.Services.GitHub;
using WinnersPortal.Services.Phone;
using WinnersPortal.Services.Profiles;
using WinnersPortal.Services.Identity;
using IdentityOptions = WinnersPortal.Services.Identity.IdentityOptions;
using WinnersPortal.Services.Settings;

namespace WinnersPortal.Services.Auth;

public sealed class AuthService(AppDbContext db, TokenService tokens, SettingsService settings, IHttpClientFactory httpFactory, EmailWorkSignal emailSignal, PhoneSender phones, ActivityNote activity, GitHubService github, IdentityOptions identity)
{
    private static readonly PasswordHasher<User> Hasher = new();

    private const string BadResetLink =
        "This reset link is invalid or has expired — request a new one.";

    private const string WrongCurrentPassword = "That is not your current password.";

    private const string SamePassword = "That is the password you have now. Choose a different one.";

    public async Task<Outcome<SessionUser>> RegisterAsync(RegisterRequest request, CancellationToken ct)
    {
        var role = request.Role?.Trim().ToLowerInvariant();
        if (role is not (Roles.Client or Roles.Freelancer))
            return Outcome.Invalid("Role must be 'client' or 'freelancer'.");

        // Registration is one of the two doors bots walk through (entry
        // is the other) — the challenge exists only once the operator
        // configures the Turnstile key pair.
        if (await Captcha.RequiredAsync(settings, ct)
            && !await Captcha.VerifyAsync(httpFactory, settings, request.CaptchaToken, ct))
            return Outcome.Invalid(
                "The captcha could not be verified — complete the challenge and try again.");

        var email = request.Email?.Trim().ToLowerInvariant() ?? "";
        if (!email.Contains('@'))
            return Outcome.Invalid("A valid email address is required.");
        if (!PasswordRules.Acceptable(request.Password))
            return Outcome.Invalid(PasswordRules.Error);
        var (displayName, nameProblem) = AccountRules.CleanDisplayName(request.DisplayName);
        if (nameProblem is not null) return Outcome.Invalid(nameProblem);
        var (phone, phoneProblem) = Confirmation.CleanPhone(request.Phone);
        if (phoneProblem is not null) return Outcome.Invalid(phoneProblem);

        // Terms, when the portal has any, are accepted at the door and
        // recorded against the version that stands at the time.
        var terms = await Terms.CurrentAsync(settings, ct);
        if (terms.Exist && !request.AcceptTerms)
            return Outcome.Invalid("Accept the terms of service to create an account.");

        if (await db.Users.AnyAsync(u => u.Email == email, ct))
            return Outcome.Conflict("An account with this email already exists.");

        var now = DateTimeOffset.UtcNow;
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            DisplayName = displayName,
            PasswordHash = "",
            Role = role,
            Phone = phone,
            AcceptedTermsVersion = terms.Exist ? terms.Version : null,
            AcceptedTermsAtUtc = terms.Exist ? now : null,
            CreatedAtUtc = now,
        };
        user.PasswordHash = Hasher.HashPassword(user, request.Password!);
        db.Users.Add(user);
        // The account is created at the door, unconfirmed, and signed
        // in: the gate (Confirmation.cs) lets that session do nothing
        // but enter a code. The welcome waits for the code — an address
        // nobody has proved is not one to greet.
        var portalName = await settings.GetAsync("branding.portalName", ct) ?? "Winners Portal";
        var sent = await IssueCodesAsync(db, user, portalName, phones, now, ct);
        // The code email rides the same save as the account: a row that
        // hits the unique index on a photo-finish double submit takes
        // its email down with it.
        await db.SaveChangesAsync(ct);
        emailSignal.Wake();

        activity.UserId = user.Id; // the log's row is theirs, cookie or not
        return Outcome.Ok(Me(user, sent)).WithSignIn(user, persistent: false);
    }

    public async Task<Outcome<SessionUser>> ConfirmAsync(ConfirmRequest request, ClaimsPrincipal principal, CancellationToken ct)
    {
        var id = Principal.UserId(principal)!.Value;
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == id, ct);
        if (user is null) return Outcome.Unauthorized();
        if (Confirmation.IsConfirmed(user)) return Outcome.Ok(Me(user));

        var now = DateTimeOffset.UtcNow;
        var match = Confirmation.Verify(
            user.EmailCodeHash, user.PhoneCodeHash, user.CodeIssuedAtUtc, user.CodeAttempts,
            request.Code, now);
        switch (match)
        {
            case Confirmation.Match.Expired:
                return Outcome.Invalid(new CodeErrorResponse { Error = "That code has expired — send a new one.", Expired = true });
            case Confirmation.Match.Exhausted:
                return Outcome.Invalid(new CodeErrorResponse { Error = "Too many wrong codes — send a new one.", Expired = true });
            case Confirmation.Match.None:
                user.CodeAttempts++;
                await db.SaveChangesAsync(ct);
                await Task.Delay(500, ct); // a guess at a code is a guess at a secret — same damper as login
                var left = Confirmation.MaxAttempts - user.CodeAttempts;
                return Outcome.Invalid(new CodeErrorResponse
                {
                    Error = left > 0
                        ? $"That code is not right. {left} {(left == 1 ? "try" : "tries")} left."
                        : "That code is not right, and that was the last try — send a new one.",
                    Expired = left <= 0,
                });
        }

        if (match == Confirmation.Match.Email) user.EmailConfirmedAtUtc = now;
        else user.PhoneConfirmedAtUtc = now;
        user.EmailCodeHash = null;
        user.PhoneCodeHash = null;
        user.CodeIssuedAtUtc = null;
        user.CodeAttempts = 0;
        // Now the welcome: the account is a member's from this save on.
        var portalName = await settings.GetAsync("branding.portalName", ct) ?? "Winners Portal";
        Notify.Queue(db, user, "welcome", Emails.Welcome(portalName, user.Role));
        await db.SaveChangesAsync(ct);
        emailSignal.Wake();
        return Outcome.Ok(Me(user));
    }

    public async Task<Outcome<CodeSentResponse>> ResendCodeAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        var id = Principal.UserId(principal)!.Value;
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == id, ct);
        if (user is null) return Outcome.Unauthorized();
        if (Confirmation.IsConfirmed(user))
            return Outcome.Invalid("This account is already confirmed.");

        var now = DateTimeOffset.UtcNow;
        if (Confirmation.TooSoonToResend(user.CodeIssuedAtUtc, now))
        {
            var wait = Confirmation.RetryAfterSeconds(user.CodeIssuedAtUtc, now);
            return Outcome.TooManyRequests(new RetryLaterResponse
            {
                Error = $"A code went out less than a minute ago — try again in {wait} seconds.",
                RetryAfterSeconds = wait,
            });
        }

        var portalName = await settings.GetAsync("branding.portalName", ct) ?? "Winners Portal";
        var sent = await IssueCodesAsync(db, user, portalName, phones, now, ct);
        await db.SaveChangesAsync(ct);
        emailSignal.Wake();
        return Outcome.Ok(new CodeSentResponse { SentTo = sent.SentTo, PhoneFailed = sent.PhoneFailed });
    }

    public async Task<Outcome<SessionUser>> LoginAsync(LoginRequest request, CancellationToken ct)
    {
        var email = request.Email?.Trim().ToLowerInvariant() ?? "";
        var user = await db.Users.SingleOrDefaultAsync(u => u.Email == email, ct);
        if (user is null ||
            Hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password ?? "")
                == PasswordVerificationResult.Failed)
        {
            await Task.Delay(500, ct); // blunt damper; real rate limiting ships with the limits group
            return Outcome.Unauthorized();
        }
        // Told apart from a wrong password only once the password is
        // right: the lock is a fact about an account, and this door
        // does not confirm which addresses have one.
        if (!AccountRules.CanSignIn(user))
            return Outcome.Forbidden(AccountRules.LockedMessage);
        // "Keep me signed in" is the difference between a cookie the
        // browser drops when it closes and one it keeps. The token's
        // sliding lifetime (TokenService.SessionLifetime) is the same
        // either way — this decides whether the browser holds on to it,
        // not for how long.
        activity.UserId = user.Id; // the log's row is theirs, cookie or not
        return Outcome.Ok(Me(user)).WithSignIn(user, request.RememberMe);
    }

    public async Task<Outcome<TokenPairResponse>> IssueTokenAsync(LoginRequest request, CancellationToken ct)
    {
        var email = request.Email?.Trim().ToLowerInvariant() ?? "";
        var user = await db.Users.SingleOrDefaultAsync(u => u.Email == email, ct);
        if (user is null ||
            Hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password ?? "")
                == PasswordVerificationResult.Failed)
        {
            await Task.Delay(500, ct); // the login damper, for the same reason
            return Outcome.Unauthorized();
        }
        if (!AccountRules.CanSignIn(user))
            return Outcome.Forbidden(AccountRules.LockedMessage);

        var now = DateTimeOffset.UtcNow;
        var (row, refresh) = RefreshTokens.Issue(user, now, tokens.RefreshLifetime);
        db.RefreshTokens.Add(row);
        await db.SaveChangesAsync(ct);
        activity.UserId = user.Id;
        return Outcome.Ok(TokenPair(tokens, user, now, refresh, row.ExpiresAtUtc));
    }

    public async Task<Outcome<TokenPairResponse>> RefreshTokenAsync(RefreshRequest request, CancellationToken ct)
    {
        var presented = request.RefreshToken?.Trim() ?? "";
        var row = presented.Length == 0
            ? null
            : await db.RefreshTokens.Include(t => t.User)
                .SingleOrDefaultAsync(t => t.TokenHash == RefreshTokens.Hash(presented), ct);
        if (row is null)
        {
            await Task.Delay(500, ct); // a guess at a token is a guess at a secret
            return Outcome.Unauthorized();
        }

        var now = DateTimeOffset.UtcNow;
        var user = row.User;
        switch (RefreshTokens.Classify(row, user.SessionStamp, now))
        {
            case RefreshTokens.Presented.Reused:
                // Whoever copied it and whoever it was issued to are
                // both out; the legitimate client signs in again.
                await db.RefreshTokens
                    .Where(t => t.UserId == row.UserId && t.RevokedAtUtc == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAtUtc, now), ct);
                return Outcome.Unauthorized();
            case RefreshTokens.Presented.Revoked:
            case RefreshTokens.Presented.Expired:
                return Outcome.Unauthorized();
        }
        if (!AccountRules.CanSignIn(user))
            return Outcome.Forbidden(AccountRules.LockedMessage);

        row.UsedAtUtc = now;
        var (next, refresh) = RefreshTokens.Issue(user, now, tokens.RefreshLifetime);
        db.RefreshTokens.Add(next);
        await db.SaveChangesAsync(ct);
        activity.UserId = user.Id;
        return Outcome.Ok(TokenPair(tokens, user, now, refresh, next.ExpiresAtUtc));
    }

    public async Task<Outcome> RevokeTokenAsync(RefreshRequest request, CancellationToken ct)
    {
        var presented = request.RefreshToken?.Trim() ?? "";
        if (presented.Length > 0)
        {
            var hash = RefreshTokens.Hash(presented);
            await db.RefreshTokens
                .Where(t => t.TokenHash == hash && t.RevokedAtUtc == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAtUtc, DateTimeOffset.UtcNow), ct);
        }
        return Outcome.NoContent();
    }

    public async Task<Outcome> ForgotPasswordAsync(ForgotPasswordRequest request, CancellationToken ct)
    {
        var email = request.Email?.Trim().ToLowerInvariant() ?? "";
        var user = await db.Users.SingleOrDefaultAsync(u => u.Email == email, ct);
        var now = DateTimeOffset.UtcNow;
        // The throttle is silent on purpose: a different answer for "too
        // soon" would itself confirm that the account exists.
        // A locked account gets no link either, and just as silently.
        if (user is null || !AccountRules.CanSignIn(user)
            || PasswordReset.TooSoonToReissue(user.PasswordResetExpiresUtc, now))
            return Outcome.NoContent();

        var token = PasswordReset.NewToken();
        user.PasswordResetTokenHash = PasswordReset.Hash(token);
        user.PasswordResetExpiresUtc = now + PasswordReset.Lifetime;
        Notify.Queue(db, user, "password_reset", Emails.PasswordReset(token));
        await db.SaveChangesAsync(ct);
        emailSignal.Wake();
        return Outcome.NoContent();
    }

    public async Task<Outcome<SessionUser>> ResetPasswordAsync(ResetPasswordRequest request, CancellationToken ct)
    {
        if (!PasswordRules.Acceptable(request.Password))
            return Outcome.Invalid(PasswordRules.Error);
        var token = request.Token?.Trim() ?? "";
        if (token.Length == 0)
            return Outcome.Invalid(BadResetLink);

        var hash = PasswordReset.Hash(token);
        var user = await db.Users.SingleOrDefaultAsync(u => u.PasswordResetTokenHash == hash, ct);
        if (user is null || user.PasswordResetExpiresUtc is not { } expires || expires < DateTimeOffset.UtcNow)
        {
            await Task.Delay(500, ct); // a guess at a token is a guess at a secret — same damper as login
            return Outcome.Invalid(BadResetLink);
        }
        // Locked between asking and clicking: the link is real, the door is not.
        if (!AccountRules.CanSignIn(user))
            return Outcome.Forbidden(AccountRules.LockedMessage);

        user.PasswordHash = Hasher.HashPassword(user, request.Password!);
        // A link clicked from the inbox is the inbox proved — this is
        // how an invited account gets its stamp without a code.
        user.EmailConfirmedAtUtc ??= DateTimeOffset.UtcNow;
        // Whoever held the old password holds no session now.
        user.SessionStamp = Guid.NewGuid();
        // Single use: a link that kept working after the password changed
        // would be a link an old inbox could still use.
        user.PasswordResetTokenHash = null;
        user.PasswordResetExpiresUtc = null;
        await db.SaveChangesAsync(ct);

        // They just proved control of the inbox — land them signed in, not
        // on a second form asking for the password they typed a moment ago.
        activity.UserId = user.Id; // the log's row is theirs, cookie or not
        return Outcome.Ok(Me(user)).WithSignIn(user, persistent: false);
    }

    /// <summary>
    /// A signed-in member choosing a new password. The current one is asked
    /// for again, so a session left open on a shared machine cannot be turned
    /// into the account itself; every other session ends, this one is
    /// re-issued, and the inbox hears about it in case it was not them.
    /// </summary>
    public async Task<Outcome> ChangePasswordAsync(ChangePasswordRequest request, ClaimsPrincipal principal, CancellationToken ct)
    {
        var id = Principal.UserId(principal)!.Value;
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == id, ct);
        if (user is null) return Outcome.Unauthorized();

        // Refused as a bad field, not a 401: the session is fine, the page
        // should say which box is wrong rather than sign them out.
        if (Hasher.VerifyHashedPassword(user, user.PasswordHash, request.CurrentPassword ?? "")
                == PasswordVerificationResult.Failed)
        {
            await Task.Delay(500, ct); // the login damper: this is a password guess too
            return Outcome.Invalid(WrongCurrentPassword);
        }
        if (!PasswordRules.Acceptable(request.NewPassword))
            return Outcome.Invalid(PasswordRules.Error);
        if (Hasher.VerifyHashedPassword(user, user.PasswordHash, request.NewPassword!) != PasswordVerificationResult.Failed)
            return Outcome.Invalid(SamePassword);

        user.PasswordHash = Hasher.HashPassword(user, request.NewPassword!);
        // A standing reset link would be a second, older way in.
        user.PasswordResetTokenHash = null;
        user.PasswordResetExpiresUtc = null;
        // Whoever held the old password holds no session now.
        user.SessionStamp = Guid.NewGuid();
        Notify.Queue(db, user, "password_changed", Emails.PasswordChanged());
        await db.SaveChangesAsync(ct);
        emailSignal.Wake();

        // Except this one: re-issued with the new stamp, not ended mid-click.
        return Outcome.NoContent().WithRefreshed(user);
    }

    public async Task<Outcome<ThemeResponse>> ThemeAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        var id = Principal.UserId(principal)!.Value;
        var row = await db.Users.AsNoTracking()
            .Where(u => u.Id == id)
            .Select(u => new { u.ThemeAccent, u.ThemeStatusColours })
            .SingleOrDefaultAsync(ct);
        return Outcome.Ok(new ThemeResponse { Color = row?.ThemeAccent, Status = UserTheme.ParseStatus(row?.ThemeStatusColours) });
    }

    public async Task<Outcome<ThemeResponse>> SaveThemeAsync(UserThemeRequest request, ClaimsPrincipal principal, CancellationToken ct)
    {
        var (ok, normalized) = UserTheme.Normalize(request.Color);
        if (!ok) return Outcome.Invalid("Color must be #RRGGBB, or null for the default.");
        var (statusOk, statusJson) = UserTheme.NormalizeStatus(request.Status);
        if (!statusOk)
            return Outcome.Invalid("Status colours map draft, open, reviewing, awarded or cancelled to #RRGGBB.");

        var id = Principal.UserId(principal)!.Value;
        var account = await db.Users.SingleOrDefaultAsync(u => u.Id == id, ct);
        if (account is null) return Outcome.Unauthorized();
        account.ThemeAccent = normalized;
        account.ThemeStatusColours = statusJson;
        await db.SaveChangesAsync(ct);
        return Outcome.Ok(new ThemeResponse { Color = normalized, Status = UserTheme.ParseStatus(statusJson) });
    }

    public async Task<Outcome<MeResponse>> MeAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        var id = Principal.UserId(principal)!.Value;
        var row = await db.Users.AsNoTracking()
            .Where(u => u.Id == id)
            .Select(u => new
            {
                u.Id, u.Email, u.DisplayName, u.Role, u.GithubLogin, u.AcceptedTermsVersion, u.AvatarUpdatedAtUtc,
                u.Phone, u.EmailConfirmedAtUtc, u.PhoneConfirmedAtUtc, u.EmailCodeHash, u.PhoneCodeHash,
                u.CodeIssuedAtUtc, u.IdentityVerifiedAtUtc,
            })
            .SingleOrDefaultAsync(ct);
        if (row is null) return Outcome.Unauthorized(); // cookie outlived the account

        // Whether an acceptance is owed rides along with identity, so
        // the shell raises the prompt on whatever page the person lands.
        var (termsExist, termsVersion) = await Terms.CurrentAsync(settings, ct);
        // Where they stand with the verification provider, and whether a
        // door of theirs asks for it — the shell's cue to invite, never a
        // gate of its own.
        var verification = await db.IdentityVerifications.AsNoTracking()
            .Where(v => v.UserId == id)
            .Select(v => (IdentityStatus?)v.Status)
            .SingleOrDefaultAsync(ct);
        var identityRequired = row.IdentityVerifiedAtUtc is null && await identity.RequiredForRoleAsync(row.Role, ct);
        return Outcome.Ok(new MeResponse
        {
            Id = row.Id,
            Email = row.Email,
            DisplayName = row.DisplayName,
            Role = row.Role,
            GithubLogin = row.GithubLogin,
            // The face beside the name in the header, on every page.
            AvatarUrl = AvatarRules.Url(row.Id, row.AvatarUpdatedAtUtc),
            // Whether connecting is possible on this portal at all, so a
            // page can hold back the invitation instead of bouncing it.
            GithubConfigured = await github.IsConfiguredAsync(ct),
            AcceptedTermsVersion = row.AcceptedTermsVersion,
            TermsPending = Terms.Pending(row.AcceptedTermsVersion, termsVersion, termsExist),
            // Whether the account is still at the door, and where its
            // codes went — a hash standing for a channel means one did.
            // Masked, so the screen can say "check b•••@x.com" without
            // the address itself sitting in a page anyone can read over
            // a shoulder.
            ConfirmationPending = row.EmailConfirmedAtUtc is null && row.PhoneConfirmedAtUtc is null,
            IdentityStatus = row.IdentityVerifiedAtUtc is not null ? "approved" : IdentityRules.StatusName(verification),
            IdentityRequired = identityRequired,
            Phone = row.Phone,
            CodeSentTo = new CodeDestination
            {
                Email = row.EmailCodeHash is null ? null : Confirmation.MaskEmail(row.Email),
                Phone = row.PhoneCodeHash is null || row.Phone is null ? null : Confirmation.MaskPhone(row.Phone),
                // When the standing code stops working, so the screen can
                // count it down rather than repeat "fifteen minutes" to
                // somebody whose fifteen minutes went days ago. Null when
                // no code stands: there is nothing to count.
                ExpiresAtUtc = row.EmailCodeHash is null && row.PhoneCodeHash is null
                    ? null
                    : Confirmation.ExpiresUtc(row.CodeIssuedAtUtc),
            },
        });
    }

    public async Task<Outcome<AcceptTermsResponse>> AcceptTermsAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        var (termsExist, termsVersion) = await Terms.CurrentAsync(settings, ct);
        if (!termsExist)
            return Outcome.Invalid("This portal has no terms of service to accept.");

        var id = Principal.UserId(principal)!.Value;
        var account = await db.Users.SingleOrDefaultAsync(u => u.Id == id, ct);
        if (account is null) return Outcome.Unauthorized();
        if ((account.AcceptedTermsVersion ?? 0) < termsVersion)
        {
            account.AcceptedTermsVersion = termsVersion;
            account.AcceptedTermsAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        return Outcome.Ok(new AcceptTermsResponse { AcceptedTermsVersion = account.AcceptedTermsVersion, TermsPending = false });
    }

    /// <summary>The bearer client's answer: the two tokens, when each stops working, and who they are for.</summary>
    private static TokenPairResponse TokenPair(TokenService tokens, User user, DateTimeOffset now, string refresh, DateTimeOffset refreshExpires) => new TokenPairResponse
    {
        TokenType = "Bearer",
        AccessToken = tokens.Issue(Principal.Identity(user), now, tokens.AccessLifetime),
        ExpiresInSeconds = (int)tokens.AccessLifetime.TotalSeconds,
        ExpiresAtUtc = now + tokens.AccessLifetime,
        RefreshToken = refresh,
        RefreshExpiresAtUtc = refreshExpires,
        User = Me(user),
    };

    private static SessionUser Me(User user, CodesSent? sent = null) => new SessionUser
    {
        Id = user.Id,
        Email = user.Email,
        DisplayName = user.DisplayName,
        Role = user.Role,
        AvatarUrl = AvatarRules.Url(user.Id, user.AvatarUpdatedAtUtc),
        ConfirmationPending = !Confirmation.IsConfirmed(user),
        SentTo = sent?.SentTo,
        PhoneFailed = sent?.PhoneFailed,
    };

    /// <summary>Where the codes went, masked for the screen; and whether a text was tried and failed.</summary>
    private sealed record CodesSent(CodeDestination SentTo, bool PhoneFailed);

    /// <summary>
    /// A fresh pair of codes on the row: the email one queued in the
    /// caller's unit of work, the phone one texted now — and its hash kept
    /// only if the gateway took the message, so the row never claims a code
    /// went somewhere it did not. The caller saves.
    /// </summary>
    private static async Task<CodesSent> IssueCodesAsync(
        AppDbContext db, User user, string portalName, PhoneSender phones, DateTimeOffset now, CancellationToken ct)
    {
        var emailCode = Confirmation.NewCode();
        user.EmailCodeHash = Confirmation.Hash(emailCode);
        user.PhoneCodeHash = null;
        user.CodeIssuedAtUtc = now;
        user.CodeAttempts = 0;
        Notify.Queue(db, user, "confirm_code", Emails.ConfirmationCode(portalName, emailCode));

        var phoneFailed = false;
        if (user.Phone is not null && await phones.CanSendAsync(ct))
        {
            var phoneCode = Confirmation.NewCode();
            var (ok, _) = await phones.SendAsync(user.Phone, Confirmation.Text(portalName, phoneCode), ct);
            if (ok) user.PhoneCodeHash = Confirmation.Hash(phoneCode);
            phoneFailed = !ok;
        }
        return new CodesSent(
            new CodeDestination
            {
                Email = Confirmation.MaskEmail(user.Email),
                Phone = user.PhoneCodeHash is null ? null : Confirmation.MaskPhone(user.Phone!),
                ExpiresAtUtc = Confirmation.ExpiresUtc(now),
            },
            phoneFailed);
    }
}

public sealed record LoginRequest(string? Email, string? Password, bool RememberMe = false);

public sealed record RegisterRequest(
    string? Email, string? Password, string? DisplayName, string? Role, string? CaptchaToken = null,
    bool AcceptTerms = false, string? Phone = null);

public sealed record ConfirmRequest(string? Code);

public sealed record ForgotPasswordRequest(string? Email);

public sealed record ResetPasswordRequest(string? Token, string? Password);

public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

public sealed record UserThemeRequest(string? Color, Dictionary<string, string?>? Status = null);

public sealed record RefreshRequest(string? RefreshToken);
