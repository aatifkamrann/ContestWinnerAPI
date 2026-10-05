using System.Text;
using Microsoft.EntityFrameworkCore;
using WinnersPortal.Domain;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Services.Common;
using WinnersPortal.Services.Email;
using WinnersPortal.Services.GitHub;
using WinnersPortal.Services.Live;
using WinnersPortal.Services.Settings;
using WinnersPortal.Services.Storage;

namespace WinnersPortal.Services.Preview;

/// <summary>Lets the webhook (a build queued) or a button (a preview asked for or stopped) nudge the worker, instead of waiting a sweep.</summary>
public sealed class PreviewWorkSignal(WorkRelay? relay = null) : WorkSignal("preview", relay);

/// <summary>
/// Builds each claimed milestone of an opportunity that requires Docker Compose.
/// The webhook queues a build on the checkpoint (Pending, due now); this
/// worker hands it to the build host with a short-lived token for the
/// repository, then follows it until the host says built or failed, keeps
/// the log — its tail on the row, the whole of it in storage — and nudges
/// the board. A hand-over the host refuses is retried with backoff and,
/// after <see cref="CheckpointBuilds.MaxAttempts"/> tries, marked failed
/// as unreachable; a build the host still calls running past its timeout
/// is given up. A failed build tells the entrant; an unreachable host
/// tells the log, because that is nobody's build to fix. Nothing new is
/// handed over while no build host setup is active and passing its test
/// (<see cref="BuildHostService"/>): the rows wait, Pending.
///
/// It carries the previews the same way: a Preview row asked for (Pending)
/// is handed to the host to check out and start; a starting or running one
/// is asked after every sweep — up, stopped by the host's idle stop, failed
/// — and a stop someone pressed is passed on. The person who pressed the
/// button is watching its dialog, so a preview emails nobody.
/// </summary>
public sealed class PreviewWorker(
    IServiceScopeFactory scopes,
    PreviewHostClient host,
    SettingsService settings,
    GitHubService github,
    StorageService storage,
    PreviewWorkSignal signal,
    EmailWorkSignal emailSignal,
    ILiveBoard live,
    ILogger<PreviewWorker> log,
    AppPause pause) : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(15);
    private const int StartBatch = 5;
    private const int FollowBatch = 20;

    // A preview that is up changes only when someone visits or it stops
    // itself, so it is asked once a minute rather than every sweep — each
    // question is a row in the activity log. One starting is asked every sweep.
    private static readonly TimeSpan RunningAskInterval = TimeSpan.FromMinutes(1);
    private readonly Dictionary<Guid, DateTimeOffset> runningAsked = [];

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // No cycle while a database move has the portal paused.
                using var lease = pause.TryEnter();
                if (lease is not null) await RunCycleAsync(ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError(e, "Preview worker cycle failed; retrying next sweep.");
            }
            await signal.WaitAsync(SweepInterval, ct);
        }
    }

    private async Task RunCycleAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var state = await scope.ServiceProvider.GetRequiredService<BuildHostService>().StateAsync(ct);
        if (state.Config is not { } config) return; // nothing to build on; the rows keep waiting

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow;
        // Nothing new goes to a host that has not passed its test: queued
        // builds and asked-for previews wait, Pending, until one does. What
        // is already there is still followed, and stopped when asked.
        if (state.Ready) await StartDueAsync(db, config, now, ct);
        await FollowAsync(db, config, now, ct);

        await StopRequestedAsync(db, config, now, ct);
        if (state.Ready) await StartRunsAsync(db, config, now, ct);
        await FollowRunsAsync(db, config, now, ct);
    }

    // ---------------------------------------------------------- hand-over

    private async Task StartDueAsync(AppDbContext db, PreviewHostConfig config, DateTimeOffset now, CancellationToken ct)
    {
        var due = await db.Checkpoints
            .Include(cp => cp.Entry!).ThenInclude(e => e.Opportunity)
            .Where(cp => cp.BuildStatus == PreviewBuildStatus.Pending && cp.BuildDueAtUtc != null && cp.BuildDueAtUtc <= now)
            .OrderBy(cp => cp.BuildDueAtUtc)
            .Take(StartBatch)
            .ToListAsync(ct);
        foreach (var cp in due)
        {
            var repo = cp.Entry?.RepoFullName;
            if (cp.CommitSha is null || repo is null)
            {
                // Queued by mistake — a claim with no commit, an entry with
                // no repository. Nothing to build, nobody to tell.
                Finish(cp, ok: false, error: "This claim carried no commit or no repository to build from.", tail: null, now);
                await db.SaveChangesAsync(ct);
                continue;
            }
            try
            {
                var token = await CloneTokenAsync(repo, ct);
                await host.StartAsync(config, cp.Id, repo, cp.CommitSha, token, ct);
                cp.BuildStatus = PreviewBuildStatus.Building;
                cp.BuildStartedAtUtc = now;
                cp.BuildDueAtUtc = null;
                cp.BuildError = null;
                log.LogInformation("Build of {Repo}@{Sha} handed to the build host.", repo, cp.CommitSha[..Math.Min(7, cp.CommitSha.Length)]);
            }
            catch (Exception e) when (IsTransient(e) && !ct.IsCancellationRequested)
            {
                cp.BuildAttempts++;
                if (cp.BuildAttempts >= CheckpointBuilds.MaxAttempts)
                {
                    // Not the entrant's doing: the row says so, the log says so, no email.
                    Finish(cp, ok: false, error: CheckpointBuilds.Unreachable(e.Message), tail: null, now);
                    log.LogError(e, "Giving up handing the build of {Repo} to the build host after {Attempts} tries.", repo, cp.BuildAttempts);
                    await NudgeAsync(cp, ct);
                }
                else
                {
                    cp.BuildDueAtUtc = now + CheckpointBuilds.Backoff(cp.BuildAttempts);
                    cp.BuildError = CheckpointBuilds.Cut(e.Message);
                    log.LogWarning(e, "Handing the build of {Repo} to the build host failed (try {Attempt}); retrying later.", repo, cp.BuildAttempts);
                }
            }
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// The token the host clones with: the setup that owns the repository
    /// mints one. No setup owns it — a portal with no GitHub App, whose
    /// entries were never provisioned here — and the host clones without
    /// one, as a public repository; GitHub itself being unreachable is a
    /// failure to retry, and is thrown.
    /// </summary>
    private async Task<string?> CloneTokenAsync(string repo, CancellationToken ct)
    {
        try
        {
            return await github.InstallationTokenForAsync(repo, ct);
        }
        catch (GitHubApiException e) when (e.StatusCode == 0)
        {
            log.LogInformation("No GitHub setup owns {Repo}; the build host clones it without a token.", repo);
            return null;
        }
    }

    // ------------------------------------------------------------ follow

    private async Task FollowAsync(AppDbContext db, PreviewHostConfig config, DateTimeOffset now, CancellationToken ct)
    {
        var running = await db.Checkpoints
            .Include(cp => cp.Entry!).ThenInclude(e => e.Opportunity)
            .Include(cp => cp.Entry!).ThenInclude(e => e.Freelancer)
            .Include(cp => cp.Milestone)
            .Where(cp => cp.BuildStatus == PreviewBuildStatus.Building)
            .OrderBy(cp => cp.BuildStartedAtUtc)
            .Take(FollowBatch)
            .ToListAsync(ct);
        foreach (var cp in running)
        {
            var finished = false;
            try
            {
                var answer = await host.StatusAsync(config, cp.Id, ct);
                switch (answer.Status)
                {
                    case PreviewHost.Queued or PreviewHost.Building
                        when CheckpointBuilds.TimedOut(cp.BuildStartedAtUtc, config.BuildTimeout, now):
                        // The host's own timeout should have ended it; past
                        // the grace on top, the portal stops waiting.
                        await FinishWithLogAsync(db, cp, config, ok: false,
                            error: CheckpointBuilds.TimedOutError(config.BuildTimeout), now, tellEntrant: true, ct);
                        finished = true;
                        break;
                    case PreviewHost.Queued or PreviewHost.Building:
                        break; // still going; next sweep
                    case PreviewHost.Built:
                        await FinishWithLogAsync(db, cp, config, ok: true, error: null, now, tellEntrant: false, ct);
                        finished = true;
                        break;
                    case PreviewHost.Failed:
                        await FinishWithLogAsync(db, cp, config, ok: false,
                            error: CheckpointBuilds.Verdict(answer), now, tellEntrant: true, ct);
                        finished = true;
                        break;
                    default:
                        await FinishWithLogAsync(db, cp, config, ok: false,
                            error: $"The build host answered with a state the portal does not know: “{answer.Status}”.", now, tellEntrant: false, ct);
                        finished = true;
                        break;
                }
            }
            catch (PreviewHostException e) when (e.StatusCode == 404)
            {
                // The host forgot it — a restart, most likely. Hand it over
                // again; a host that keeps forgetting is given up on.
                cp.BuildAttempts++;
                if (cp.BuildAttempts >= CheckpointBuilds.MaxAttempts)
                {
                    Finish(cp, ok: false, error: CheckpointBuilds.Lost(), tail: null, now);
                    finished = true;
                }
                else
                {
                    cp.BuildStatus = PreviewBuildStatus.Pending;
                    cp.BuildDueAtUtc = now;
                    cp.BuildStartedAtUtc = null;
                }
                log.LogWarning("The build host no longer knows the build of checkpoint {Id}; queued again (try {Attempt}).", cp.Id, cp.BuildAttempts);
            }
            catch (Exception e) when (IsTransient(e) && !ct.IsCancellationRequested)
            {
                if (CheckpointBuilds.TimedOut(cp.BuildStartedAtUtc, config.BuildTimeout, now))
                {
                    // Unreachable for longer than the build could have taken.
                    Finish(cp, ok: false, error: CheckpointBuilds.Unreachable(e.Message), tail: null, now);
                    finished = true;
                    log.LogError(e, "The build host stayed unreachable past the build timeout; giving up on checkpoint {Id}.", cp.Id);
                }
                else
                {
                    log.LogWarning(e, "Asking the build host about checkpoint {Id} failed; asking again next sweep.", cp.Id);
                }
            }
            await db.SaveChangesAsync(ct);
            if (finished) await NudgeAsync(cp, ct);
        }
    }

    /// <summary>
    /// Read the log, keep it — its tail on the row, the whole of it in
    /// storage where there is any — mark the row, forget the build on the
    /// host, and tell the entrant when the failure is theirs to fix.
    /// </summary>
    private async Task FinishWithLogAsync(
        AppDbContext db, Checkpoint cp, PreviewHostConfig config, bool ok, string? error, DateTimeOffset now, bool tellEntrant, CancellationToken ct)
    {
        string logText;
        try
        {
            logText = await host.LogAsync(config, cp.Id, ct);
        }
        catch (Exception e) when (IsTransient(e) && !ct.IsCancellationRequested)
        {
            log.LogWarning(e, "The build log of checkpoint {Id} could not be read; the verdict is kept without it.", cp.Id);
            logText = "";
        }

        Finish(cp, ok, error, CheckpointBuilds.Tail(logText), now);

        if (logText.Length > 0)
        {
            try
            {
                if (await storage.UploadSetupAsync(ct) is { } setup)
                {
                    var bytes = Encoding.UTF8.GetBytes(logText);
                    var key = CheckpointBuilds.LogKey(cp.Id);
                    using (var stream = new MemoryStream(bytes))
                        // A bare media type: the store's client refuses parameters on it.
                        await storage.PutAsync(setup, key, stream, bytes.LongLength, "text/plain", ct);
                    cp.BuildLogKey = key;
                    cp.BuildLogSetup = setup;
                }
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // The tail on the row is what the dialog shows; the download
                // is the extra that did not make it this time.
                log.LogWarning(e, "The build log of checkpoint {Id} could not be stored; its tail is kept on the row.", cp.Id);
            }
        }

        try
        {
            await host.DeleteAsync(config, cp.Id, ct);
        }
        catch (Exception e) when (IsTransient(e) && !ct.IsCancellationRequested)
        {
            log.LogWarning(e, "The build host did not forget checkpoint {Id}; it will be pruned there.", cp.Id);
        }

        if (!ok && tellEntrant && cp.Entry?.Freelancer is { } entrant && cp.Entry.Opportunity is { } opportunity)
        {
            Notify.Queue(db, entrant, "build_failed",
                Emails.BuildFailed(opportunity.Title, opportunity.Slug, (cp.Milestone?.Order ?? 0) + 1, cp.BuildError ?? ""));
            emailSignal.Wake();
        }
    }

    private static void Finish(Checkpoint cp, bool ok, string? error, string? tail, DateTimeOffset now)
    {
        cp.BuildStatus = ok ? PreviewBuildStatus.Built : PreviewBuildStatus.Failed;
        cp.BuildFinishedAtUtc = now;
        cp.BuildDueAtUtc = null;
        cp.BuildError = ok ? null : CheckpointBuilds.Cut(error ?? "The build failed.");
        cp.BuildLogTail = tail;
    }

    /// <summary>After the save, so a watcher's refetch reads the mark it was woken for.</summary>
    private Task NudgeAsync(Checkpoint cp, CancellationToken ct) =>
        cp.Entry?.Opportunity?.Slug is { } slug ? live.OpportunityChangedAsync(slug, ct) : Task.CompletedTask;

    // ---------------------------------------------------------- previews

    private static IQueryable<Domain.Preview> WithOpportunity(AppDbContext db) =>
        db.Previews.Include(p => p.Entry!).ThenInclude(e => e.Opportunity);

    /// <summary>A stop someone pressed. One never handed over just stops; one on the host is taken down there first.</summary>
    private async Task StopRequestedAsync(AppDbContext db, PreviewHostConfig config, DateTimeOffset now, CancellationToken ct)
    {
        var stopping = await WithOpportunity(db)
            .Where(p => p.StopRequested && (p.Status == PreviewStatus.Pending || p.Status == PreviewStatus.Starting || p.Status == PreviewStatus.Running))
            .Take(FollowBatch)
            .ToListAsync(ct);
        foreach (var p in stopping)
        {
            if (p.Status != PreviewStatus.Pending)
            {
                try
                {
                    await host.StopRunAsync(config, p.Id, ct);
                }
                catch (Exception e) when (IsTransient(e) && !ct.IsCancellationRequested)
                {
                    log.LogWarning(e, "Stopping preview {Id} on the build host failed; trying again next sweep.", p.Id);
                    continue; // the flag stays; the next sweep tries again
                }
            }
            Stop(p, Previews.StopReasonText("requested", config.IdleMinutes), now);
            await db.SaveChangesAsync(ct);
            await NudgeAsync(p, ct);
        }
    }

    /// <summary>Previews asked for: checked, then handed to the host with a token to clone with.</summary>
    private async Task StartRunsAsync(AppDbContext db, PreviewHostConfig config, DateTimeOffset now, CancellationToken ct)
    {
        var due = await WithOpportunity(db)
            .Where(p => p.Status == PreviewStatus.Pending && !p.StopRequested && p.DueAtUtc != null && p.DueAtUtc <= now)
            .OrderBy(p => p.DueAtUtc)
            .Take(StartBatch)
            .ToListAsync(ct);
        if (due.Count == 0) return;

        // Checked here as well as at the button: the setting may have moved
        // since, and a preview under the portal's own domain must never run.
        var refusal = config.RunUrl is not { } runUrl
            ? Previews.NoAddress
            : PreviewHost.RunDomainProblem(runUrl, PreviewHost.ParseWebUrl(await settings.GetAsync(WebOrigin.WebUrlKey, ct)));

        foreach (var p in due)
        {
            var repo = p.Entry?.RepoFullName;
            if (refusal is not null || repo is null)
            {
                Fail(p, refusal ?? "This entry has no repository to run.", tail: null, now);
                await db.SaveChangesAsync(ct);
                await NudgeAsync(p, ct);
                continue;
            }
            try
            {
                var token = await CloneTokenAsync(repo, ct);
                await host.StartRunAsync(config, p.Id, repo, p.Ref, token, ct);
                p.Status = PreviewStatus.Starting;
                p.StartedAtUtc = now;
                p.DueAtUtc = null;
                p.Error = null;
                log.LogInformation("Preview {Label} of {Repo}@{Ref} handed to the build host.", PreviewHost.Label(p.Id), repo, p.Ref);
            }
            catch (Exception e) when (IsTransient(e) && !ct.IsCancellationRequested)
            {
                p.Attempts++;
                if (p.Attempts >= CheckpointBuilds.MaxAttempts)
                {
                    Fail(p, Previews.Unreachable(e.Message), tail: null, now);
                    log.LogError(e, "Giving up handing preview {Id} to the build host after {Attempts} tries.", p.Id, p.Attempts);
                }
                else
                {
                    p.DueAtUtc = now + CheckpointBuilds.Backoff(p.Attempts);
                    log.LogWarning(e, "Handing preview {Id} to the build host failed (try {Attempt}); retrying later.", p.Id, p.Attempts);
                }
            }
            await db.SaveChangesAsync(ct);
            await NudgeAsync(p, ct);
        }
    }

    /// <summary>
    /// Starting and running previews, asked after every sweep. Up: the
    /// commit it ran, the entrant's notes, the last visit. Down — stopped by
    /// the host or failed to start — the row says why and the host forgets it.
    /// </summary>
    private async Task FollowRunsAsync(AppDbContext db, PreviewHostConfig config, DateTimeOffset now, CancellationToken ct)
    {
        var up = await WithOpportunity(db)
            .Where(p => !p.StopRequested && (p.Status == PreviewStatus.Starting || p.Status == PreviewStatus.Running))
            .OrderBy(p => p.StartedAtUtc)
            .Take(FollowBatch)
            .ToListAsync(ct);
        // Forget the ones no longer up, so the map stays the size of what runs.
        foreach (var gone in runningAsked.Keys.Where(k => !up.Any(p => p.Id == k)).ToList())
            runningAsked.Remove(gone);
        foreach (var p in up)
        {
            if (p.Status == PreviewStatus.Running)
            {
                if (runningAsked.TryGetValue(p.Id, out var asked) && now - asked < RunningAskInterval) continue;
                runningAsked[p.Id] = now;
            }
            var was = p.Status;
            var forget = false;
            try
            {
                var answer = await host.RunStatusAsync(config, p.Id, ct);
                switch (answer.Status)
                {
                    case PreviewHost.Queued or PreviewHost.Starting
                        when CheckpointBuilds.TimedOut(p.StartedAtUtc, config.BuildTimeout, now):
                        Fail(p, Previews.StartTimedOut(config.BuildTimeout), answer.LogTail, now);
                        forget = true;
                        break;
                    case PreviewHost.Queued or PreviewHost.Starting:
                        p.LogTail = answer.LogTail ?? p.LogTail;
                        break;
                    case PreviewHost.Running:
                        p.Status = PreviewStatus.Running;
                        p.Sha = answer.Sha ?? p.Sha;
                        p.Notes = Notes(answer.Notes);
                        p.LastSeenAtUtc = answer.LastSeenAt ?? p.LastSeenAtUtc;
                        p.LogTail = answer.LogTail ?? p.LogTail;
                        break;
                    case PreviewHost.Stopped:
                        p.LastSeenAtUtc = answer.LastSeenAt ?? p.LastSeenAtUtc;
                        Stop(p, Previews.StopReasonText(answer.StopReason, config.IdleMinutes), answer.StoppedAt ?? now);
                        forget = true;
                        break;
                    case PreviewHost.Failed:
                        p.Sha = answer.Sha ?? p.Sha;
                        Fail(p, answer.Error ?? "The preview did not start.", answer.LogTail, now);
                        forget = true;
                        break;
                    default:
                        Fail(p, $"The build host answered with a state the portal does not know: “{answer.Status}”.", answer.LogTail, now);
                        forget = true;
                        break;
                }
            }
            catch (PreviewHostException e) when (e.StatusCode == 404)
            {
                // The host forgot it — a restart. One still starting is handed
                // over again; one that was up has simply gone.
                if (p.Status == PreviewStatus.Running)
                {
                    Stop(p, Previews.Lost(), now);
                }
                else if (++p.Attempts >= CheckpointBuilds.MaxAttempts)
                {
                    Fail(p, Previews.Lost(), tail: null, now);
                }
                else
                {
                    p.Status = PreviewStatus.Pending;
                    p.DueAtUtc = now;
                }
                log.LogWarning("The build host no longer knows preview {Id}; it was {Status}.", p.Id, was);
            }
            catch (Exception e) when (IsTransient(e) && !ct.IsCancellationRequested)
            {
                if (p.Status == PreviewStatus.Starting && CheckpointBuilds.TimedOut(p.StartedAtUtc, config.BuildTimeout, now))
                {
                    Fail(p, Previews.Unreachable(e.Message), tail: null, now);
                    log.LogError(e, "The build host stayed unreachable past the start timeout; giving up on preview {Id}.", p.Id);
                }
                else
                {
                    log.LogWarning(e, "Asking the build host about preview {Id} failed; asking again next sweep.", p.Id);
                }
            }
            await db.SaveChangesAsync(ct);
            if (forget)
            {
                try
                {
                    await host.StopRunAsync(config, p.Id, ct);
                }
                catch (Exception e) when (IsTransient(e) && !ct.IsCancellationRequested)
                {
                    log.LogWarning(e, "The build host did not forget preview {Id}.", p.Id);
                }
            }
            if (p.Status != was) await NudgeAsync(p, ct);
        }
    }

    private static string? Notes(string? notes) =>
        string.IsNullOrWhiteSpace(notes) ? null : notes.Length <= Previews.MaxNotes ? notes : notes[..Previews.MaxNotes];

    private static void Stop(Domain.Preview p, string reason, DateTimeOffset at)
    {
        p.Status = PreviewStatus.Stopped;
        p.StopRequested = false;
        p.StoppedAtUtc = at;
        p.DueAtUtc = null;
        p.StopReason = reason.Length <= Domain.Preview.MaxStopReason ? reason : reason[..Domain.Preview.MaxStopReason];
    }

    private static void Fail(Domain.Preview p, string error, string? tail, DateTimeOffset now)
    {
        p.Status = PreviewStatus.Failed;
        p.StopRequested = false;
        p.StoppedAtUtc = now;
        p.DueAtUtc = null;
        p.Error = CheckpointBuilds.Cut(error);
        p.LogTail = tail is null ? p.LogTail : CheckpointBuilds.Tail(tail);
    }

    private Task NudgeAsync(Domain.Preview p, CancellationToken ct) =>
        p.Entry?.Opportunity?.Slug is { } slug ? live.OpportunityChangedAsync(slug, ct) : Task.CompletedTask;

    /// <summary>The host, the network, GitHub's token minting: what a later try may get past.</summary>
    private static bool IsTransient(Exception e) =>
        e is PreviewHostException or HttpRequestException or TaskCanceledException or GitHubApiException or IOException;
}
