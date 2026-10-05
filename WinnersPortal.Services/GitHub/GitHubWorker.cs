using WinnersPortal.Services.Live;
using WinnersPortal.Services.Opportunities;
using Microsoft.EntityFrameworkCore;
using WinnersPortal.Services.Ai;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Services.Common;
using WinnersPortal.Domain;
using WinnersPortal.Services.Email;

namespace WinnersPortal.Services.GitHub;

/// <summary>
/// Lets request handlers nudge the worker instead of waiting a full sweep —
/// a new entry starts provisioning in seconds, not half a minute.
/// </summary>
public sealed class GitHubWorkSignal(WorkRelay? relay = null) : WorkSignal("github", relay);

/// <summary>
/// The background half of the GitHub integration — the blueprint's Hangfire
/// jobs as one hosted sweep, because no HTTP request should ever wait on
/// GitHub. Each cycle it: provisions repos for new entries (retried with
/// backoff, so a GitHub hiccup delays a repo by minutes without breaking the
/// join flow), flips past-deadline opportunities to Reviewing, freezes each entry
/// (final tag, push revoked), grants the client's connected account read
/// access for review, archives withdrawn and losing repos, and verifies
/// pending transfers by re-reading the repository — only this code may write
/// the handover's final state.
/// </summary>
public sealed class GitHubWorker(
    IServiceScopeFactory scopes,
    GitHubService github,
    GitHubWorkSignal signal,
    EmailWorkSignal emailSignal,
    ILiveBoard live,
    ILogger<GitHubWorker> log,
    AppPause pause) : BackgroundService
{
    private const int MaxProvisionAttempts = 8;
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long GitHub holds a transfer to a personal account for the
    /// recipient to accept before the invitation lapses.
    /// </summary>
    private static readonly TimeSpan TransferInvitationLife = TimeSpan.FromDays(1);

    /// <summary>
    /// Transfer steps that failed at GitHub, and when each may be tried
    /// again. In memory, deliberately: a restart forgets it and tries once
    /// more, and Restart handover clears the note, which clears this too. A
    /// token GitHub keeps refusing must not be sent back every thirty
    /// seconds — repeated bad credentials are what GitHub blocks an address for.
    /// </summary>
    private readonly Dictionary<Guid, (int Failures, DateTimeOffset NextAt)> _transferBackoff = [];

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
                log.LogError(e, "GitHub worker cycle failed; retrying next sweep.");
            }
            await signal.WaitAsync(SweepInterval, ct);
        }
    }

    private async Task RunCycleAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow;

        // The deadline is a portal fact, not a GitHub fact — opportunities move to
        // review on time even when the integration is not configured.
        var toReview = await db.Opportunities
            .Include(c => c.Client)
            .Include(c => c.Entries.Where(e => e.Status == EntryStatus.Active))
            .ThenInclude(e => e.Freelancer)
            // Paid by milestone there is no review to move to: the client
            // hires while it is open, and the milestones' own dates run the
            // work after that.
            .Where(c => c.Status == OpportunityStatus.Open && c.Kind == OpportunityKind.Competitive
                && c.DeadlineUtc != null && c.DeadlineUtc <= now)
            .ToListAsync(ct);
        foreach (var opportunity in toReview)
        {
            opportunity.Status = OpportunityStatus.Reviewing;
            log.LogInformation("Opportunity '{Slug}' hit its deadline; moved to review.", opportunity.Slug);

            // The deadline is the moment both sides are waiting on someone
            // else — the client on themselves, the entrants on the client.
            Notify.Queue(db, opportunity.Client!, "opportunity_review_client",
                Emails.OpportunityInReviewClient(opportunity.Title, opportunity.Slug, opportunity.Entries.Count, opportunity.Delivery));
            foreach (var entry in opportunity.Entries)
                Notify.Queue(db, entry.Freelancer!, "opportunity_review_entrant",
                    Emails.OpportunityInReviewEntrant(opportunity.Title, opportunity.Slug, opportunity.Delivery));
        }
        if (toReview.Count > 0)
        {
            // Applicants nobody answered in time: the deadline closed the
            // door on them too, and they are told rather than left waiting.
            var closedIds = toReview.Select(c => c.Id).ToList();
            var undecided = await db.Applications
                .Include(a => a.Freelancer)
                .Where(a => closedIds.Contains(a.OpportunityId) && a.Status == ApplicationStatus.UnderReview)
                .ToListAsync(ct);
            foreach (var application in undecided)
                Notify.Queue(db, application.Freelancer!, "application_closed_undecided",
                    Emails.ApplicationClosedUndecided(
                        toReview.First(c => c.Id == application.OpportunityId).Title, cancelled: false));
            await db.SaveChangesAsync(ct);
            emailSignal.Wake();
            // Open tabs see "entry closed" the minute it is true, not on reload.
            foreach (var opportunity in toReview) await live.OpportunityChangedAsync(opportunity.Slug, ct);
        }

        if (!await github.IsConfiguredAsync(ct)) return;

        await ProvisionPendingAsync(db, now, ct);
        await ReadTreeFactsAsync(db, now, ct);
        await FreezeReviewingAsync(db, now, ct);
        await GrantReviewAccessAsync(db, now, ct);
        await ArchiveDeadReposAsync(db, now, ct);
        await RevokeRemovedAccessAsync(db, now, ct);
        await VerifyTransfersAsync(db, now, ct);
    }

    // ---------------------------------------------------------- provision

    private async Task ProvisionPendingAsync(AppDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var pending = await db.Entries
            .Include(e => e.Opportunity).ThenInclude(c => c!.Milestones.OrderBy(m => m.Order))
            .Include(e => e.Freelancer)
            .Where(e => e.Status == EntryStatus.Active
                && e.ProvisionStatus == RepoProvisionStatus.Pending
                // An upload-only opportunity hands out no repositories; its
                // entries stay Pending forever and that is not a backlog.
                && e.Opportunity!.Delivery != OpportunityDelivery.Upload
                // Paid by milestone, the one entry is made by the hire, the
                // moment the opportunity turns awarded.
                && (e.Opportunity!.Status == OpportunityStatus.Open
                    || (e.Opportunity.Kind == OpportunityKind.Milestones && e.Opportunity.Status == OpportunityStatus.Awarded)))
            .OrderBy(e => e.CreatedAtUtc)
            .Take(10)
            .ToListAsync(ct);

        foreach (var entry in pending)
        {
            // Exponential backoff: 2^attempts minutes, capped at an hour.
            if (entry.ProvisionAttemptedAtUtc is { } last)
            {
                var wait = TimeSpan.FromMinutes(Math.Min(Math.Pow(2, entry.ProvisionAttempts), 60));
                if (now - last < wait) continue;
            }

            entry.ProvisionAttempts++;
            entry.ProvisionAttemptedAtUtc = now;
            try
            {
                // Create is skipped on retry when a previous attempt already
                // made the repo — each step below is idempotent, so a partial
                // failure heals instead of duplicating.
                if (entry.RepoFullName is null)
                {
                    var name = RepoNames.For(entry.Opportunity!.Slug, entry.GithubUsername);
                    GitHubRepo repo;
                    try
                    {
                        repo = await github.CreateOrgRepoAsync(name, entry.Opportunity.Title, ct);
                    }
                    catch (GitHubApiException e) when (e.StatusCode == 422)
                    {
                        // Name taken (a withdrawn re-entry, usually) — suffix with the entry id.
                        repo = await github.CreateOrgRepoAsync(
                            RepoNames.For(entry.Opportunity.Slug, entry.GithubUsername + "-" + entry.Id.ToString("N")[..6]),
                            entry.Opportunity.Title, ct);
                    }
                    entry.RepoFullName = repo.FullName;
                    entry.RepoId = repo.Id;
                    entry.DefaultBranch = repo.DefaultBranch;
                    await db.SaveChangesAsync(ct); // record the repo before anything else can fail
                }

                try
                {
                    await github.PutFileAsync(entry.RepoFullName, "README.md",
                        SeedReadme(entry), "Seed the opportunity brief and milestones", ct);
                }
                catch (GitHubApiException e) when (e.StatusCode == 422)
                {
                    // README already there from a previous attempt.
                }

                await github.SetCollaboratorAsync(entry.RepoFullName, entry.GithubUsername, "push", ct);

                entry.ProvisionStatus = RepoProvisionStatus.Provisioned;
                entry.ProvisionNote = null;
                log.LogInformation("Provisioned {Repo} for entry {EntryId}.", entry.RepoFullName, entry.Id);

                Notify.Queue(db, entry.Freelancer!, "repo_ready", Emails.RepoReady(
                    entry.Opportunity!.Title, entry.Opportunity.Slug, entry.RepoFullName!,
                    entry.GithubUsername, entry.Opportunity.DeadlineUtc));
                emailSignal.Wake();
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                entry.ProvisionNote = e.Message.Length > 380 ? e.Message[..380] : e.Message;
                if (entry.ProvisionAttempts >= MaxProvisionAttempts)
                {
                    entry.ProvisionStatus = RepoProvisionStatus.Failed;
                    log.LogError(e, "Provisioning entry {EntryId} failed permanently after {Attempts} attempts.",
                        entry.Id, entry.ProvisionAttempts);

                    // The entrant hears it plainly; the operators get the
                    // error, because only they can act on it.
                    Notify.Queue(db, entry.Freelancer!, "repo_failed",
                        Emails.RepoFailed(entry.Opportunity!.Title, entry.Opportunity.Slug));
                    foreach (var admin in await db.Users.Where(u => u.Role == Roles.Admin).ToListAsync(ct))
                        Notify.Queue(db, admin, "repo_failed_admin", Emails.RepoFailedAdmin(
                            entry.Opportunity.Title, entry.GithubUsername,
                            entry.ProvisionAttempts, entry.ProvisionNote));
                    emailSignal.Wake();
                }
                else
                {
                    log.LogWarning(e, "Provisioning entry {EntryId} failed (attempt {Attempts}); will retry.",
                        entry.Id, entry.ProvisionAttempts);
                }
            }
            await db.SaveChangesAsync(ct);
        }
    }

    private static string SeedReadme(Entry entry)
    {
        var opportunity = entry.Opportunity!;
        var milestones = string.Join("\n", opportunity.Milestones.Select((m, i) =>
            $"{i + 1}. **{m.Title}**{(string.IsNullOrEmpty(m.Description) ? "" : " — " + m.Description)}" +
            $"\n   Claim it when it works: `git tag m{i + 1} && git push origin m{i + 1}`"));
        return $"""
            # {opportunity.Title}

            Your private opportunity repository. Nobody else in the opportunity can see it —
            the client gets read access only after the deadline.

            **Award:** {opportunity.AwardAmount:0} {opportunity.Currency} · **Deadline:** {opportunity.DeadlineUtc:yyyy-MM-dd HH:mm} UTC

            ## The brief

            {opportunity.BriefMarkdown}

            ## Milestones

            Each milestone is claimed from inside this repo: push a tag `m1`, `m2`, …
            (or open a pull request from a branch named `m1-…`) and the portal's board
            updates within seconds. The first claim per milestone counts.

            {milestones}
            """;
    }

    // ------------------------------------------------------------- freeze

    // ---------------------------------------------------------- tree facts

    /// <summary>
    /// What each repository's file listing shows — tests, a README, CI, how
    /// many files — read once per push rather than once per page view. The
    /// standing score reads the stored answer, so no board ever waits on
    /// GitHub. Five repositories a sweep, the longest-stale first.
    /// </summary>
    private async Task ReadTreeFactsAsync(AppDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var stale = await db.Entries
            .Where(e => e.Status == EntryStatus.Active
                && e.ProvisionStatus == RepoProvisionStatus.Provisioned
                && e.RepoFullName != null
                && e.LastPushAtUtc != null
                && (e.TreeReadAtUtc == null || e.TreeReadAtUtc < e.LastPushAtUtc)
                && (e.Opportunity!.Status == OpportunityStatus.Open || e.Opportunity.Status == OpportunityStatus.Reviewing))
            .OrderBy(e => e.LastPushAtUtc)
            .Take(5)
            .ToListAsync(ct);

        foreach (var entry in stale)
        {
            try
            {
                var gitRef = entry.FrozenAtUtc is not null
                    ? "tags/final"
                    : $"heads/{entry.DefaultBranch ?? "main"}";
                var sha = await github.GetRefShaAsync(entry.RepoFullName!, gitRef, ct);
                if (sha is not null)
                {
                    var paths = await github.GetTreePathsAsync(entry.RepoFullName!, sha, 2000, ct);
                    var (count, tests, readme, ci, compose) = AiInputs.TreeFacts(paths);
                    entry.TreeFileCount = count;
                    entry.TreeHasTests = tests;
                    entry.TreeHasReadme = readme;
                    entry.TreeHasCi = ci;
                    entry.TreeHasCompose = compose;
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Stamped anyway: the next push re-reads, and a repository
                // GitHub will not show us must not be asked every sweep.
                log.LogWarning(e, "Reading the tree of {Repo} failed; keeping the last facts.", entry.RepoFullName);
            }
            entry.TreeReadAtUtc = now;
        }
        if (stale.Count > 0) await db.SaveChangesAsync(ct);
    }

    private async Task FreezeReviewingAsync(AppDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var toFreeze = await db.Entries
            .Include(e => e.Opportunity)
            .Where(e => e.Status == EntryStatus.Active
                && e.ProvisionStatus == RepoProvisionStatus.Provisioned
                && e.FrozenAtUtc == null
                && ((e.Opportunity!.Kind == OpportunityKind.Competitive
                        && (e.Opportunity.Status == OpportunityStatus.Reviewing || e.Opportunity.Status == OpportunityStatus.Awarded))
                    // Paid by milestone the hired freelancer pushes until the
                    // last milestone is paid, and the repository freezes then,
                    // on its way to the client.
                    || (e.Opportunity.Kind == OpportunityKind.Milestones
                        && db.Awards.Any(a => a.EntryId == e.Id && a.PaidAtUtc != null))))
            .Take(10)
            .ToListAsync(ct);

        foreach (var entry in toFreeze)
        {
            try
            {
                await github.CreateTagAsync(entry.RepoFullName!, "final", ct);
                await github.SetCollaboratorAsync(entry.RepoFullName!, entry.GithubUsername, "pull", ct);
                entry.FrozenAtUtc = now;
                log.LogInformation("Froze {Repo}: final tag, push revoked.", entry.RepoFullName);
            }
            catch (GitHubApiException e) when (e.StatusCode == 404)
            {
                entry.FrozenAtUtc = now; // repo vanished — nothing left to freeze
                log.LogWarning("Freezing {Repo}: repository not found; marked frozen.", entry.RepoFullName);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogWarning(e, "Freezing {Repo} failed; will retry next sweep.", entry.RepoFullName);
            }
        }
        if (toFreeze.Count > 0) await db.SaveChangesAsync(ct);
    }

    // ------------------------------------------------------ review access

    private async Task GrantReviewAccessAsync(AppDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        // The client is the reviewer, and review happens in GitHub's own UI.
        // Requires their connected account — which is why connecting is
        // prompted at publish and hard-required before announcing.
        var due = await db.Entries
            .Include(e => e.Opportunity).ThenInclude(c => c!.Client)
            .Where(e => e.Status == EntryStatus.Active
                && e.ProvisionStatus == RepoProvisionStatus.Provisioned
                && e.ReviewAccessGrantedAtUtc == null
                && ((e.FrozenAtUtc != null
                        && (e.Opportunity!.Status == OpportunityStatus.Reviewing || e.Opportunity.Status == OpportunityStatus.Awarded))
                    // Paid by milestone the client reads the work as it
                    // grows — each milestone is reviewed before it is paid.
                    || (e.Opportunity!.Kind == OpportunityKind.Milestones && e.Opportunity.Status == OpportunityStatus.Awarded))
                && e.Opportunity.Client!.GithubLogin != null)
            .Take(10)
            .ToListAsync(ct);

        foreach (var entry in due)
        {
            try
            {
                await github.SetCollaboratorAsync(
                    entry.RepoFullName!, entry.Opportunity!.Client!.GithubLogin!, "pull", ct);
                entry.ReviewAccessGrantedAtUtc = now;
                log.LogInformation("Granted {Client} read access to {Repo} for review.",
                    entry.Opportunity.Client.GithubLogin, entry.RepoFullName);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogWarning(e, "Review access for {Repo} failed; will retry next sweep.", entry.RepoFullName);
            }
        }
        if (due.Count > 0) await db.SaveChangesAsync(ct);
    }

    // ------------------------------------------------------------ archive

    private async Task ArchiveDeadReposAsync(AppDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        // Withdrawn and removed repos archive immediately; losing repos
        // archive once a winner is announced; a cancelled opportunity archives
        // every entry. In all of them, work never changes hands and never
        // disappears. For a removal the archive is also the revocation that
        // matters most: an archived repository takes no pushes from anyone.
        var winners = db.Awards.Select(a => a.EntryId);
        var dead = await db.Entries
            .Where(e => e.RepoFullName != null && e.ArchivedAtUtc == null
                && (e.Status == EntryStatus.Withdrawn
                    || e.Status == EntryStatus.Removed
                    || e.Status == EntryStatus.Deselected
                    || e.Opportunity!.Status == OpportunityStatus.Cancelled
                    || (e.Opportunity.Status == OpportunityStatus.Awarded && !winners.Contains(e.Id))))
            .Take(10)
            .ToListAsync(ct);

        foreach (var entry in dead)
        {
            try
            {
                await github.ArchiveRepoAsync(entry.RepoFullName!, ct);
                entry.ArchivedAtUtc = now;
                log.LogInformation("Archived {Repo}.", entry.RepoFullName);
            }
            catch (GitHubApiException e) when (e.StatusCode == 404)
            {
                entry.ArchivedAtUtc = now;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogWarning(e, "Archiving {Repo} failed; will retry next sweep.", entry.RepoFullName);
            }
        }
        if (dead.Count > 0) await db.SaveChangesAsync(ct);
    }

    // ------------------------------------------------- removal follow-up

    /// <summary>
    /// The second half of a removal. Archiving already stopped every push;
    /// this is what ends the removed entrant's read access, once the window
    /// the email promised them has run out. Late rather than immediate on
    /// purpose: being told to leave should not also mean losing the only
    /// copy of a week's work.
    /// </summary>
    private async Task RevokeRemovedAccessAsync(AppDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var due = await db.Entries
            .Where(e => e.Status == EntryStatus.Removed
                && e.RepoFullName != null
                && e.AccessRevokedAtUtc == null
                && e.RepoAccessEndsAtUtc != null
                && e.RepoAccessEndsAtUtc <= now)
            .Take(10)
            .ToListAsync(ct);

        foreach (var entry in due)
        {
            try
            {
                // Also cancels an invitation they never accepted — same call.
                await github.RemoveCollaboratorAsync(entry.RepoFullName!, entry.GithubUsername, ct);
                entry.AccessRevokedAtUtc = now;
                log.LogInformation("Revoked {User}'s access to {Repo} after removal.",
                    entry.GithubUsername, entry.RepoFullName);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogWarning(e, "Revoking access to {Repo} failed; will retry next sweep.", entry.RepoFullName);
            }
        }
        if (due.Count > 0) await db.SaveChangesAsync(ct);
    }

    // ---------------------------------------------------------- transfers

    private async Task VerifyTransfersAsync(AppDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var open = await db.Awards
            .Include(a => a.Entry)
            .Include(a => a.Opportunity).ThenInclude(c => c!.Client)
            .Where(a => a.PaidAtUtc != null && a.Handover == HandoverStatus.Requested)
            .Take(10)
            .ToListAsync(ct);

        foreach (var award in open)
        {
            var entry = award.Entry!;
            if (entry.RepoFullName is null || award.TransferTargetLogin is null) continue;
            if (award.HandoverNote is null) _transferBackoff.Remove(award.Id); // restarted, or never failed
            if (_transferBackoff.TryGetValue(award.Id, out var wait) && wait.NextAt > now) continue;
            try
            {
                if (award.TransferRequestedAtUtc is null)
                {
                    // A missing or unusable token is known without asking
                    // GitHub, so it is checked every sweep at no cost — and
                    // the transfer goes the sweep after somebody fixes it.
                    if (await github.TransferBlockerAsync(entry.RepoFullName, ct) is { } blocker)
                    {
                        award.HandoverNote = Clip(blocker);
                        continue;
                    }

                    await github.TransferRepoAsync(entry.RepoFullName, award.TransferTargetLogin, ct);
                    award.TransferRequestedAtUtc = now;
                    award.HandoverNote = null;
                    _transferBackoff.Remove(award.Id);
                    log.LogInformation("Transfer of {Repo} to {Target} requested.",
                        entry.RepoFullName, award.TransferTargetLogin);

                    // GitHub holds a transfer to a personal account until the
                    // recipient accepts — tell the client to go accept it.
                    Notify.Queue(db, award.Opportunity!.Client!, "transfer_requested", Emails.TransferRequested(
                        award.Opportunity.Title, award.Opportunity.Slug, entry.RepoFullName, award.TransferTargetLogin));
                    emailSignal.Wake();
                    continue; // verify on a later sweep — the 202 is not the finish line
                }

                // A transfer to a personal account sits pending until the
                // recipient accepts it. Re-read the repository and only write
                // the final state once it has actually moved.
                var origin = entry.RepoFullName;
                var org = origin.Split('/')[0];
                var repo = await github.GetRepoAsync(origin, ct);
                var sight = GitHubService.ReadTransfer(repo, award.TransferTargetLogin);
                if (sight == GitHubService.TransferSight.Left && entry.RepoId is { } repoId
                    && await github.FindMovedRepoAsync(origin, repoId, ct) is { } moved)
                {
                    repo = moved;
                    sight = GitHubService.ReadTransfer(moved, award.TransferTargetLogin);
                }
                _transferBackoff.Remove(award.Id);

                switch (sight)
                {
                    case GitHubService.TransferSight.Arrived:
                        entry.RepoFullName = repo!.FullName; // owner changed; keep links working
                        award.Handover = HandoverStatus.Verified;
                        award.HandoverVerifiedAtUtc = now;
                        award.HandoverNote = null;
                        log.LogInformation("Handover verified: {Repo} now belongs to {Target}.",
                            repo.FullName, award.TransferTargetLogin);
                        break;

                    case GitHubService.TransferSight.Left:
                        // The App is installed on the organization, not on the
                        // client's own account, so a private repository that
                        // has landed there reads as a 404. Nothing but the
                        // requested transfer takes an entry repository out of
                        // the organization — the portal never deletes one — so
                        // gone is landed; the note says it was not seen.
                        entry.RepoFullName = $"{award.TransferTargetLogin}/{origin.Split('/')[1]}";
                        award.Handover = HandoverStatus.Verified;
                        award.HandoverVerifiedAtUtc = now;
                        award.HandoverNote = Clip($"Verified when the repository left {org}: GitHub does not let the "
                            + $"portal read a private repository in @{award.TransferTargetLogin}'s own account, so the "
                            + "new owner was not seen directly.");
                        log.LogInformation("Handover verified: {Repo} left {Org} after its transfer to {Target}.",
                            origin, org, award.TransferTargetLogin);
                        break;

                    default:
                        award.HandoverNote = repo is not null
                            && !string.Equals(repo.OwnerLogin, org, StringComparison.OrdinalIgnoreCase)
                            ? Clip($"The repository now belongs to @{repo.OwnerLogin}, not @{award.TransferTargetLogin} — "
                                + "it was moved outside the portal.")
                            : now - award.TransferRequestedAtUtc.Value > TransferInvitationLife
                                ? Clip($"Still in {org} a day after the transfer was requested, and GitHub lets a transfer "
                                    + $"invitation lapse after a day. If @{award.TransferTargetLogin} did not accept it, "
                                    + "Restart sends another.")
                                : null;
                        break;
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                award.HandoverNote = Clip(e.Message);
                var failures = (_transferBackoff.TryGetValue(award.Id, out var before) ? before.Failures : 0) + 1;
                var delay = TimeSpan.FromMinutes(Math.Min(60, Math.Pow(2, Math.Min(failures - 1, 6))));
                _transferBackoff[award.Id] = (failures, now + delay);
                log.LogWarning(e, "Transfer step for {Repo} failed; trying again in {Delay}.", entry.RepoFullName, delay);
            }
        }
        if (open.Count > 0) await db.SaveChangesAsync(ct);
    }

    /// <summary>A note as the award keeps it — the column holds 400.</summary>
    private static string Clip(string note) => note.Length > 380 ? note[..380] : note;
}
