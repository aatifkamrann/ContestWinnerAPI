using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using WinnersPortal.Domain;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Services.Auth;
using WinnersPortal.Services.Common;

namespace WinnersPortal.Services.Notifications;

/// <summary>
/// The bell's API: the inbox behind it, a line marked read or unread and
/// the whole inbox marked either way; and the Notifications page's: what
/// to hear about (an opportunity opening, a winner announced), by which channel
/// (email, and push per device), and the anonymous one-click unsubscribe
/// every opted-in email carries.
/// </summary>
public sealed partial class NotificationService(AppDbContext db, PushKeys keys, UnsubscribeTokens tokens)
{
    private const string BadUnsubscribeLink =
        "This link is not valid. Sign in and turn email off under Notifications.";

    /// <summary>The bell's panel shows this many; the page asks for the same and pages on.</summary>
    public const int PageSize = 20;
    public const int MaxPageSize = 50;

    // ------------------------------------------------------------- inbox

    /// <summary>
    /// One page of the caller's inbox, newest first, from the cursor the
    /// page before handed back. On SQL Server, Inbox_Page.
    /// </summary>
    public async Task<Outcome<InboxResponse>> InboxAsync(string? cursor, int? take, ClaimsPrincipal principal, CancellationToken ct)
    {
        var id = Principal.UserId(principal)!.Value;
        var size = Math.Clamp(take ?? PageSize, 1, MaxPageSize);
        var before = Opportunities.Cursor.Decode(cursor);
        // One more than the page, to know whether there is another.
        var page = db.UseDapper
            ? await InboxSqlAsync(db.Sql, id, before, size + 1, ct)
            : await InboxLinqAsync(db, id, before, size + 1, ct);
        var more = page.Rows.Count > size;
        var rows = more ? page.Rows[..size] : page.Rows;
        return Outcome.Ok(new InboxResponse
        {
            Items = rows.Select(r => new InboxLine
            {
                Id = r.Id,
                Kind = r.Kind,
                Title = r.Title,
                Body = r.Body,
                Path = r.Path,
                CreatedAtUtc = r.CreatedAtUtc,
                ReadAtUtc = r.ReadAtUtc,
            }),
            Unread = page.Unread,
            Total = page.Total,
            NextCursor = more ? new Opportunities.Cursor(rows[^1].CreatedAtUtc, rows[^1].Id).Encode() : null,
        });
    }

    internal sealed record InboxRow(Guid Id, string Kind, string Title, string Body, string? Path,
        DateTimeOffset CreatedAtUtc, DateTimeOffset? ReadAtUtc);

    internal sealed record InboxPage(int Unread, int Total, List<InboxRow> Rows);

    internal static async Task<InboxPage> InboxLinqAsync(AppDbContext db, Guid userId, Opportunities.Cursor? before, int take, CancellationToken ct)
    {
        var mine = db.Notifications.AsNoTracking().Where(n => n.UserId == userId);
        var counts = await mine
            .GroupBy(_ => 1)
            .Select(g => new { Unread = g.Count(n => n.ReadAtUtc == null), Total = g.Count() })
            .SingleOrDefaultAsync(ct);
        if (before is { } c)
            mine = mine.Where(n => n.CreatedAtUtc < c.At || (n.CreatedAtUtc == c.At && n.Id.CompareTo(c.Id) < 0));
        var rows = await mine
            .OrderByDescending(n => n.CreatedAtUtc).ThenByDescending(n => n.Id)
            .Take(take)
            .Select(n => new InboxRow(n.Id, n.Kind, n.Title, n.Body, n.Path, n.CreatedAtUtc, n.ReadAtUtc))
            .ToListAsync(ct);
        return new InboxPage(counts?.Unread ?? 0, counts?.Total ?? 0, rows);
    }

