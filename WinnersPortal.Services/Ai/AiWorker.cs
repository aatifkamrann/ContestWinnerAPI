using WinnersPortal.Services.Live;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WinnersPortal.Services.Opportunities;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Services.Common;
using WinnersPortal.Domain;
using WinnersPortal.Services.Email;
using WinnersPortal.Services.GitHub;
using WinnersPortal.Services.Profiles;
using WinnersPortal.Services.Settings;

namespace WinnersPortal.Services.Ai;

/// <summary>Lets a request that queued an artifact nudge the worker instead of waiting a sweep.</summary>
public sealed class AiWorkSignal(WorkRelay? relay = null) : WorkSignal("ai", relay);

/// <summary>
/// Runs every AI job — the counterpart of GitHubWorker and EmailWorker, for
/// the blueprint's rule: nothing in the request path. Requests only queue
/// artifact rows; this worker builds each job's input, hashes it (an
/// unchanged re-run costs nothing), checks the gate and the daily ceiling
/// (reached means skipped, never queued), calls the provider, validates the
/// answer, and stores the draft. The spam scan is the exception that proves
/// the shape: same pipeline, no provider, no quota.
/// </summary>
public sealed class AiWorker(
    IServiceScopeFactory scopes,
    SettingsService settings,
    AiOptions ai,
    AiQuota quota,
    AiProviderClient providerClient,
    GitHubService github,
    AiWorkSignal signal,
    ILiveBoard live,
    ILogger<AiWorker> log,
    AppPause pause) : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(15);

    /// <summary>Jobs per cycle — a burst of digests drains steadily, not all at once.</summary>
    private const int BatchSize = 4;

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
                log.LogError(e, "AI worker cycle failed; retrying next sweep.");
            }
            await signal.WaitAsync(SweepInterval, ct);
        }
    }

    private async Task RunCycleAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow;

        await ScheduleNarrativesAsync(db, now, ct);
        await ProcessPendingAsync(db, now, ct);
    }

    // -------------------------------------------------- daily narratives

    /// <summary>
    /// The one scheduled feature: once a day, after the digest hour, each
    /// open opportunity with entrants gets its board note queued. Deliberately
    /// batched — this is what keeps narrative usage at one call per opportunity
    /// per day instead of one per push.
    /// </summary>
    private async Task ScheduleNarrativesAsync(AppDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        if (!await ai.IsFeatureEnabledAsync(AiFeature.ProgressNarrative, ct)) return;
        var lastDate = await settings.GetAsync("system.aiNarrativeLastDate", ct);
        _ = int.TryParse(await settings.GetAsync("notifications.digestHourUtc", ct), out var hour);
        if (!DigestSchedule.IsDue(lastDate, hour, now)) return;

        var opportunityIds = await db.Opportunities
            .Where(c => c.Status == OpportunityStatus.Open
                && c.Entries.Any(e => e.Status == EntryStatus.Active))
            .Select(c => c.Id)
            .ToListAsync(ct);
        foreach (var opportunityId in opportunityIds)
            await QueueAsync(db, AiFeature.ProgressNarrative, opportunityId, now, ct);
        await db.SaveChangesAsync(ct);

        await settings.SetManyAsync(new Dictionary<string, string?>
        {
            ["system.aiNarrativeLastDate"] = DigestSchedule.DateKey(now),
        }, changedBy: "ai-worker", allowSystem: true, ct: ct);
        if (opportunityIds.Count > 0)
            log.LogInformation("Queued {Count} daily narrative(s).", opportunityIds.Count);
    }

    /// <summary>Re-queues an existing artifact or creates one — same upsert the endpoints use.</summary>
    internal static async Task QueueAsync(
        AppDbContext db, AiFeature feature, Guid subjectId, DateTimeOffset now, CancellationToken ct)
    {
        var artifact = await db.AiArtifacts
            .SingleOrDefaultAsync(a => a.Feature == feature && a.SubjectId == subjectId, ct);
        if (artifact is null)
        {
            db.AiArtifacts.Add(new AiArtifact
            {
                Id = Guid.NewGuid(),
                Feature = feature,
                SubjectId = subjectId,
                Status = AiArtifactStatus.Pending,
                CreatedAtUtc = now,
            });
        }
        else if (artifact.Status != AiArtifactStatus.Pending)
        {
            artifact.Status = AiArtifactStatus.Pending;
            artifact.Attempts = 0;
            artifact.Note = null;
        }
    }

    // ------------------------------------------------------ the pipeline

    private async Task ProcessPendingAsync(AppDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var pending = await db.AiArtifacts
            .Where(a => a.Status == AiArtifactStatus.Pending)
            .OrderBy(a => a.CreatedAtUtc)
            .Take(BatchSize)
            .ToListAsync(ct);
        if (pending.Count == 0) return;

        // The master switch stops queued jobs too, not only new requests.
        if (!await ai.IsEnabledAsync(ct))
        {
            foreach (var artifact in pending)
                Skip(artifact, "AI automation is switched off on this portal.");
            await db.SaveChangesAsync(ct);
            return;
        }

        foreach (var artifact in pending)
        {
            try
            {
                await ProcessOneAsync(db, artifact, now, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                artifact.Attempts++;
                artifact.Note = AiRules.FailureNote(e);
                if (artifact.Attempts >= AiRules.MaxAttempts)
                {
                    artifact.Status = AiArtifactStatus.Failed;
                    log.LogWarning(e, "AI job {Feature}/{Subject} failed for good.",
                        artifact.Feature, artifact.SubjectId);
                }
            }
            await db.SaveChangesAsync(ct);

            // The narrative is the one artifact that renders on a public page
            // nobody re-requested — tell its watchers, after the save.
            if (artifact is { Feature: AiFeature.ProgressNarrative, Status: AiArtifactStatus.Done })
            {
                var slug = await db.Opportunities.Where(c => c.Id == artifact.SubjectId)
                    .Select(c => c.Slug).SingleOrDefaultAsync(ct);
                if (slug is not null) await live.OpportunityChangedAsync(slug, ct);
            }
        }
    }

    private async Task ProcessOneAsync(AppDbContext db, AiArtifact artifact, DateTimeOffset now, CancellationToken ct)
    {
        if (!await ai.IsFeatureEnabledAsync(artifact.Feature, ct))
        {
            Skip(artifact, "This AI feature is switched off (or the provider key is missing).");
            return;
        }

        // ---- the local feature: same pipeline, no provider, no quota ----
        if (artifact.Feature == AiFeature.SpamFilter)
        {
            var (input, output) = await RunSpamScanAsync(db, artifact.SubjectId, ct);
            var localHash = AiRules.InputHash(input);
            if (localHash == artifact.InputHash && artifact.OutputJson is not null)
            {
                Restore(artifact);
                return;
            }
            Complete(artifact, localHash, output, "local", null, now);
            return;
        }

        // ---- provider features: input → hash → ceiling → call → validate ----
        var (prompt, canonicalInput) = artifact.Feature switch
        {
            AiFeature.EntryDigest => await BuildDigestJobAsync(db, artifact.SubjectId, ct),
            AiFeature.ProgressNarrative => await BuildNarrativeJobAsync(db, artifact.SubjectId, now, ct),
            AiFeature.StandingNotes => await BuildStandingJobAsync(db, artifact.SubjectId, now, ct),
            AiFeature.RecommendedMatching => await BuildWorkKindsJobAsync(db, artifact.SubjectId, ct),
            AiFeature.ProfileReview => await BuildProfileReviewJobAsync(db, artifact.SubjectId, ct),
            AiFeature.ApplicationEvaluation => await BuildApplicationEvaluationJobAsync(db, artifact.SubjectId, ct),
            _ => throw new InvalidOperationException($"No job builder for {artifact.Feature}."),
        };

        var hash = AiRules.InputHash(canonicalInput);
        if (hash == artifact.InputHash && artifact.OutputJson is not null)
        {
            Restore(artifact); // unchanged input — the cache answers, no call
            return;
        }

        if (!await quota.TryConsumeAsync(ct))
        {
            // Skipped rather than queued: the panel simply does not appear
            // today, and a fresh request tomorrow starts clean.
            Skip(artifact, "The daily AI call ceiling is reached — try again tomorrow.");
            return;
        }

        var config = await ai.ProviderConfigAsync(ct)
            ?? throw new InvalidOperationException("The AI provider key disappeared mid-run.");
        var model = config.Model ?? AiProviderRequests.DefaultModel(config.Provider);
        var answer = await providerClient.CompleteAsync(
            config.Provider, model, config.ApiKey, prompt.System, prompt.User, ct);

        var canonical = AiOutputs.Validate(artifact.Feature, answer, out var error)
            ?? throw new InvalidOperationException(error ?? "The model's answer failed validation.");
        Complete(artifact, hash, canonical, config.Provider, model, now);
        log.LogInformation("AI {Feature}/{Subject} drafted by {Provider}:{Model}.",
            artifact.Feature, artifact.SubjectId, config.Provider, model);
    }

    private static void Skip(AiArtifact artifact, string note)
    {
        artifact.Status = AiArtifactStatus.Skipped;
        artifact.Note = note;
    }

    private static void Restore(AiArtifact artifact)
    {
        artifact.Status = AiArtifactStatus.Done;
        artifact.Note = null;
        artifact.Attempts = 0;
    }

    private static void Complete(
        AiArtifact artifact, string hash, string outputJson, string provider, string? model, DateTimeOffset now)
    {
        artifact.Status = AiArtifactStatus.Done;
        artifact.InputHash = hash;
        artifact.OutputJson = outputJson;
        artifact.Provider = provider;
        artifact.Model = model;
        artifact.Note = null;
        artifact.Attempts = 0;
        artifact.CompletedAtUtc = now;
    }

    // ------------------------------------------------------ job builders

    private static async Task<((string System, string User) Prompt, string Input)> BuildWorkKindsJobAsync(
        AppDbContext db, Guid userId, CancellationToken ct)
    {
        var row = await db.Profiles.AsNoTracking()
            .Where(p => p.UserId == userId)
            .Select(p => new
            {
                p.Headline,
                Skills = p.Skills.OrderBy(s => s.Order)
                    .Select(s => new { s.Name, s.Level, s.Years }).ToList(),
            })
            .SingleOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("That profile is gone.");

        var taxonomy = AiInputs.Taxonomy();
        var profile = AiInputs.WorkKinds(
            row.Headline, row.Skills.Select(s => (s.Name, s.Level.ToString(), s.Years)));
        return (AiPrompts.WorkKinds(taxonomy, profile), taxonomy + profile);
    }

    /// <summary>
    /// The profile and the open opportunities' doors, read the same way the
    /// preview reads them to decide whether to queue this — the two must
    /// hash alike, or every open of the tab would be a fresh call.
    /// </summary>
    private async Task<((string System, string User) Prompt, string Input)> BuildProfileReviewJobAsync(
        AppDbContext db, Guid userId, CancellationToken ct)
    {
        var facts = await ProfileReview.ReadAsync(db, userId, await github.CanConnectAsync(ct), ct)
            ?? throw new InvalidOperationException("That profile is gone.");
        var json = AiInputs.ProfileReview(facts);
        return (AiPrompts.ProfileReview(json), json);
    }

    /// <summary>
    /// The application as it was filed, and the evaluation the portal made
    /// of it then — the lines the model is asked to word. Read off the row,
    /// never recomputed: the client decides on what was submitted.
    /// </summary>
    private static async Task<((string System, string User) Prompt, string Input)> BuildApplicationEvaluationJobAsync(
        AppDbContext db, Guid applicationId, CancellationToken ct)
    {
        var a = await db.Applications.AsNoTracking()
            .Where(x => x.Id == applicationId)
            .Select(x => new
            {
                x.Summary, x.Approach, x.PortfolioJson, x.EvaluationJson, x.Commitment, x.HoursPerWeek, x.Advantages,
                OpportunityTitle = x.Opportunity!.Title,
                Brief = x.Opportunity.BriefMarkdown,
                x.Opportunity.Category,
                Skills = x.Opportunity.Skills.OrderBy(s => s.Order).Select(s => s.Name).ToList(),
            })
            .SingleOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("That application is gone.");
        Evaluation evaluation;
        try
        {
            evaluation = JsonSerializer.Deserialize<Evaluation>(a.EvaluationJson) ?? new(0, "Unread", [], []);
        }
        catch (JsonException)
        {
            evaluation = new(0, "Unread", [], []);
        }
        var json = AiInputs.ApplicationEvaluation(
            a.OpportunityTitle, a.Brief, OpportunityCategories.Find(a.Category)?.Label, a.Skills, evaluation,
            a.Summary, a.Approach, ApplicationRules.Portfolio(a.PortfolioJson),
            ApplicationRules.CommitmentName(a.Commitment), a.HoursPerWeek,
            a.Advantages
                .Select(k => ApplicationRules.Advantages.FirstOrDefault(v => v.Key == k).Label)
                .Where(l => l is not null)!);
        return (AiPrompts.ApplicationEvaluation(json), json);
    }

    private async Task<((string System, string User) Prompt, string Input)> BuildDigestJobAsync(
        AppDbContext db, Guid entryId, CancellationToken ct)
    {
        var e = await db.Entries.AsNoTracking()
            .Where(x => x.Id == entryId)
            .Select(x => new
            {
                x.RepoFullName, x.DefaultBranch, x.PushCount, x.LastPushAtUtc,
                x.Note, x.FrozenAtUtc, x.FreelancerId,
                OpportunityTitle = x.Opportunity!.Title,
                Brief = x.Opportunity.BriefMarkdown,
                MilestonesAll = x.Opportunity.Milestones.OrderBy(m => m.Order).Select(m => m.Title).ToList(),
                MilestonesClaimed = x.Checkpoints.OrderBy(cp => cp.Milestone!.Order)
                    .Select(cp => cp.Milestone!.Title).ToList(),
            })
            .SingleOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("The entry no longer exists.");

        // What the portal already knows, plus what GitHub will tell the
        // server. Whether any of the repo's own content travels onward is
        // sendCode's call, enforced inside AiInputs.Digest.
        List<string> languages = [];
        List<string>? treePaths = null;
        string? readme = null;
        int? fileCount = null;
        bool? hasTests = null, hasReadme = null, hasCi = null;
        var sendCode = await ai.MaySendCodeAsync(ct);

        if (e.RepoFullName is not null && await github.IsConfiguredAsync(ct))
        {
            languages = await github.GetLanguagesAsync(e.RepoFullName, ct);
            var gitRef = e.FrozenAtUtc is not null
                ? "tags/final"
                : $"heads/{e.DefaultBranch ?? "main"}";
            var sha = await github.GetRefShaAsync(e.RepoFullName, gitRef, ct);
            if (sha is not null)
            {
                treePaths = await github.GetTreePathsAsync(e.RepoFullName, sha, 2000, ct);
                (fileCount, hasTests, hasReadme, hasCi, _) = AiInputs.TreeFacts(treePaths);
                if (sendCode)
                    readme = await github.GetReadmeTextAsync(
                        e.RepoFullName, sha, AiInputs.MaxReadmeChars, ct);
            }
        }

        // The entrant's own portfolio, so the reading aid can point at where
        // a claim could be checked against the code. Self-reported, and
        // carried under a name that says so — it is not evidence about this
        // repository and the prompt says the same.
        var written = await db.Profiles.AsNoTracking()
            .Where(p => p.UserId == e.FreelancerId)
            .Select(p => new AiInputs.EntrantPortfolio(
                p.Headline,
                p.Bio,
                p.Skills.OrderBy(s => s.Order).Select(s => s.Name).ToList(),
                p.Projects.OrderBy(x => x.Order).Select(x => x.Title).ToList(),
                p.YearsExperience,
                p.HoursPerWeek))
            .SingleOrDefaultAsync(ct);

        var input = AiInputs.Digest(
            new AiInputs.DigestFacts(
                e.OpportunityTitle, e.Brief, e.Note ?? "", e.PushCount, e.LastPushAtUtc,
                e.MilestonesAll, e.MilestonesClaimed, languages,
                fileCount, hasTests, hasReadme, hasCi, written),
            treePaths, readme, sendCode);
        return (AiPrompts.Digest(input), input);
    }

    private static async Task<((string System, string User) Prompt, string Input)> BuildNarrativeJobAsync(
        AppDbContext db, Guid opportunityId, DateTimeOffset now, CancellationToken ct)
    {
        var c = await db.Opportunities.AsNoTracking()
            .Where(x => x.Id == opportunityId)
            .Select(x => new
            {
                x.Title,
                Milestones = x.Milestones.OrderBy(m => m.Order)
                    .Select(m => new { m.Order, m.Title, m.DueUtc }).ToList(),
            })
            .SingleOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("The opportunity no longer exists.");

        var since = now.AddHours(-24);
        var entrantRows = await db.Entries.AsNoTracking()
            .Where(x => x.OpportunityId == opportunityId && x.Status == EntryStatus.Active)
            .OrderBy(x => x.CreatedAtUtc)
            .Select(x => new
            {
                Name = x.Freelancer!.DisplayName,
                x.PushCount,
                x.LastPushAtUtc,
                Claimed = x.Checkpoints.OrderBy(cp => cp.Milestone!.Order)
                    .Select(cp => new { cp.Milestone!.Order, cp.Milestone.Title, cp.ClaimedAtUtc }).ToList(),
            })
            .ToListAsync(ct);

        var entrants = entrantRows
            .Select(x =>
            {
                // The same reading the board shows, counted here so the note
                // summarises arithmetic instead of attempting any.
                var standing = Schedule.Stand(c.Milestones.Select(m => Schedule.StateOf(
                    m.DueUtc,
                    x.Claimed.FirstOrDefault(cp => cp.Order == m.Order)?.ClaimedAtUtc,
                    now)));
                return new AiInputs.NarrativeEntrant(
                    x.Name,
                    x.Claimed.Select(cp => cp.Title).ToList(),
                    x.Claimed.Where(cp => cp.ClaimedAtUtc >= since).Select(cp => cp.Title).ToList(),
                    x.PushCount,
                    x.LastPushAtUtc,
                    standing.OnTime,
                    standing.Late,
                    standing.Overdue);
            })
            .ToList();

        var input = AiInputs.Narrative(
            c.Title,
            c.Milestones.Select(m => new AiInputs.NarrativeMilestone(m.Title, m.DueUtc)).ToList(),
            entrants,
            now);
        return (AiPrompts.Narrative(input), input);
    }

    /// <summary>
    /// The standing notes: the same rows the board is ordered by, with their
    /// computed scores and explained parts, for the model to put into words.
    /// The reader does every sum; the prompt forbids redoing any.
    /// </summary>
    private static async Task<((string System, string User) Prompt, string Input)> BuildStandingJobAsync(
        AppDbContext db, Guid opportunityId, DateTimeOffset now, CancellationToken ct)
    {
        var c = await db.Opportunities.AsNoTracking()
            .Where(x => x.Id == opportunityId)
            .Select(x => new
            {
                x.Title, x.Status, x.Delivery, x.DeadlineUtc,
                Milestones = x.Milestones.OrderBy(m => m.Order)
                    .Select(m => new AiInputs.NarrativeMilestone(m.Title, m.DueUtc)).ToList(),
            })
            .SingleOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("The opportunity no longer exists.");

        var rows = await StandingReader.ForOpportunitiesAsync(db, [opportunityId], now, ct);
        if (rows.Count == 0)
            throw new InvalidOperationException("This opportunity has no active entrants to read.");

        var entrants = rows.Values
            .OrderBy(r => r.Rank)
            .Select(r => new AiInputs.StandingEntrant(
                r.EntryId, r.DisplayName, r.Rank, r.Of, r.Score, Standing.BandName(r.Band),
                r.EnteredAtUtc, r.LastActivityUtc,
                r.States.Select(Schedule.Name).ToList(),
                r.Parts.Select(p => new AiInputs.StandingPart(p.Label, p.Earned, p.Available, p.Detail)).ToList(),
                r.Note))
            .ToList();

        var input = AiInputs.Standing(
            c.Title, OpportunityNames.StatusName(c.Status), Delivery.Name(c.Delivery), c.DeadlineUtc,
            c.Milestones, entrants, now);
        return (AiPrompts.Standing(input), input);
    }

    private async Task<(string Input, string Output)> RunSpamScanAsync(
        AppDbContext db, Guid opportunityId, CancellationToken ct)
    {
        var rows = await db.Entries.AsNoTracking()
            .Where(x => x.OpportunityId == opportunityId && x.Status == EntryStatus.Active)
            .OrderBy(x => x.Id)
            .Select(x => new
            {
                x.Id, x.PushCount, x.RepoFullName, x.DefaultBranch, x.FrozenAtUtc,
                Entrant = x.Freelancer!.DisplayName,
            })
            .ToListAsync(ct);
        if (rows.Count == 0)
            throw new InvalidOperationException("This opportunity has no active entries to scan.");

        var githubConfigured = await github.IsConfiguredAsync(ct);
        var entries = new List<SpamRules.Entry>(rows.Count);
        foreach (var row in rows)
        {
            List<string>? paths = null;
            if (githubConfigured && row.RepoFullName is not null && row.PushCount > 0)
            {
                var gitRef = row.FrozenAtUtc is not null
                    ? "tags/final"
                    : $"heads/{row.DefaultBranch ?? "main"}";
                var sha = await github.GetRefShaAsync(row.RepoFullName, gitRef, ct);
                if (sha is not null)
                    paths = await github.GetTreePathsAsync(row.RepoFullName, sha, 2000, ct);
            }
            entries.Add(new SpamRules.Entry(row.Id, row.Entrant, row.PushCount, paths));
        }

        var input = JsonSerializer.Serialize(entries.Select(x => new
        {
            entryId = x.EntryId,
            pushCount = x.PushCount,
            treePaths = x.TreePaths,
        }));
        var flags = SpamRules.Scan(entries);
        var output = JsonSerializer.Serialize(new
        {
            scanned = entries.Count,
            flags = flags.Select(f => new
            {
                entryId = f.EntryId,
                entrant = f.Entrant,
                reasons = f.Reasons,
            }),
        });
        return (input, output);
    }
}
