using Microsoft.EntityFrameworkCore;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Domain;
using WinnersPortal.Services.Email;

namespace WinnersPortal.Services.Notifications;

/// <summary>
/// Fans an event out to everyone who asked to hear about it. Like
/// <see cref="Notify"/>, rows are added and never saved here: the emails,
/// pushes and inbox lines commit with the publish or the announcement they
/// describe, so an opportunity that failed to publish told nobody it had.
/// </summary>
public static class Broadcast
{
    /// <summary>An opportunity just went from draft to open. Its client is not told — they pressed the button.</summary>
    public static async Task OpportunityOpenedAsync(
        AppDbContext db, UnsubscribeTokens tokens, Opportunity opportunity, CancellationToken ct)
    {
        var subscribers = await db.Users.AsNoTracking()
            .Where(u => u.NotifyNewOpportunities && u.Id != opportunity.ClientId)
            .ToListAsync(ct);
        var clientName = opportunity.Client!.DisplayName;
        Fan(db, tokens, subscribers, await DevicesAsync(db, subscribers, ct), "opportunity_open", opportunity.Id,
            Emails.OpportunityOpened(opportunity.Title, opportunity.Slug, clientName, opportunity.AwardAmount, opportunity.Currency,
                opportunity.DeadlineUtc, LaterStart(opportunity)),
            Pushes.OpportunityOpened(opportunity.Id, opportunity.Title, opportunity.Slug, clientName, opportunity.AwardAmount,
                opportunity.Currency, opportunity.DeadlineUtc, LaterStart(opportunity)),
            newsTo: _ => true);
    }

    /// <summary>The start day, only where it is still ahead — a start of today tells a reader nothing new.</summary>
    private static DateTimeOffset? LaterStart(Opportunity opportunity) =>
        opportunity.StartsAtUtc is { } s && s > DateTimeOffset.UtcNow ? s : null;

    /// <summary>
    /// A winner was just announced. The entrants already have their own
    /// emails and inbox lines (won, or went elsewhere), so the broadcast
    /// skips them; a push is a different channel with no such twin, and
    /// goes to every device that asked.
    /// </summary>
    public static async Task WinnerAnnouncedAsync(
        AppDbContext db, UnsubscribeTokens tokens, Opportunity opportunity, string winnerName,
        IReadOnlySet<Guid> entrantIds, CancellationToken ct)
    {
        var subscribers = await db.Users.AsNoTracking()
            .Where(u => u.NotifyWinners && u.Id != opportunity.ClientId)
            .ToListAsync(ct);
        Fan(db, tokens, subscribers, await DevicesAsync(db, subscribers, ct), "winner_announced", opportunity.Id,
            Emails.WinnerAnnounced(opportunity.Title, opportunity.Slug, winnerName, opportunity.AwardAmount, opportunity.Currency),
            Pushes.WinnerAnnounced(opportunity.Id, opportunity.Title, opportunity.Slug, winnerName, opportunity.AwardAmount,
                opportunity.Currency),
            newsTo: u => !entrantIds.Contains(u.Id));
    }

    private static async Task<ILookup<Guid, PushDevice>> DevicesAsync(
        AppDbContext db, List<User> subscribers, CancellationToken ct)
    {
        if (subscribers.Count == 0) return Array.Empty<PushDevice>().ToLookup(d => d.UserId);
        var ids = subscribers.Select(u => u.Id).ToList();
        var devices = await db.PushDevices.AsNoTracking()
            .Where(d => ids.Contains(d.UserId))
            .ToListAsync(ct);
        return devices.ToLookup(d => d.UserId);
    }

    /// <param name="newsTo">Whether the event is news to this subscriber — false for one who has their own mail about it.</param>
    private static void Fan(
        AppDbContext db, UnsubscribeTokens tokens, List<User> subscribers, ILookup<Guid, PushDevice> devices,
        string kind, Guid opportunityId, EmailContent email, PushContent push, Func<User, bool> newsTo)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var user in subscribers)
        {
            if (newsTo(user))
            {
                // The dedupe key is a backstop against the same event fanning
                // out twice; the unique index underneath makes it a hard one.
                if (user.NotifyByEmail)
                    Notify.Queue(db, user, kind, email with { UnsubscribePath = tokens.Path(user.Id) },
                        dedupeKey: $"{kind}:{opportunityId:N}:{user.Id:N}");
                // The bell, in the push's words: a subscriber with neither
                // email nor a device still asked to hear.
                db.Notifications.Add(Inbox.Line(user.Id, kind, push.Title, push.Body, push.Path, now));
            }
            foreach (var device in devices[user.Id])
                db.PushMessages.Add(new PushMessage
                {
                    Id = Guid.NewGuid(),
                    DeviceId = device.Id,
                    Kind = kind,
                    Title = push.Title,
                    Body = push.Body,
                    Path = push.Path,
                    Topic = push.Topic,
                    CreatedAtUtc = now,
                });
        }
    }
}