    /// <summary>
    /// One of the caller's lines read or unread. Reading keeps the first
    /// reading's time; a line that is not theirs is not found. On SQL
    /// Server, Inbox_MarkOne.
    /// </summary>
    public async Task<Outcome<InboxLineMarkedResponse>> MarkAsync(Guid id, bool read, ClaimsPrincipal principal, CancellationToken ct)
    {
        var userId = Principal.UserId(principal)!.Value;
        DateTimeOffset? readAt = read ? DateTimeOffset.UtcNow : null;
        var mine = db.Notifications.Where(n => n.Id == id && n.UserId == userId);
        var changed = db.UseDapper
            ? await db.Sql.ExecuteAsync(Procedures.InboxMarkOne, new { userId, id, readAt }, ct)
            : read
                ? await mine.ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAtUtc, n => n.ReadAtUtc ?? readAt), ct)
                : await mine.ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAtUtc, readAt), ct);
        if (changed == 0) return Outcome.NotFound();
        // What the row holds now: the earlier reading's time where there was one.
        var stamp = read
            ? await db.Notifications.AsNoTracking().Where(n => n.Id == id).Select(n => n.ReadAtUtc).SingleOrDefaultAsync(ct)
            : null;
        return Outcome.Ok(new InboxLineMarkedResponse { Id = id, ReadAtUtc = stamp });
    }

    /// <summary>Every one of the caller's lines read, or every one unread. On SQL Server, Inbox_MarkAll.</summary>
    public async Task<Outcome<InboxMarkedResponse>> MarkAllAsync(bool read, ClaimsPrincipal principal, CancellationToken ct)
    {
        var userId = Principal.UserId(principal)!.Value;
        DateTimeOffset? readAt = read ? DateTimeOffset.UtcNow : null;
        var changed = db.UseDapper
            ? await db.Sql.ExecuteAsync(Procedures.InboxMarkAll, new { userId, readAt }, ct)
            : await db.Notifications
                .Where(n => n.UserId == userId && (read ? n.ReadAtUtc == null : n.ReadAtUtc != null))
                .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAtUtc, readAt), ct);
        return Outcome.Ok(new InboxMarkedResponse { Changed = changed });
    }

    // ------------------------------------------------------- preferences

    public async Task<Outcome<NotificationPreferencesResponse>> PreferencesAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        var id = Principal.UserId(principal)!.Value;
        var row = await db.Users.AsNoTracking()
            .Where(u => u.Id == id)
            .Select(u => new { u.NotifyNewOpportunities, u.NotifyWinners, u.NotifyByEmail })
            .SingleOrDefaultAsync(ct);
        if (row is null) return Outcome.Unauthorized();

        var devices = await db.PushDevices.AsNoTracking()
            .Where(d => d.UserId == id)
            .OrderBy(d => d.CreatedAtUtc)
            .ToListAsync(ct);
        // The page tells its own browser's subscription apart by hashing
        // the endpoint on both sides; the endpoint itself is a capability
        // URL and stays on the server.
        var (publicKey, _) = await keys.GetAsync(ct);
        return Outcome.Ok(new NotificationPreferencesResponse
        {
            NewOpportunities = row.NotifyNewOpportunities,
            Winners = row.NotifyWinners,
            ByEmail = row.NotifyByEmail,
            PushKey = publicKey,
            Devices = devices.Select(d => new PushDeviceView
            {
                Id = d.Id,
                Label = d.Label,
                CreatedAtUtc = d.CreatedAtUtc,
                EndpointHash = EndpointHash(d.Endpoint),
            }),
        });
    }

    public async Task<Outcome<NotificationChoicesResponse>> SavePreferencesAsync(NotificationPreferencesRequest request, ClaimsPrincipal principal, CancellationToken ct)
    {
        var id = Principal.UserId(principal)!.Value;
        var account = await db.Users.SingleOrDefaultAsync(u => u.Id == id, ct);
        if (account is null) return Outcome.Unauthorized();
        account.NotifyNewOpportunities = request.NewOpportunities;
        account.NotifyWinners = request.Winners;
        account.NotifyByEmail = request.ByEmail;
        await db.SaveChangesAsync(ct);
        return Outcome.Ok(new NotificationChoicesResponse
        {
            NewOpportunities = account.NotifyNewOpportunities,
            Winners = account.NotifyWinners,
            ByEmail = account.NotifyByEmail,
        });
    }

    public async Task<Outcome<DeviceRegisteredResponse>> RegisterDeviceAsync(RegisterDeviceRequest request, ClaimsPrincipal principal, CancellationToken ct)
    {
        if (!Vapid.IsValidEndpoint(request.Endpoint)
            || !Vapid.IsValidP256dh(request.P256dh)
            || !Vapid.IsValidAuth(request.Auth))
            return Outcome.Invalid(
                "The browser's push subscription is not valid — turn push off and on again.");

        var id = Principal.UserId(principal)!.Value;
        var now = DateTimeOffset.UtcNow;
        var label = string.IsNullOrWhiteSpace(request.Label) ? null : request.Label.Trim();
        if (label?.Length > 80) label = label[..80];

        var device = await db.PushDevices.SingleOrDefaultAsync(d => d.Endpoint == request.Endpoint, ct);
        if (device is null)
        {
            device = new PushDevice
            {
                Id = Guid.NewGuid(),
                UserId = id,
                Endpoint = request.Endpoint!,
                P256dh = request.P256dh!,
                Auth = request.Auth!,
                Label = label,
                CreatedAtUtc = now,
                LastSeenAtUtc = now,
            };
            db.PushDevices.Add(device);
        }
        else
        {
            if (device.UserId != id)
            {
                // Whatever was queued for the previous holder is not
                // this person's news. On SQL Server, Push_DropPending.
                if (db.UseDapper) await db.Sql.ExecuteAsync(Procedures.PushDropPending, new { deviceId = device.Id }, ct);
                else await db.PushMessages
                    .Where(m => m.DeviceId == device.Id && m.Status == PushStatus.Pending)
                    .ExecuteDeleteAsync(ct);
                device.UserId = id;
            }
            device.P256dh = request.P256dh!;
            device.Auth = request.Auth!;
            device.Label = label ?? device.Label;
            device.LastSeenAtUtc = now;
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (DbErrors.IsUniqueViolation(e))
        {
            // Two tabs registering the same browser at once: the second
            // finds the first's row on its retry.
            return Outcome.Conflict("This browser was just registered — try again.");
        }
        return Outcome.Ok(new DeviceRegisteredResponse { Id = device.Id, EndpointHash = EndpointHash(device.Endpoint) });
    }

    /// <summary>A device of this person's; its queued rows go with it (cascade). On SQL Server, Push_RemoveDevice.</summary>
    public async Task<Outcome> RemoveDeviceAsync(Guid id, ClaimsPrincipal principal, CancellationToken ct)
    {
        var userId = Principal.UserId(principal)!.Value;
        var removed = db.UseDapper
            ? await db.Sql.ExecuteAsync(Procedures.PushRemoveDevice, new { id, userId }, ct)
            : await db.PushDevices
                .Where(d => d.Id == id && d.UserId == userId)
                .ExecuteDeleteAsync(ct);
        return removed == 0 ? Outcome.NotFound() : Outcome.NoContent();
    }

    public async Task<Outcome<UnsubscribeResponse>> UnsubscribeAsync(UnsubscribeRequest request, CancellationToken ct)
    {
        var id = tokens.Read(request.Token);
        var account = id is null ? null : await db.Users.SingleOrDefaultAsync(u => u.Id == id, ct);
        if (account is null) return Outcome.Invalid(BadUnsubscribeLink);
        if (account.NotifyByEmail)
        {
            account.NotifyByEmail = false;
            await db.SaveChangesAsync(ct);
        }
        return Outcome.Ok(new UnsubscribeResponse { ByEmail = false });
    }

    internal static string EndpointHash(string endpoint) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(endpoint))).ToLowerInvariant();
}

public sealed record NotificationPreferencesRequest(bool NewOpportunities, bool Winners, bool ByEmail);

/// <summary>Read, or unread: the one thing a line, or the whole inbox, is marked.</summary>
public sealed record InboxMarkRequest(bool Read);

public sealed record RegisterDeviceRequest(string? Endpoint, string? P256dh, string? Auth, string? Label);

public sealed record UnsubscribeRequest(string? Token);
