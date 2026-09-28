using WinnersPortal.Services.Ai;
using WinnersPortal.Services.Email;
using Lib.Net.Http.WebPush.Authentication;
using Microsoft.EntityFrameworkCore;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Services.Common;
using WinnersPortal.Domain;
using WinnersPortal.Services.Settings;
using PushEncryptionKeyName = Lib.Net.Http.WebPush.PushEncryptionKeyName;
using PushMessageUrgency = Lib.Net.Http.WebPush.PushMessageUrgency;
using PushServiceClient = Lib.Net.Http.WebPush.PushServiceClient;
using PushServiceClientException = Lib.Net.Http.WebPush.PushServiceClientException;
using WebPushMessage = Lib.Net.Http.WebPush.PushMessage;
using WebPushSubscription = Lib.Net.Http.WebPush.PushSubscription;

namespace WinnersPortal.Services.Notifications;

/// <summary>The push worker's nudge — the email one's twin, so a broadcast leaves seconds after the event.</summary>
public sealed class PushWorkSignal(WorkRelay? relay = null) : WorkSignal("push", relay);

/// <summary>
/// Drains the push outbox: encrypts each message for its device, signs the
/// request with the portal's VAPID pair, and posts it to the browser's push
/// service. A device whose service says the subscription is gone is deleted
/// with its queue; anything else retries with backoff, and a message that
/// sat a day unsent expires — a heads-up that late is no heads-up.
/// </summary>
public sealed class PushWorker(
    IServiceScopeFactory scopes,
    SettingsService settings,
    PushKeys keys,
    IHttpClientFactory http,
    PushWorkSignal signal,
    ILogger<PushWorker> log,
    AppPause pause) : BackgroundService
{
    public const string HttpClientName = "push";

    private const int MaxSendAttempts = 5;
    private const int TimeToLiveSeconds = 86_400; // the push service holds it a day for an offline device
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan UnsentExpiry = TimeSpan.FromDays(1);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // No cycle while a database move has the portal paused: a row
                // written now would be one the copy already went past.
                using var lease = pause.TryEnter();
                if (lease is not null) await RunCycleAsync(ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError(e, "Push worker cycle failed; retrying next sweep.");
            }
            await signal.WaitAsync(SweepInterval, ct);
        }
    }

    private async Task RunCycleAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow;

        await ExpireStaleAsync(db, now, ct);
        await SendPendingAsync(db, now, ct);
    }

    internal const string ExpiredReason = "Expired unsent after a day.";

    /// <summary>Pending rows older than the cutoff are marked failed, with the reason; on SQL Server, Push_Expire.</summary>
    private async Task ExpireStaleAsync(AppDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var cutoff = now - UnsentExpiry;
        var expired = db.UseDapper
            ? await db.Sql.ExecuteAsync(Procedures.PushExpire, new { cutoff, reason = ExpiredReason }, ct)
            : await db.PushMessages
                .Where(m => m.Status == PushStatus.Pending && m.CreatedAtUtc < cutoff)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.Status, PushStatus.Failed)
                    .SetProperty(m => m.LastError, ExpiredReason), ct);
        if (expired > 0) log.LogWarning("Expired {Count} push messages that sat unsent for over a day.", expired);
    }

    private async Task SendPendingAsync(AppDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var pending = await db.PushMessages.Include(m => m.Device)
            .Where(m => m.Status == PushStatus.Pending)
            .OrderBy(m => m.CreatedAtUtc)
            .Take(50)
            .ToListAsync(ct);
        // Exponential backoff per message: 2^attempts minutes, capped at an hour.
        pending.RemoveAll(m => m.AttemptedAtUtc is { } last
            && now - last < TimeSpan.FromMinutes(Math.Min(Math.Pow(2, m.Attempts), 60)));
        if (pending.Count == 0) return;

        var (publicKey, privateKey) = await keys.GetAsync(ct);
        var from = Email.EmailSender.From(await Email.EmailSender.ServerAsync(settings, ct));
        var icon = await settings.GetAsync("branding.logoUrl", ct);
        if (string.IsNullOrWhiteSpace(icon)) icon = null;

        var client = new PushServiceClient(http.CreateClient(HttpClientName))
        {
            // The subject is who to contact about this sender, per RFC 8292.
            DefaultAuthentication = new VapidAuthentication(publicKey, privateKey) { Subject = "mailto:" + from },
            DefaultTimeToLive = TimeToLiveSeconds,
            // A Retry-After from one service must not stall the whole sweep;
            // the message's own backoff covers the retry.
            AutoRetryAfter = false,
        };

        var gone = new HashSet<Guid>();
        foreach (var msg in pending)
        {
            if (gone.Contains(msg.DeviceId)) continue;
            msg.Attempts++;
            msg.AttemptedAtUtc = now;
            var device = msg.Device!;
            var subscription = new WebPushSubscription { Endpoint = device.Endpoint };
            subscription.SetKey(PushEncryptionKeyName.P256DH, device.P256dh);
            subscription.SetKey(PushEncryptionKeyName.Auth, device.Auth);
            var push = new WebPushMessage(Pushes.Payload(msg, icon))
            {
                Topic = msg.Topic,
                Urgency = PushMessageUrgency.Normal,
            };
            try
            {
                await client.RequestPushMessageDeliveryAsync(subscription, push, ct);
                msg.Status = PushStatus.Sent;
                msg.SentAtUtc = now;
                msg.LastError = null;
            }
            catch (PushServiceClientException e) when (Pushes.Gone(e.StatusCode))
            {
                gone.Add(msg.DeviceId);
                log.LogInformation("Push subscription {Device} is gone ({Status}); removing the device.",
                    device.Id, (int)e.StatusCode);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                msg.LastError = e.Message.Length > 380 ? e.Message[..380] : e.Message;
                if (msg.Attempts >= MaxSendAttempts)
                {
                    msg.Status = PushStatus.Failed;
                    log.LogError(e, "Push {Kind} to device {Device} failed permanently after {Attempts} attempts.",
                        msg.Kind, device.Id, msg.Attempts);
                }
                else
                {
                    log.LogWarning(e, "Push {Kind} to device {Device} failed (attempt {Attempts}); will retry.",
                        msg.Kind, device.Id, msg.Attempts);
                }
            }
        }

        // A dead device's rows go with it (cascade), so the ones touched
        // above are let go here rather than updated into a deleted table row.
        // On SQL Server the devices go through Push_RemoveDevices.
        foreach (var msg in pending.Where(m => gone.Contains(m.DeviceId)))
            db.Entry(msg).State = EntityState.Detached;
        await db.SaveChangesAsync(ct);
        if (gone.Count > 0)
        {
            var removed = db.UseDapper
                ? await db.Sql.ExecuteAsync(Procedures.PushRemoveDevices, new { ids = Sql.JsonIds(gone) }, ct)
                : await db.PushDevices.Where(d => gone.Contains(d.Id)).ExecuteDeleteAsync(ct);
            log.LogInformation("Removed {Count} push devices whose subscriptions are gone.", removed);
        }
    }
}
