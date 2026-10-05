using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WinnersPortal.Domain;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Services.Common;
using WinnersPortal.Services.Opportunities;
using WinnersPortal.Services.Live;
using WinnersPortal.Services.Preview;
using WinnersPortal.Services.Settings;

namespace WinnersPortal.Services.GitHub;

/// <summary>
/// The webhook fan-in. GitHub (via the smee tunnel in dev) posts every event
/// from the opportunity organisation here; the handler verifies the HMAC
/// signature, stores the raw delivery in jsonb, and stamps checkpoints —
/// a pushed tag <c>m2</c> or an opened pull request claims a milestone.
/// Everything is idempotent because GitHub redelivers.
/// </summary>
public sealed class GitHubWebhookService(
    AppDbContext db, SettingsService settings, ILiveBoard live, PreviewWorkSignal previewSignal, ILoggerFactory logFactory)
{
    /// <param name="payload">The body exactly as it arrived — the signature is over these bytes.</param>
    /// <param name="signature">The X-Hub-Signature-256 header.</param>
    /// <param name="eventName">The X-GitHub-Event header, empty when absent.</param>
    /// <param name="deliveryId">The X-GitHub-Delivery header, empty when absent.</param>
    public async Task<Outcome<WebhookResponse>> ReceiveAsync(byte[] payload, string? signature, string eventName, string deliveryId, CancellationToken ct)
    {
        var log = logFactory.CreateLogger("GitHubWebhook");

        // Every setup's secret, active or not: an inactive setup still has
        // repositories in its organization, and their pushes still claim
        // milestones. A delivery signed by any of them is real.
        var secrets = (await settings.SetupsAsync(Setups.GitHub, ct))
            .Select(s => s.Get("github.webhookSecret"))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (secrets.Count == 0)
        {
            log.LogWarning("Webhook received but no GitHub setup has a webhook secret; dropping it.");
            return Outcome.Unavailable();
        }

        if (!secrets.Any(secret => WebhookSignature.Verify(secret!, payload, signature)))
        {
            log.LogWarning("Webhook signature verification failed; dropping the delivery.");
            return Outcome.Unauthorized();
        }

        if (eventName.Length == 0 || deliveryId.Length == 0)
            return Outcome.Invalid("Missing X-GitHub-Event or X-GitHub-Delivery.");

        using var json = JsonDocument.Parse(payload);
        var root = json.RootElement;
        var action = root.TryGetProperty("action", out var a) ? a.GetString() : null;
        var repoFullName = root.TryGetProperty("repository", out var repo)
            && repo.TryGetProperty("full_name", out var fn) ? fn.GetString() : null;

        // Stamp the delivery first. The unique index on DeliveryId turns a
        // redelivery into a constraint hit — INSERT … ON CONFLICT in spirit.
        var delivery = new WebhookDelivery
        {
            Id = Guid.NewGuid(),
            DeliveryId = deliveryId,
            Event = eventName,
            Action = action,
            RepoFullName = repoFullName,
            Payload = System.Text.Encoding.UTF8.GetString(payload),
            ReceivedAtUtc = DateTimeOffset.UtcNow,
        };
        db.WebhookDeliveries.Add(delivery);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (DbErrors.IsUniqueViolation(e))
        {
            return Outcome.Ok(new WebhookResponse { Ok = true, Note = "duplicate delivery" });
        }

        var (note, touchedSlug, buildQueued) = await HandleAsync(db, eventName, root, repoFullName, log, ct);
        delivery.HandledNote = note;
        await db.SaveChangesAsync(ct);

        // After the save, so a watcher's refetch reads the claim it was
        // woken for. The nudge carries no data — the page refetches.
        if (touchedSlug is not null) await live.OpportunityChangedAsync(touchedSlug, ct);
        // Likewise the worker: the row it wakes for is committed.
        if (buildQueued) previewSignal.Wake();
        return Outcome.Ok(new WebhookResponse { Ok = true, Note = delivery.HandledNote });
    }

    /// <summary>
    /// The whole dispatch, shared with the admin console's replay action —
    /// a replayed delivery must take exactly the live path, or replaying
    /// proves nothing. Claims are safe to run twice by design (a unique
    /// index makes the second a no-op); the push-activity stamps are not,
    /// so a replay skips them — restamping LastPushAtUtc with "now" would
    /// make an abandoned entry look freshly active.
    ///
    /// Returns the slug of the opportunity the delivery touched (or null), so
    /// the caller can nudge that opportunity's live watchers once its own save
    /// has committed — a nudge before the commit wakes pages into refetching
    /// the world as it was — and whether a claim queued a build, so the
    /// caller can wake the preview worker the same way.
    /// </summary>
    internal static async Task<(string Note, string? OpportunitySlug, bool BuildQueued)> HandleAsync(
        AppDbContext db,
        string eventName,
        JsonElement root,
        string? repoFullName,
        ILogger log,
        CancellationToken ct,
        bool replay = false)
    {
        switch (eventName)
        {
            case "ping":
                return ("pong", null, false);

            case "push":
            {
                var entry = await FindEntryAsync(db, repoFullName, ct);
                if (entry is null) return ("no matching entry", null, false);

                var slug = entry.Opportunity!.Slug;
                var reference = root.TryGetProperty("ref", out var r) ? r.GetString() : null;
                var now = DateTimeOffset.UtcNow;
                if (!replay)
                {
                    entry.LastPushAtUtc = now;
                    if (root.TryGetProperty("commits", out var commits) && commits.ValueKind == JsonValueKind.Array)
                        entry.PushCount += Math.Max(commits.GetArrayLength(), 1);
                    else
                        entry.PushCount += 1;
                }

                if (MilestoneRefs.FromTag(reference) is { } number)
                {
                    var sha = root.TryGetProperty("after", out var after) ? after.GetString() : null;
                    var tag = reference![("refs/tags/".Length)..];
                    var (claimed, queued) = await ClaimAsync(db, entry, number, sha, "tag", tag, now, log, ct);
                    return (claimed, slug, queued);
                }
                return (replay ? "activity not restamped on replay" : "activity recorded", replay ? null : slug, false);
            }

            case "create":
            {
                // Some clients produce a create event without a matching push.
                if (root.TryGetProperty("ref_type", out var refType) && refType.GetString() == "tag"
                    && MilestoneRefs.FromTag(root.GetProperty("ref").GetString()) is { } number)
                {
                    var entry = await FindEntryAsync(db, repoFullName, ct);
                    if (entry is null) return ("no matching entry", null, false);
                    var (claimed, queued) = await ClaimAsync(db, entry, number, null, "tag",
                        root.GetProperty("ref").GetString()!, DateTimeOffset.UtcNow, log, ct);
                    return (claimed, entry.Opportunity!.Slug, queued);
                }
                return ("ignored", null, false);
            }

            case "pull_request":
            {
                var action = root.TryGetProperty("action", out var a) ? a.GetString() : null;
                if (action is not ("opened" or "reopened" or "ready_for_review")) return ("ignored", null, false);

                var pr = root.GetProperty("pull_request");
                var branch = pr.GetProperty("head").TryGetProperty("ref", out var b) ? b.GetString() : null;
                var title = pr.TryGetProperty("title", out var t) ? t.GetString() : null;
                if (MilestoneRefs.FromPullRequest(branch, title) is not { } number) return ("no milestone in PR", null, false);

                var entry = await FindEntryAsync(db, repoFullName, ct);
                if (entry is null) return ("no matching entry", null, false);
                var sha = pr.GetProperty("head").TryGetProperty("sha", out var s) ? s.GetString() : null;
                var (claimed, queued) = await ClaimAsync(db, entry, number, sha, "pull_request",
                    branch ?? title ?? "", DateTimeOffset.UtcNow, log, ct);
                return (claimed, entry.Opportunity!.Slug, queued);
            }

            case "installation":
            case "installation_repositories":
                log.LogInformation("GitHub App installation event: {Action}.",
                    root.TryGetProperty("action", out var act) ? act.GetString() : "?");
                return ("noted", null, false);

            default:
                return ("ignored", null, false);
        }
    }

    private static Task<Entry?> FindEntryAsync(AppDbContext db, string? repoFullName, CancellationToken ct) =>
        repoFullName is null
            ? Task.FromResult<Entry?>(null)
            : db.Entries.Include(e => e.Opportunity)
                .SingleOrDefaultAsync(e => e.RepoFullName == repoFullName && e.Status == EntryStatus.Active, ct);

    /// <summary>The note for the delivery, and whether the claim queued a build for the preview worker.</summary>
    private static async Task<(string Note, bool BuildQueued)> ClaimAsync(
        AppDbContext db,
        Entry entry,
        int number,
        string? sha,
        string via,
        string reference,
        DateTimeOffset now,
        ILogger log,
        CancellationToken ct)
    {
        // Paid by milestone, a claim is the hired freelancer handing the
        // milestone in: only the one being worked on, and again after the
        // client asked for changes. Its own rules, its own save.
        if (MilestonePay.ByMilestone(entry.Opportunity!.Kind))
        {
            var (problem, handedIn, again) = await MilestonePaymentService.HandInAsync(
                db, entry, entry.Opportunity, number - 1, via, reference, sha, now, ct);
            if (problem is not null) return ($"m{number} ignored — {problem}", false);
            var queued = handedIn!.BuildStatus == PreviewBuildStatus.Pending;
            log.LogInformation("Checkpoint: {Repo} handed in m{Number} via {Via}{Again}.",
                entry.RepoFullName, number, via, again ? " again" : "");
            return ($"{(again ? "handed in again" : "handed in")} m{number} via {via}" + (queued ? ", build queued" : ""), queued);
        }

        // Claims land only while the opportunity runs; a tag pushed after the
        // freeze (or into a review) records activity but moves no board.
        if (entry.Opportunity.Status != OpportunityStatus.Open) return ($"m{number} ignored — opportunity not open", false);

        var milestone = await db.Milestones
            .SingleOrDefaultAsync(m => m.OpportunityId == entry.OpportunityId && m.Order == number - 1, ct);
        if (milestone is null) return ($"m{number} ignored — opportunity has no milestone {number}", false);

        // An opportunity that requires Docker Compose has the claimed commit
        // built; a claim with no commit (a bare create event) has nothing
        // to build and says so. The worker is woken after the save.
        var build = entry.Opportunity.RequiresCompose && sha is not null;
        db.Checkpoints.Add(new Checkpoint
        {
            Id = Guid.NewGuid(),
            EntryId = entry.Id,
            MilestoneId = milestone.Id,
            CommitSha = sha,
            Via = via,
            Ref = reference,
            ClaimedAtUtc = now,
            BuildStatus = build ? PreviewBuildStatus.Pending : PreviewBuildStatus.None,
            BuildDueAtUtc = build ? now : null,
        });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (DbErrors.IsUniqueViolation(e))
        {
            // First claim wins; a redelivery or a second tag push is a no-op.
            db.ChangeTracker.Entries<Checkpoint>()
                .Where(c => c.State == EntityState.Added).ToList()
                .ForEach(c => c.State = EntityState.Detached);
            return ($"m{number} already claimed", false);
        }
        log.LogInformation("Checkpoint: {Repo} claimed m{Number} via {Via}.", entry.RepoFullName, number, via);
        var note = $"claimed m{number} via {via}"
            + (build ? ", build queued" : entry.Opportunity.RequiresCompose ? ", no commit to build" : "");
        return (note, build);
    }
}
