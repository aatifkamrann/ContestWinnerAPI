using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WinnersPortal.Domain;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Services.Common;
using WinnersPortal.Services.Opportunities;
using WinnersPortal.Services.GitHub;
using WinnersPortal.Services.Identity;
using WinnersPortal.Services.Ai;
using WinnersPortal.Services.Live;
using WinnersPortal.Services.Preview;
using WinnersPortal.Services.Settings;

namespace WinnersPortal.Services.Admin;

/// <summary>
/// The operations console: the dashboard says what is stuck, this is where
/// the operator unsticks it. Three actions, each of them a restart of
/// machinery that already exists — retry a failed provisioning, restart a
/// handover, replay a webhook delivery — because the failure modes they cover
/// (a bad username fixed, a GitHub App reinstalled, a delivery that arrived
/// before its entry) all end with "run it again", never with hand-editing.
/// Beside them, the slow queries the process has seen — read from memory,
/// cleared from here, so a fix is measured from the moment it shipped —
/// and what the AI features have cost, a month of day rows priced as the
/// settings price them now.
/// </summary>
public sealed partial class AdminService(
    AppDbContext db, GitHubWorkSignal githubSignal, GitHubService github, ILiveBoard live, PreviewWorkSignal previewSignal,
    ILoggerFactory logFactory, IdentityService identity, SlowQueryStats slowQueries, AiQuota aiQuota, AiOptions ai)
{
    /// <summary>How many query shapes the screen shows, the costliest first.</summary>
    public const int SlowQueriesShown = 50;

    public async Task<Outcome<OperationsResponse>> OperationsAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        // The three reads, in the LINQ or the T-SQL.
        var (provisioning, handovers, deliveries) = db.UseDapper
            ? await OperationsSqlAsync(db.Sql, ct)
            : await OperationsLinqAsync(db, ct);

        return Outcome.Ok(new OperationsResponse
        {
            GeneratedAtUtc = now,
            Provisioning = provisioning.Select(p => new ProvisioningRow
            {
                Id = p.Id, GithubUsername = p.GithubUsername, Freelancer = p.Freelancer,
                OpportunityTitle = p.OpportunityTitle, OpportunitySlug = p.OpportunitySlug,
                OpportunityStatus = OpportunityNames.StatusName(p.OpportunityStatus),
                Status = OpportunityNames.ProvisionName(p.Status),
                Attempts = p.Attempts, LastAttemptAtUtc = p.LastAttemptAtUtc, Note = p.Note,
                // Retry re-enters the worker's sweep, and the sweep only
                // provisions for open opportunities — surface that up front
                // instead of letting the button silently do nothing.
                CanRetry = p.Status == RepoProvisionStatus.Failed
                    && p.OpportunityStatus == OpportunityStatus.Open,
            }),
            Handovers = handovers.Select(h => new HandoverRow
            {
                Id = h.Id, OpportunityTitle = h.OpportunityTitle, OpportunitySlug = h.OpportunitySlug,
                Client = h.Client, ClientGithubLogin = h.ClientGithubLogin,
                Winner = h.Winner, RepoFullName = h.RepoFullName, Amount = h.Amount, Currency = h.Currency,
                AnnouncedAtUtc = h.AnnouncedAtUtc, PaidAtUtc = h.PaidAtUtc,
                Handover = AwardNames.HandoverName(h.Handover),
                TransferTargetLogin = h.TransferTargetLogin, TransferRequestedAtUtc = h.TransferRequestedAtUtc,
                HandoverVerifiedAtUtc = h.HandoverVerifiedAtUtc, Note = h.Note,
                CanRestart = h.PaidAtUtc is not null && h.Handover != HandoverStatus.Verified,
            }),
            Deliveries = deliveries,
            SlowQueries = SlowQueries(),
            AiUsage = AiUsageReport.Build(
                await aiQuota.SpendSinceAsync(AiUsageReport.FromDay(now), ct), now,
                await ai.PricesAsync(ct), await ai.DailyBudgetUsdAsync(ct), await ai.DailyCallLimitAsync(ct)),
        });
    }

    // ---- the console's reads, whichever database ran them; the LINQ is
    // here, the T-SQL in AdminService.SqlServer.cs.

    /// <summary>A repository that is not simply fine.</summary>
    internal sealed record ProvisioningRead(
        Guid Id, string GithubUsername, string Freelancer, string OpportunityTitle, string OpportunitySlug, OpportunityStatus OpportunityStatus,
        RepoProvisionStatus Status, int Attempts, DateTimeOffset? LastAttemptAtUtc, string? Note);

    /// <summary>One award's paper trail.</summary>
    internal sealed record HandoverRead(
        Guid Id, string OpportunityTitle, string OpportunitySlug, string Client, string? ClientGithubLogin, string Winner,
        string? RepoFullName, decimal Amount, string Currency, DateTimeOffset AnnouncedAtUtc, DateTimeOffset? PaidAtUtc,
        HandoverStatus Handover, string? TransferTargetLogin, DateTimeOffset? TransferRequestedAtUtc,
        DateTimeOffset? HandoverVerifiedAtUtc, string? Note);

    internal sealed record OperationsReads(
        List<ProvisioningRead> Provisioning, List<HandoverRead> Handovers, List<WebhookDeliveryRow> Deliveries);

    internal static async Task<OperationsReads> OperationsLinqAsync(AppDbContext db, CancellationToken ct)
    {
        // Repositories that are not simply fine: failed outright, or
        // pending with at least one failed attempt behind them.
        var provisioning = await db.Entries.AsNoTracking()
            .Where(e => e.Status == EntryStatus.Active
                && (e.ProvisionStatus == RepoProvisionStatus.Failed
                    || (e.ProvisionStatus == RepoProvisionStatus.Pending && e.ProvisionAttempts > 0)))
            .OrderByDescending(e => e.ProvisionStatus)
            .ThenBy(e => e.CreatedAtUtc)
            .Select(e => new ProvisioningRead(
                e.Id, e.GithubUsername, e.Freelancer!.DisplayName, e.Opportunity!.Title, e.Opportunity.Slug, e.Opportunity.Status,
                e.ProvisionStatus, e.ProvisionAttempts, e.ProvisionAttemptedAtUtc, e.ProvisionNote))
            .Take(50)
            .ToListAsync(ct);

        // Every award, newest first — the paper trail the disputes land
        // on. Announced, paid, transfer requested, verified: four stamps
        // that say exactly how far the deal got.
        var handovers = await db.Awards.AsNoTracking()
            .OrderByDescending(a => a.AnnouncedAtUtc)
            .Select(a => new HandoverRead(
                a.Id, a.Opportunity!.Title, a.Opportunity.Slug, a.Opportunity.Client!.DisplayName, a.Opportunity.Client.GithubLogin,
                a.Entry!.Freelancer!.DisplayName, a.Entry.RepoFullName, a.Amount, a.Currency, a.AnnouncedAtUtc, a.PaidAtUtc,
                a.Handover, a.TransferTargetLogin, a.TransferRequestedAtUtc, a.HandoverVerifiedAtUtc, a.HandoverNote))
            .Take(25)
            .ToListAsync(ct);

        var deliveries = await db.WebhookDeliveries.AsNoTracking()
            .OrderByDescending(d => d.ReceivedAtUtc)
            .Select(d => new WebhookDeliveryRow
            {
                Id = d.Id,
                Source = d.Source,
                DeliveryId = d.DeliveryId,
                Event = d.Event,
                Action = d.Action,
                RepoFullName = d.RepoFullName,
                HandledNote = d.HandledNote,
                ReceivedAtUtc = d.ReceivedAtUtc,
            })
            .Take(50)
            .ToListAsync(ct);

        return new OperationsReads(provisioning, handovers, deliveries);
    }

    private SlowQueriesSection SlowQueries() => new()
    {
        ThresholdMs = slowQueries.ThresholdMs is var ms and > 0 ? ms : null,
        IncludesBackground = slowQueries.IncludesBackground,
        SinceUtc = slowQueries.SinceUtc,
        Items = slowQueries.Snapshot(SlowQueriesShown).Select(s => new SlowQueryRow
        {
            Sql = s.Sql, Source = s.Source, Count = s.Count, TotalMs = s.TotalMs,
            AverageMs = s.TotalMs / s.Count, MaxMs = s.MaxMs, LastMs = s.LastMs, LastRows = s.LastRows,
            LastDuring = s.LastDuring, FirstAtUtc = s.FirstAtUtc, LastAtUtc = s.LastAtUtc,
        }).ToList(),
    };

    /// <summary>Starts the slow-query tally again from now; the log lines already written stay.</summary>
    public Outcome<SlowQueriesSection> ClearSlowQueries()
    {
        slowQueries.Clear();
        return Outcome.Ok(SlowQueries());
    }

    public async Task<Outcome<RetryProvisionResponse>> RetryProvisionAsync(Guid id, CancellationToken ct)
    {
        var entry = await db.Entries.Include(e => e.Opportunity)
            .SingleOrDefaultAsync(e => e.Id == id, ct);
        if (entry is null || entry.Status != EntryStatus.Active) return Outcome.NotFound();
        if (entry.ProvisionStatus != RepoProvisionStatus.Failed)
            return Outcome.Conflict(
                entry.ProvisionStatus == RepoProvisionStatus.Provisioned
                    ? "This repository is already provisioned."
                    : "This repository is still being retried automatically — the worker has not given up yet.");
        if (entry.Opportunity!.Status != OpportunityStatus.Open)
            return Outcome.Conflict(
                "The worker only provisions repositories for open opportunities — this one has closed, "
                    + "so a new repository would have nothing to receive.");

        // Back to the front of the sweep with a clean slate. The old note
        // stays until the outcome replaces it: either success clears it or
        // the next failure explains itself.
        entry.ProvisionStatus = RepoProvisionStatus.Pending;
        entry.ProvisionAttempts = 0;
        entry.ProvisionAttemptedAtUtc = null;
        await db.SaveChangesAsync(ct);

        githubSignal.Wake();
        return Outcome.Ok(new RetryProvisionResponse { Status = OpportunityNames.ProvisionName(entry.ProvisionStatus) });
    }

    public async Task<Outcome<RestartHandoverResponse>> RestartHandoverAsync(Guid id, CancellationToken ct)
    {
        var award = await db.Awards
            .Include(a => a.Opportunity).ThenInclude(c => c!.Client)
            .Include(a => a.Entry)
            .SingleOrDefaultAsync(a => a.Id == id, ct);
        if (award is null) return Outcome.NotFound();
        if (award.PaidAtUtc is null)
            return Outcome.Conflict(
                "The transfer fires on confirmed payment — this award has not been marked paid, "
                    + "so there is nothing to restart.");
        if (award.Handover == HandoverStatus.Verified)
            return Outcome.Conflict("This handover is already verified; the repository has its new owner.");

        // The same three preconditions the payment path checks, rechecked
        // now rather than copied from then — the point of this action is
        // that the world has changed since payment: GitHub configured,
        // the client connected, a username fixed.
        if (!await github.IsConfiguredAsync(ct))
            return Outcome.Conflict("GitHub is not configured; there is no way to transfer a repository.");
        if (award.Entry!.RepoFullName is null)
            return Outcome.Conflict("The winning entry has no repository; nothing to transfer.");
        if (award.Opportunity!.Client!.GithubLogin is null)
            return Outcome.Conflict(
                "The client has no connected GitHub account to transfer to — "
                    + "ask them to connect one first.");
        // Refused here rather than queued: a restart that can only fail
        // the same way on the next sweep is a button that lies.
        if (await github.TransferBlockerAsync(award.Entry.RepoFullName, ct) is { } blocker)
            return Outcome.Conflict(blocker);

        award.Handover = HandoverStatus.Requested;
        award.TransferTargetLogin = award.Opportunity.Client.GithubLogin; // current login, not the one from payment time
        award.TransferRequestedAtUtc = null; // the worker re-fires the transfer call
        award.HandoverNote = null;
        await db.SaveChangesAsync(ct);

        githubSignal.Wake();
        return Outcome.Ok(new RestartHandoverResponse
        {
            Handover = AwardNames.HandoverName(award.Handover),
            TransferTargetLogin = award.TransferTargetLogin,
        });
    }

    public async Task<Outcome<ReplayDeliveryResponse>> ReplayDeliveryAsync(Guid id, CancellationToken ct)
    {
        var delivery = await db.WebhookDeliveries.SingleOrDefaultAsync(d => d.Id == id, ct);
        if (delivery is null) return Outcome.NotFound();

        using var json = JsonDocument.Parse(delivery.Payload);
        string note;
        string? touchedSlug = null;
        var buildQueued = false;
        if (delivery.Source == WebhookDelivery.Identity && delivery.DeliveryId.StartsWith("shufti:", StringComparison.Ordinal))
        {
            // A Shufti Pro callback: its reference names the verification,
            // its event is the word, and its delivery id is the one the live
            // path stamped as the verification's last event.
            var root = json.RootElement;
            note = await identity.HandleAsync(
                root.GetProperty("reference").GetString()!,
                root.GetProperty("event").GetString()!,
                IdentityProviderRequests.DeclineReason(IdentityProviders.ShuftiPro, root),
                delivery.DeliveryId,
                ct);
        }
        else if (delivery.Source == WebhookDelivery.Identity)
        {
            // The provider's verdict, taken again down the live path; the
            // event id is what makes a verdict already applied a no-op.
            var root = json.RootElement;
            note = await identity.HandleAsync(
                root.GetProperty("session_id").GetString()!,
                root.GetProperty("status").GetString()!,
                IdentityProviderRequests.DeclineReason(root),
                root.TryGetProperty("event_id", out var e) ? e.GetString() : null,
                ct);
        }
        else
        {
            (note, touchedSlug, buildQueued) = await GitHubWebhookService.HandleAsync(
                db, delivery.Event, json.RootElement, delivery.RepoFullName,
                logFactory.CreateLogger("GitHubWebhookReplay"), ct, replay: true);
        }

        // The note keeps both halves of the story: what the live handling
        // did, and what the replay did — "no matching entry" followed by
        // "claimed m2" is the whole diagnosis in one line.
        delivery.HandledNote = Truncate(
            $"{delivery.HandledNote ?? "?"} → replay {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm}Z: {note}");
        await db.SaveChangesAsync(ct);

        // A replayed claim moves the board like a live one would, and queues its build the same way.
        if (touchedSlug is not null) await live.OpportunityChangedAsync(touchedSlug, ct);
        if (buildQueued) previewSignal.Wake();
        return Outcome.Ok(new ReplayDeliveryResponse { Note = delivery.HandledNote });
    }

    private static string Truncate(string s) => s.Length > 400 ? "…" + s[^399..] : s;
}
