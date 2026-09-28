using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using WinnersPortal.Domain;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Services.Phone;
using WinnersPortal.Services.Settings;

namespace WinnersPortal.Services.Auth;

/// <summary>
/// The reads the edge makes about a signed-in account before any controller
/// runs: whether its session still stands, whether it owes a confirmation,
/// whether it owes the terms. Each is one narrow query, made only for a
/// signed-in caller the path rules did not already let through; the rules
/// themselves stay in <see cref="AccountRules"/>, <see cref="Confirmation"/>
/// and <see cref="Terms"/>. On SQL Server the three are the procedures
/// <c>Account_Session</c>, <c>Account_Confirmed</c> and
/// <c>Account_AcceptedTerms</c>.
/// </summary>
public sealed class AccountGates(AppDbContext db, SettingsService settings, PhoneSender phones)
{
    /// <summary>
    /// The account a validated token names, while that token may still be
    /// used: the account exists, may sign in, and the token carries its
    /// current session stamp. Null ends the session — a lock, an erasure or
    /// a new stamp takes effect on the next request, not at the token's end.
    /// The procedure reads the whole row into the entity: every column the
    /// table has is a property of the same name, so a column added later
    /// rides along the way it does through EF Core.
    /// </summary>
    public async Task<User?> SessionAccountAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        if (Principal.UserId(principal) is not { } userId) return null;
        var user = db.UseDapper
            ? await db.Sql.SingleOrDefaultAsync<User>(Procedures.AccountSession, new { userId }, ct)
            : await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, ct);
        return user is not null && AccountRules.CanSignIn(user) && Principal.Stamp(principal) == user.SessionStamp
            ? user
            : null;
    }

    internal sealed record ConfirmedRow(DateTimeOffset? EmailConfirmedAtUtc, DateTimeOffset? PhoneConfirmedAtUtc);

    /// <summary>
    /// What an account that has proved neither an email nor a phone is told,
    /// or null when it owes nothing. A portal with no gateway never texted
    /// anybody, so the message does not name a channel it cannot use; that
    /// settings read is cached, and only unconfirmed accounts reach it.
    /// </summary>
    public async Task<string?> ConfirmationOwedAsync(Guid userId, CancellationToken ct)
    {
        var row = db.UseDapper
            ? await db.Sql.SingleOrDefaultAsync<ConfirmedRow>(Procedures.AccountConfirmed, new { userId }, ct)
            : await db.Users.AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => new ConfirmedRow(u.EmailConfirmedAtUtc, u.PhoneConfirmedAtUtc))
                .SingleOrDefaultAsync(ct);
        if (row is null || row.EmailConfirmedAtUtc is not null || row.PhoneConfirmedAtUtc is not null) return null;
        return Confirmation.GateMessage(await phones.CanSendAsync(ct));
    }

    /// <summary>
    /// Whether the account still has to accept the current terms. The
    /// settings read (cached) comes first, so a portal with no terms never
    /// reads the row.
    /// </summary>
    public async Task<bool> TermsOwedAsync(Guid userId, CancellationToken ct)
    {
        var (exist, version) = await Terms.CurrentAsync(settings, ct);
        if (!exist) return false;
        var accepted = db.UseDapper
            ? await db.Sql.SingleOrDefaultAsync<int?>(Procedures.AccountAcceptedTerms, new { userId }, ct)
            : await db.Users.AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => u.AcceptedTermsVersion)
                .SingleOrDefaultAsync(ct);
        return Terms.Pending(accepted, version, termsExist: true);
    }
}
