using WinnersPortal.Services.Ai;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Services.Common;
using WinnersPortal.Domain;
using WinnersPortal.Services.Settings;

namespace WinnersPortal.Services.Email;

/// <summary>
/// Lets request handlers nudge the sender instead of waiting a full sweep —
/// a winner's email goes out seconds after the announcement.
/// </summary>
public sealed class EmailWorkSignal(WorkRelay? relay = null) : WorkSignal("email", relay);

/// <summary>When it is time to compose the daily digest — pure, so the rule is testable.</summary>
public static class DigestSchedule
{
    public static string DateKey(DateTimeOffset nowUtc) => nowUtc.UtcDateTime.ToString("yyyy-MM-dd");

    /// <summary>
    /// Due once per UTC day, any time from the configured hour onward. "From
    /// the hour onward" rather than "at the hour" means a worker that was down
    /// over the digest hour still sends when it comes back, just late.
    /// </summary>
    public static bool IsDue(string? lastSentDate, int hourUtc, DateTimeOffset nowUtc) =>
        nowUtc.UtcDateTime.Hour >= Math.Clamp(hourUtc, 0, 23)
        && !string.Equals(lastSentDate, DateKey(nowUtc), StringComparison.Ordinal);
}

/// <summary>
/// Drains the email outbox in the background — the counterpart of
/// GitHubWorker, for the same reason: no HTTP request ever waits on SMTP or a mail API.
/// Each cycle it composes the daily digest when due, expires rows that sat
/// unsent for days (so configuring email late never floods anyone with stale
/// mail), and sends what is pending with per-message retry and backoff.
/// </summary>
public sealed class EmailWorker(
    IServiceScopeFactory scopes,
    SettingsService settings,
    EmailSender sender,
    EmailWorkSignal signal,
    ILogger<EmailWorker> log,
    AppPause pause) : BackgroundService
{
    private const int MaxSendAttempts = 8;
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan UnsentExpiry = TimeSpan.FromDays(3);

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
                log.LogError(e, "Email worker cycle failed; retrying next sweep.");
            }
            await signal.WaitAsync(SweepInterval, ct);
        }
    }

    private async Task RunCycleAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow;

        await ComposeDigestsAsync(db, now, ct);
        await ExpireStaleAsync(db, now, ct);
        await SweepInboxAsync(db, now, ct);
        await SendPendingAsync(db, now, ct);
    }

    // -------------------------------------------------------------- inbox

    private static readonly TimeSpan InboxSweepInterval = TimeSpan.FromHours(1);
    private DateTimeOffset _lastInboxSweep;

    /// <summary>
    /// Once an hour, inbox lines older than <c>limits.inboxRetentionDays</c>
    /// go — read or not; a line nobody opened in ninety days is not news.
    /// Here rather than in a worker of its own because the inbox is the
    /// outbox's twin, written in the same saves. On SQL Server, Inbox_Sweep.
    /// </summary>
    private async Task SweepInboxAsync(AppDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        if (now - _lastInboxSweep < InboxSweepInterval) return;
        _lastInboxSweep = now;
        if (Notifications.InboxRetention.CutOff(await settings.GetAsync(Notifications.InboxRetention.Key, ct), now) is not { } cutOff)
            return;
        var removed = db.UseDapper
            ? await db.Sql.ExecuteAsync(Procedures.InboxSweep, new { cutOff }, ct)
            : await db.Notifications.Where(n => n.CreatedAtUtc < cutOff).ExecuteDeleteAsync(ct);
        if (removed > 0) log.LogInformation("Inbox: {Count} lines older than {CutOff:u} removed.", removed, cutOff);
    }

    // ------------------------------------------------------------- digest

    private async Task ComposeDigestsAsync(AppDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var lastDate = await settings.GetAsync("system.digestLastDate", ct);
        _ = int.TryParse(await settings.GetAsync("notifications.digestHourUtc", ct), out var hour);
        if (!DigestSchedule.IsDue(lastDate, hour, now)) return;

        var today = DigestSchedule.DateKey(now);
        var portalName = await settings.GetAsync("branding.portalName", ct) ?? "Winners Portal";
        var lines = new Dictionary<Guid, List<string>>();
        void Add(Guid userId, string line) =>
            (lines.TryGetValue(userId, out var list) ? list : lines[userId] = []).Add(line);

        // Clients: opportunities waiting on a decision, and awards waiting on money.
        var undecided = await db.Opportunities.AsNoTracking()
            .Where(c => c.Status == OpportunityStatus.Reviewing)
            .Select(c => new
            {
                c.ClientId, c.Title, c.DeadlineUtc,
                Entries = c.Entries.Count(e => e.Status == EntryStatus.Active),
            })
            .ToListAsync(ct);
        foreach (var c in undecided)
            Add(c.ClientId, $"“{c.Title}” closed {AgoDays(now, c.DeadlineUtc)} with {c.Entries} "
                + $"{(c.Entries == 1 ? "entry" : "entries")} — no winner announced yet.");

        var unpaid = await db.Awards.AsNoTracking()
            .Where(a => a.PaidAtUtc == null && a.Opportunity!.Kind == OpportunityKind.Competitive)
            .Select(a => new
            {
                a.Opportunity!.ClientId, a.Opportunity.Title, a.Amount, a.Currency, a.AnnouncedAtUtc,
                Winner = a.Entry!.Freelancer!.DisplayName,
            })
            .ToListAsync(ct);
        foreach (var a in unpaid)
            Add(a.ClientId, $"The award on “{a.Title}” — {Emails.Money(a.Amount, a.Currency)} to {a.Winner}, "
                + $"announced {AgoDays(now, a.AnnouncedAtUtc)} — is not marked paid. Your payment record is public.");

        // Paid by milestone: milestones handed in and still waiting on the
        // client — to approve, or approved and not yet marked paid.
        var waitingMilestones = await db.Checkpoints.AsNoTracking()
            .Where(cp => cp.PaidAtUtc == null && cp.ChangesRequestedAtUtc == null
                && cp.Entry!.Status == EntryStatus.Active
                && cp.Entry.Opportunity!.Kind == OpportunityKind.Milestones
                && cp.Entry.Opportunity.Status == OpportunityStatus.Awarded)
            .Select(cp => new
            {
                cp.Entry!.Opportunity!.ClientId, cp.Entry.Opportunity.Title, cp.Milestone!.Order, cp.Milestone.Amount,
                cp.Entry.Opportunity.Currency, cp.ClaimedAtUtc, cp.ApprovedAtUtc,
            })
            .ToListAsync(ct);
        foreach (var m in waitingMilestones)
            Add(m.ClientId, m.ApprovedAtUtc is { } approved
                ? $"Milestone {m.Order + 1} on “{m.Title}” was approved {AgoDays(now, approved)} and is not marked paid"
                  + (m.Amount is { } a ? $" ({Emails.Money(a, m.Currency)})" : "") + " — the next milestone waits on it."
                : $"Milestone {m.Order + 1} on “{m.Title}” was handed in {AgoDays(now, m.ClaimedAtUtc)} and waits on your review.");

        // Freelancers: deadlines inside 48 hours on entries that are not done.
        var closing = await db.Entries.AsNoTracking()
            .Where(e => e.Status == EntryStatus.Active
                && e.Opportunity!.Status == OpportunityStatus.Open
                && e.Opportunity.DeadlineUtc > now
                && e.Opportunity.DeadlineUtc <= now.AddHours(48))
            .Select(e => new
            {
                e.FreelancerId, e.Opportunity!.Title, e.Opportunity.DeadlineUtc,
                Claimed = e.Checkpoints.Count, Possible = e.Opportunity.Milestones.Count,
            })
            .ToListAsync(ct);
        foreach (var e in closing)
            Add(e.FreelancerId, $"“{e.Title}” closes in {Math.Max(1, (int)(e.DeadlineUtc!.Value - now).TotalHours)} "
                + $"hours — {e.Claimed} of {e.Possible} milestones claimed. Unclaimed work looks unbuilt.");

        // Admins: the portal-health items that never resolve on their own.
        var failedRepos = await db.Entries.CountAsync(e => e.Status == EntryStatus.Active
            && e.ProvisionStatus == RepoProvisionStatus.Failed
            && e.Opportunity!.Status == OpportunityStatus.Open, ct);
        var stuckHandovers = await db.Awards.CountAsync(
            a => a.PaidAtUtc != null && a.Handover == HandoverStatus.Requested, ct);
        var neverStarted = await db.Awards.CountAsync(
            a => a.PaidAtUtc != null && a.Handover == HandoverStatus.NotStarted, ct);
        var reportedChats = await db.ChatReports.Where(r => r.ResolvedAtUtc == null).Select(r => r.EntryId).Distinct().CountAsync(ct);
        var adminLines = new List<string>();
        if (failedRepos > 0)
            adminLines.Add($"{failedRepos} {(failedRepos == 1 ? "repository has" : "repositories have")} failed "
                + "provisioning — those entrants cannot start while the clock runs.");
        if (stuckHandovers > 0)
            adminLines.Add($"{stuckHandovers} paid {(stuckHandovers == 1 ? "award has" : "awards have")} an "
                + "unverified repository handover — money has moved, code has not.");
        if (reportedChats > 0)
            adminLines.Add($"{reportedChats} reported {(reportedChats == 1 ? "conversation is" : "conversations are")} waiting "
                + "to be reviewed — the members who reported them have heard nothing back yet.");
        if (neverStarted > 0)
            adminLines.Add($"{neverStarted} paid {(neverStarted == 1 ? "award has" : "awards have")} no transfer "
                + "started at all — the winner's code is still owned by the portal.");

        var userIds = lines.Keys.ToList();
        var users = await db.Users
            .Where(u => userIds.Contains(u.Id) || (adminLines.Count > 0 && u.Role == Roles.Admin))
            .ToListAsync(ct);

        // The date gate makes a duplicate day near-impossible; the dedupe key
        // check (and its unique index underneath) makes it actually impossible.
        var sentKeys = await db.EmailMessages
            .Where(m => m.DedupeKey != null && m.DedupeKey.EndsWith(today))
            .Select(m => m.DedupeKey!)
            .ToListAsync(ct);

        var queued = 0;
        foreach (var user in users)
        {
            var mine = lines.GetValueOrDefault(user.Id) ?? [];
            if (user.Role == Roles.Admin) mine = [.. adminLines, .. mine];
            if (mine.Count == 0) continue;
            var key = $"digest:{user.Id:N}:{today}";
            if (sentKeys.Contains(key)) continue;
            Notify.Queue(db, user, "digest", Emails.Digest(portalName, mine), key);
            queued++;
        }
        await db.SaveChangesAsync(ct);
        await settings.SetManyAsync(new Dictionary<string, string?> { ["system.digestLastDate"] = today },
            changedBy: "email-worker", allowSystem: true, ct: ct);
        if (queued > 0) log.LogInformation("Digest composed for {Count} users.", queued);
    }

    private static string AgoDays(DateTimeOffset now, DateTimeOffset? then)
    {
        if (then is null) return "recently";
        var days = (int)(now - then.Value).TotalDays;
        return days <= 0 ? "today" : days == 1 ? "yesterday" : $"{days} days ago";
    }

    // ------------------------------------------------------------- expiry

    internal const string ExpiredReason = "Expired unsent after 3 days — email was unconfigured or failing throughout.";

    private async Task ExpireStaleAsync(AppDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        // A backlog from before email was configured must not flood anyone the
        // moment it is. Old news is worse than no news; the rows keep the
        // record, marked for the operator. On SQL Server, Email_Expire.
        var cutoff = now - UnsentExpiry;
        var expired = db.UseDapper
            ? await db.Sql.ExecuteAsync(Procedures.EmailExpire, new { cutoff, reason = ExpiredReason }, ct)
            : await db.EmailMessages
                .Where(m => m.Status == EmailStatus.Pending && m.CreatedAtUtc < cutoff)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.Status, EmailStatus.Failed)
                    .SetProperty(m => m.LastError, ExpiredReason),
                    ct);
        if (expired > 0) log.LogWarning("Expired {Count} emails that sat unsent for over 3 days.", expired);
    }

    // -------------------------------------------------------------- send

    // After the service itself failed — would not connect, refused the key,
    // was down — the outbox waits before trying it again: a minute, then
    // doubling to fifteen. Each try of a mail API is a row in the activity
    // log, and one every sweep would bury the log in the same refusal. The
    // wait is for these values only: a setup saved differently, or another
    // made active, is tried at once.
    private static readonly TimeSpan MaxServiceWait = TimeSpan.FromMinutes(15);
    private string? _downValues;
    private DateTimeOffset _downUntil;
    private int _downCount;

    private static string ValuesOf(SetupValues server) =>
        server.Id + ":" + SetupTestLog.Fingerprint(Setups.Email, server);

    private void ServiceDown(SetupValues server, DateTimeOffset now, Exception e, int waiting)
    {
        var values = ValuesOf(server);
        _downCount = values == _downValues ? _downCount + 1 : 1;
        _downValues = values;
        var wait = TimeSpan.FromMinutes(Math.Min(Math.Pow(2, _downCount - 1), MaxServiceWait.TotalMinutes));
        _downUntil = now + wait;
        // The service, not the messages, is the problem — attempts are left
        // alone and the outbox tries again later. Expiry bounds the wait.
        log.LogWarning(e, "Email setup {Setup} ({Where}) is not taking mail; {Count} emails wait, next try in {Minutes} min.",
            server.Name, EmailSender.Where(server), waiting, wait.TotalMinutes);
    }

    private async Task SendPendingAsync(AppDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var server = await EmailSender.ServerAsync(settings, ct);
        if (server is null) return; // not configured; rows wait, expiry is the exit
        if (now < _downUntil && _downValues == ValuesOf(server)) return;

        var pending = await db.EmailMessages
            .Where(m => m.Status == EmailStatus.Pending)
            .OrderBy(m => m.CreatedAtUtc)
            .Take(20)
            .ToListAsync(ct);
        // Exponential backoff per message: 2^attempts minutes, capped at an hour.
        pending.RemoveAll(m => m.AttemptedAtUtc is { } last
            && now - last < TimeSpan.FromMinutes(Math.Min(Math.Pow(2, m.Attempts), 60)));
        if (pending.Count == 0) return;

        var portalName = await settings.GetAsync("branding.portalName", ct) ?? "Winners Portal";
        var publicUrl = await settings.GetAsync("branding.publicUrl", ct) ?? "http://localhost";
        var accent = await settings.GetAsync("branding.accentColor", ct) ?? "#0f766e";

        IMailTransport transport;
        try
        {
            transport = await sender.OpenAsync(server, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            ServiceDown(server, now, e, pending.Count);
            return;
        }
        await using var _ = transport;
        var from = EmailSender.From(server);

        foreach (var msg in pending)
        {
            try
            {
                var url = msg.ActionPath is null ? null : EmailRender.AbsoluteUrl(publicUrl, msg.ActionPath);
                var stop = msg.UnsubscribePath is null ? null : EmailRender.AbsoluteUrl(publicUrl, msg.UnsubscribePath);
                // Shared with the settings test send — one MIME shape, so
                // what the test proves is what notifications actually do.
                var mime = EmailSender.Compose(portalName, from, msg.ToName, msg.ToEmail,
                    msg.Subject, msg.TextBody, msg.ActionText, url, accent, stop);

                await transport.SendAsync(mime, ct);
                msg.Attempts++;
                msg.AttemptedAtUtc = now;
                msg.Status = EmailStatus.Sent;
                msg.SentAtUtc = now;
                msg.LastError = null;
                _downCount = 0;
            }
            catch (MailServiceDown e)
            {
                // Every message would get the same answer: this one keeps
                // its attempts, the rest are not tried, and the reason is
                // on the row for whoever looks.
                msg.LastError = Short(e.Message);
                ServiceDown(server, now, e, pending.Count);
                break;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                msg.Attempts++;
                msg.AttemptedAtUtc = now;
                msg.LastError = Short(e.Message);
                if (msg.Attempts >= MaxSendAttempts)
                {
                    msg.Status = EmailStatus.Failed;
                    log.LogError(e, "Email {Kind} to {To} failed permanently after {Attempts} attempts.",
                        msg.Kind, msg.ToEmail, msg.Attempts);
                }
                else
                {
                    log.LogWarning(e, "Email {Kind} to {To} failed (attempt {Attempts}); will retry.",
                        msg.Kind, msg.ToEmail, msg.Attempts);
                }
            }
        }
        // Recorded before the transport says goodbye, which cannot fail loudly.
        await db.SaveChangesAsync(ct);
    }

    private static string Short(string message) => message.Length > 380 ? message[..380] : message;
}
