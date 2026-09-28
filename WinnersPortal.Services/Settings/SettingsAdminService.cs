using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Services.Auth;
using WinnersPortal.Services.Common;

namespace WinnersPortal.Services.Settings;

public sealed class SettingsAdminService(SettingsService settings, SetupTestLog tests, AppDbContext db, TokenService tokens, RedisConnection redis)
{
    public async Task<Outcome<SettingsResponse>> ReadAsync(CancellationToken ct)
    {
        var groups = await settings.GetForAdminAsync(ct);
        for (var i = 0; i < groups.Count; i++)
        {
            if (groups[i].Setups is not { } listed || Setups.KindOfGroup(groups[i].Name) is not { } kind)
                continue;
            var values = await settings.SetupsAsync(kind, ct);
            // A store's files and an organization's repositories are
            // counted on the screen, so the reason a setup cannot be
            // removed is in front of the administrator before they try.
            var inUse = kind == Setups.Storage || kind == Setups.GitHub
                ? await SetupUsage.InUseAsync(db, kind, values, ct)
                : new Dictionary<string, string>();
            // And every card wears its last test, so a setup that has
            // never been proved, or has changed since, says so folded.
            var tested = await tests.ReadAsync(kind, values, ct);
            groups[i] = groups[i] with
            {
                Setups = listed
                    .Select(s => s with { InUse = inUse.GetValueOrDefault(s.Id), LastTest = tested.GetValueOrDefault(s.Id) })
                    .ToList(),
            };
        }
        return Outcome.Ok(new SettingsResponse { Groups = groups });
    }

    public async Task<Outcome> SaveAsync(UpdateSettingsRequest request, ClaimsPrincipal principal, CancellationToken ct)
    {
        if (request.Updates is null || request.Updates.Count == 0)
            return Outcome.Invalid("No updates were provided.");
        if (await SetupUsage.RemovalProblemAsync(db, settings, request.Updates, ct) is { } removal)
            return Outcome.Invalid(removal);
        try
        {
            await settings.SetManyAsync(
                request.Updates,
                changedBy: Principal.Email(principal) ?? "admin",
                ct: ct);
        }
        catch (SettingsValidationException ex)
        {
            return Outcome.Invalid(ex.Message);
        }
        if (request.Updates.Keys.Any(k => Setups.KindOfList(k) is not null))
            await tests.PruneAsync(ct);
        return Outcome.NoContent();
    }

    /// <summary>What this process does with Redis, and whether the settings as saved differ from what it started with.</summary>
    public async Task<Outcome<RedisStatusResponse>> RedisStatusAsync(CancellationToken ct)
    {
        var saved = await RedisSettings.LoadAsync(settings, ct);
        return Outcome.Ok(new RedisStatusResponse
        {
            Active = redis.Bound.Active is not null,
            Connected = redis.Muxer?.IsConnected ?? false,
            Endpoints = redis.Bound.Endpoints,
            OnButUnusable = redis.Bound.OnButUnusable,
            RestartPending = RedisSettings.RestartPending(redis.Bound, saved),
        });
    }

    /// <summary>Whether the JWT settings as saved differ from what this process signs and checks tokens with.</summary>
    public async Task<Outcome<JwtStatusResponse>> JwtStatusAsync(CancellationToken ct) =>
        Outcome.Ok(new JwtStatusResponse
        {
            RestartPending = await JwtSettings.RestartPendingAsync(settings, tokens.Config, ct),
        });

    /// <summary>
    /// Ends every session at once. Every account's session stamp rolls, so
    /// every browser cookie and every access token fails its next request,
    /// whatever its expiry says; every refresh token still standing is
    /// withdrawn, so no client can trade one for a new pair. Nobody is
    /// exempt: the administrator who asked signs in again too.
    /// </summary>
    public async Task<Outcome<SessionsRevokedResponse>> RevokeAllSessionsAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var accounts = await db.Users
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.SessionStamp, u => Guid.NewGuid()), ct);
        var refreshTokens = await db.RefreshTokens
            .Where(t => t.RevokedAtUtc == null && t.UsedAtUtc == null && t.ExpiresAtUtc > now)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAtUtc, now), ct);
        await transaction.CommitAsync(ct);
        return Outcome.Ok(new SessionsRevokedResponse { Accounts = accounts, RefreshTokens = refreshTokens });
    }
}

public sealed record UpdateSettingsRequest(Dictionary<string, string?>? Updates);
