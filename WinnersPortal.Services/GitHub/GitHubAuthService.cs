using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using WinnersPortal.Domain;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Services.Auth;
using WinnersPortal.Services.Common;
using WinnersPortal.Services.Settings;

namespace WinnersPortal.Services.GitHub;

/// <summary>
/// "Connect GitHub" — the App's user-authorisation flow. The portal stores
/// the account's numeric id (which never changes) and treats the login as a
/// display cache. Typed usernames are deliberately not accepted for clients:
/// a typo is invisible until the repository transfer fails, which is after
/// the client has paid.
/// </summary>
public sealed class GitHubAuthService(GitHubService github, IDataProtectionProvider dataProtection, AppDbContext db, ILoggerFactory logFactory)
{
    private const string ProtectorPurpose = "WinnersPortal.GitHubOAuthState";
    private static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(10);

    public async Task<Outcome> ConnectAsync(string? next, ClaimsPrincipal principal, CancellationToken ct)
    {
        // Where to come back to. Connecting is started from four screens
        // now, and landing all of them on the client's opportunity list was
        // wrong for three of them.
        var back = SafeNext(next);
        // The first setup switched on with a client pair — and the state
        // remembers which, because the code GitHub sends back only
        // redeems against the client that asked for it.
        var setup = await github.ConnectSetupAsync(ct);
        var clientId = setup?.Get("github.clientId");
        if (setup is null || string.IsNullOrWhiteSpace(clientId))
            return Outcome.Redirect((back ?? Landing(principal)) + "?github=unconfigured");

        var state = CreateState(dataProtection, Principal.UserId(principal)!.Value, DateTimeOffset.UtcNow, back, setup.Id);
        return Outcome.Redirect(
            $"https://github.com/login/oauth/authorize?client_id={Uri.EscapeDataString(clientId)}&state={Uri.EscapeDataString(state)}");
    }

    public async Task<Outcome> CallbackAsync(string? code, string? state, ClaimsPrincipal principal, CancellationToken ct)
    {
        var log = logFactory.CreateLogger("GitHubConnect");
        var userId = Principal.UserId(principal)!.Value;

        var parsed = ParseState(dataProtection, state);
        if (parsed is not { } from || from.UserId != userId || string.IsNullOrEmpty(code))
        {
            log.LogWarning("GitHub connect callback rejected: bad or expired state.");
            return Outcome.Redirect(Landing(principal) + "?github=error");
        }
        var back = from.Next ?? Landing(principal);

        try
        {
            var (githubId, login) = await github.ExchangeUserCodeAsync(code, from.Setup, ct);
            var account = await db.Users.SingleAsync(u => u.Id == userId, ct);
            account.GithubUserId = githubId;
            account.GithubLogin = login;
            account.GithubConnectedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            log.LogInformation("User {UserId} connected GitHub account {Login} ({GithubId}).",
                userId, login, githubId);
            return Outcome.Redirect(back + "?github=connected");
        }
        catch (GitHubApiException e)
        {
            log.LogWarning(e, "GitHub connect code exchange failed.");
            return Outcome.Redirect(back + "?github=error");
        }
    }

    private static string Landing(ClaimsPrincipal user) =>
        user.IsInRole(Roles.Client) ? "/client/opportunities" : "/";

    /// <summary>Who started the connect, the screen they started it from, and the GitHub setup it went through.</summary>
    public sealed record ConnectState(Guid UserId, string? Next, string? Setup = null);

    /// <summary>Unprotects the state — null when forged, truncated, or older than 10 minutes.</summary>
    public static ConnectState? ParseState(IDataProtectionProvider dataProtection, string? state)
    {
        if (string.IsNullOrEmpty(state)) return null;
        try
        {
            // Neither the path (SafeNext) nor a setup id has a colon in it.
            var parts = dataProtection.CreateProtector(ProtectorPurpose).Unprotect(state).Split(':', 4);
            if (parts.Length is not (2 or 3 or 4)) return null;
            var issuedAt = new DateTimeOffset(long.Parse(parts[1]), TimeSpan.Zero);
            if (DateTimeOffset.UtcNow - issuedAt > StateLifetime) return null;
            // Sealed inside the token, but checked again on the way out: the
            // rule for where the portal will send a browser lives in one place.
            return new(
                Guid.Parse(parts[0]),
                parts.Length >= 3 ? SafeNext(parts[2]) : null,
                parts.Length == 4 && Setups.IsValidId(parts[3]) ? parts[3] : null);
        }
        catch (Exception)
        {
            return null; // tampered, truncated, or protected with rotated-away keys
        }
    }

    /// <summary>Creates a state token; exposed for the round-trip test.</summary>
    public static string CreateState(
        IDataProtectionProvider dataProtection, Guid userId, DateTimeOffset now, string? next = null, string? setup = null) =>
        dataProtection.CreateProtector(ProtectorPurpose)
            .Protect($"{userId}:{now.UtcTicks}:{SafeNext(next)}:{(Setups.IsValidId(setup) ? setup : "")}");

    /// <summary>
    /// A path on this portal, or nothing. Deliberately a small allow-list
    /// rather than a blocklist: this value ends up in a Location header, and
    /// "//evil.example" is a path by every loose reading of the word. No
    /// query or fragment either, so the caller can append ?github=… safely.
    /// </summary>
    public static string? SafeNext(string? next) =>
        !string.IsNullOrEmpty(next)
        && next.Length <= 200
        && next[0] == '/'
        && (next.Length == 1 || next[1] != '/')
        && !next.Contains("..")
        && next.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '-' or '_' or '.' or '~')
            ? next
            : null;
}
