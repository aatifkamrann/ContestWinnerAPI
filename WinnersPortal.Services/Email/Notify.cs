using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Domain;
using WinnersPortal.Services.Notifications;

namespace WinnersPortal.Services.Email;

/// <summary>
/// Queues an email in the caller's unit of work. The row is added, never
/// saved — it commits (or rolls back) with the state change it announces, so
/// a rolled-back announcement can never have congratulated anybody. The
/// same news goes to the reader's inbox in the same save, from the same
/// wording, unless the kind is one <see cref="Inbox.Silent"/> keeps out.
/// </summary>
public static class Notify
{
    public static void Queue(AppDbContext db, User to, string kind, EmailContent content, string? dedupeKey = null)
    {
        var now = DateTimeOffset.UtcNow;
        db.EmailMessages.Add(new EmailMessage
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            ToEmail = to.Email,
            ToName = to.DisplayName,
            Subject = content.Subject,
            TextBody = content.TextBody,
            ActionText = content.ActionText,
            ActionPath = content.ActionPath,
            DedupeKey = dedupeKey,
            UnsubscribePath = content.UnsubscribePath,
            CreatedAtUtc = now,
        });
        if (Inbox.FromEmail(to.Id, kind, content, now) is { } line) db.Notifications.Add(line);
    }
}
