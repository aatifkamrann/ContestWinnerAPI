using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WinnersPortal.Domain;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Services.Activity;
using WinnersPortal.Services.Ai;
using WinnersPortal.Services.Auth;
using WinnersPortal.Services.Common;
using WinnersPortal.Services.Email;
using WinnersPortal.Services.GitHub;
using WinnersPortal.Services.Live;
using WinnersPortal.Services.Profiles;
using WinnersPortal.Services.Identity;
using WinnersPortal.Services.Settings;

namespace WinnersPortal.Services.Opportunities;

/// <summary>
/// Applying to compete, and deciding who does. The freelancer's side is
/// one read that carries everything the eight-step wizard shows — the
/// opportunity in figures, their fit, the door's checks, their own summary and
/// past work with a relevance figure each — and one write that files the
/// application. The client's (or an administrator's) side is one decision
/// per application; selecting makes the entry, by the rules the old door
/// enforced, so a selection can still be refused.
/// </summary>
public sealed class ApplicationService(AppDbContext db, SettingsService settings, AiOptions ai, AiWorkSignal aiSignal, EmailWorkSignal emailSignal, ILiveBoard live, IHttpClientFactory httpFactory, GitHubWorkSignal githubSignal, ActivityNote activity, AiQuota quota, AiProviderClient providerClient, ILogger<AiService.AiFormDraftLog> log, IdentityOptions identity)
{
    public async Task<Outcome<ApplyFormResponse>> ApplyFormAsync(string slug, ClaimsPrincipal principal, CancellationToken ct)
    {
        var me = Principal.UserId(principal)!.Value;
        var c = await db.Opportunities.AsNoTracking()
            .Include(x => x.Client)
            .Include(x => x.Skills.OrderBy(s => s.Order))
            .SingleOrDefaultAsync(x => x.Slug == slug, ct);
        if (c is null || c.Status == OpportunityStatus.Draft) return Outcome.NotFound();
        var requiredSkills = c.Skills.Select(s => s.Name).ToList();
        var now = DateTimeOffset.UtcNow;
        var startsAt = Schedule.StartsAt(c.StartsAtUtc, c.PublishedAtUtc);

        // Their fit, the same judgement the card and the page make.
        var viewer = await FitReader.ForAsync(db, me, Principal.Role(principal), ai, aiSignal, ct);
        var fit = viewer?.Judge(c.MinMeritScore, requiredSkills, c.Category, startsAt, c.DeadlineUtc);

        var account = await db.Users.AsNoTracking()
            .Where(u => u.Id == me)
            .Select(u => new { u.EmailConfirmedAtUtc, u.PhoneConfirmedAtUtc, u.IdentityVerifiedAtUtc })
            .SingleAsync(ct);
        var identityRequired = await identity.IsEnabledAsync(ct) && await identity.RequiredForApplyAsync(ct);
        var identityOwed = identityRequired && account.IdentityVerifiedAtUtc is null;
        // Through the profile, so a closed one reads as none.
        var profile = await db.Profiles.AsNoTracking()
            .Where(p => p.UserId == me)
            .Select(p => new
            {
                p.Bio,
                p.Availability,
                p.HoursPerWeek,
                Projects = p.Projects.OrderBy(x => x.Order)
                    .Select(x => new { x.Id, x.Title, x.Description, x.Outcome, x.Role, x.Tech, x.Category })
                    .ToList(),
            })
            .SingleOrDefaultAsync(ct);
        var projects = (profile?.Projects ?? []).Select(x => new ApplyProject
        {
            Id = x.Id,
            Title = x.Title,
            Role = x.Role,
            Tech = x.Tech,
            Outcome = x.Outcome,
            Relevance = ApplicationRules.Relevance(
                ProjectFacts.Of(x.Category, x.Title, x.Description, x.Outcome, x.Role, x.Tech),
                requiredSkills, c.Category),
        }).ToList();
        var relevantCount = projects.Count(p => p.Relevance >= ApplicationRules.RelevantFrom);

        var removedBefore = await db.Entries.AnyAsync(
            e => e.OpportunityId == c.Id && e.FreelancerId == me && e.Status == EntryStatus.Removed, ct);
        var alreadyEntered = await db.Entries.AnyAsync(
            e => e.OpportunityId == c.Id && e.FreelancerId == me && e.Status == EntryStatus.Active, ct);
        var application = await db.Applications.AsNoTracking()
            .SingleOrDefaultAsync(a => a.OpportunityId == c.Id && a.FreelancerId == me, ct);
        var entryOpen = Schedule.EntryOpen(c.Status, c.EntryCloseUtc, c.DeadlineUtc, now);
        var problem = ApplicationRules.SubmitProblem(
            c.Status, entryOpen, fit, removedBefore, alreadyEntered, application is not null, identityOwed);
        _ = int.TryParse(await settings.GetAsync("limits.maxEntriesPerOpportunity", ct), out var cap);

        return Outcome.Ok(new ApplyFormResponse
        {
            Opportunity = new ApplyFormOpportunity
            {
                Slug = c.Slug,
                Title = c.Title,
                AwardAmount = c.AwardAmount,
                Currency = c.Currency,
                Status = OpportunityNames.StatusName(c.Status),
                Delivery = Delivery.Name(c.Delivery),
                NeedsGithubUsername = Delivery.NeedsGithubUsername(c.Delivery),
                Category = c.Category,
                CategoryLabel = OpportunityCategories.Find(c.Category)?.Label,
                Skills = requiredSkills,
                MinMeritScore = c.MinMeritScore,
                StartsAtUtc = startsAt,
                DeadlineUtc = c.DeadlineUtc,
                EntryCloseUtc = Schedule.EntryClosesAt(c.EntryCloseUtc, c.DeadlineUtc),
                EntryOpen = entryOpen,
                Weeks = Assessment.TimelineWeeks(startsAt ?? now, c.DeadlineUtc),
                ClientName = c.Client!.DisplayName,
                EntrantCount = await db.Entries.CountAsync(
                    e => e.OpportunityId == c.Id && e.Status == EntryStatus.Active, ct),
                MaxEntries = cap == 0 ? (int?)null : cap,
                ApplicationCount = await db.Applications.CountAsync(a => a.OpportunityId == c.Id, ct),
            },
            Fit = fit is null ? null : FitReader.Dto(fit),
            Eligibility = new ApplyEligibility
            {
                CanApply = problem is null,
                Problem = problem,
                EmailVerified = account.EmailConfirmedAtUtc != null,
                PhoneVerified = account.PhoneConfirmedAtUtc != null,
                IdentityRequired = identityRequired,
                IdentityVerified = account.IdentityVerifiedAtUtc != null,
                // Said on the profile, and not "not taking work on".
                AvailabilitySet = profile?.Availability != null || profile?.HoursPerWeek != null,
                AvailabilityOk = profile?.Availability != Availability.Unavailable,
            },
            Profile = new ApplyProfile
            {
                Summary = profile?.Bio,
                HoursPerWeek = profile?.HoursPerWeek,
                Projects = projects,
                RelevantCount = relevantCount,
            },
            Advantages = ApplicationRules.Advantages.Select(a => new AdvantageOption { Key = a.Key, Label = a.Label }),
            SuggestedAdvantages = fit is null ? [] : ApplicationRules.SuggestedAdvantages(fit, relevantCount),
            Application = application is null
                ? null
                : await ApplicantViewAsync(db, application, c.Category, c.Status, startsAt, now, ct),
        });
    }

    public async Task<Outcome<ApplicationView>> ApplyAsync(string slug, ApplyRequest request, ClaimsPrincipal principal, CancellationToken ct)
    {
        var me = Principal.UserId(principal)!.Value;
        var approach = ApplicationRules.CleanApproach(request.Approach, out var approachProblem);
        if (approach is null) return Outcome.Invalid(approachProblem!);
        var summary = ProfileRules.Clean(request.Summary, ProfileRules.MaxBio) ?? "";
        var commitment = ApplicationRules.ParseCommitment(request.Commitment);
        if (commitment is null)
            return Outcome.Invalid("Say whether your availability is full, partial or limited.");
        if (request.HoursPerWeek is not (>= ApplicationRules.MinHours and <= ApplicationRules.MaxHours))
            return Outcome.Invalid(
                $"Hours a week is a number from {ApplicationRules.MinHours} to {ApplicationRules.MaxHours}.");
        if (!(request.DedicateTime && request.OnSchedule && request.Competitive && request.OriginalWork))
            return Outcome.Invalid(
                "Tick all four confirmations — the time, the schedule, the competition and original work.");

        var c = await db.Opportunities.AsNoTracking()
            .Include(x => x.Client)
            .Include(x => x.Skills.OrderBy(s => s.Order))
            .SingleOrDefaultAsync(x => x.Slug == slug, ct);
        if (c is null || c.Status == OpportunityStatus.Draft) return Outcome.NotFound();
        var requiredSkills = c.Skills.Select(s => s.Name).ToList();
        var now = DateTimeOffset.UtcNow;
        var startsAt = Schedule.StartsAt(c.StartsAtUtc, c.PublishedAtUtc);

        // Where the repository invitation goes, if they are selected —
        // checked now, because the person will not be here when it is.
        var githubUsername = request.GithubUsername?.Trim() ?? "";
        if (Delivery.NeedsGithubUsername(c.Delivery))
        {
            if (!EntryService.GithubUsername().IsMatch(githubUsername))
                return Outcome.Invalid(
                    "That does not look like a GitHub username (letters, digits, single hyphens; max 39).");
        }
        else
        {
            githubUsername = "";
        }

        var viewer = await FitReader.ForAsync(db, me, Principal.Role(principal), ai, aiSignal, ct);
        var fit = viewer?.Judge(c.MinMeritScore, requiredSkills, c.Category, startsAt, c.DeadlineUtc);
        var removedBefore = await db.Entries.AnyAsync(
            e => e.OpportunityId == c.Id && e.FreelancerId == me && e.Status == EntryStatus.Removed, ct);
        var alreadyEntered = await db.Entries.AnyAsync(
            e => e.OpportunityId == c.Id && e.FreelancerId == me && e.Status == EntryStatus.Active, ct);
        var alreadyApplied = await db.Applications.AnyAsync(
            a => a.OpportunityId == c.Id && a.FreelancerId == me, ct);
        var identityOwed = await identity.IsEnabledAsync(ct) && await identity.RequiredForApplyAsync(ct)
            && await db.Users.Where(u => u.Id == me).Select(u => u.IdentityVerifiedAtUtc).SingleAsync(ct) is null;
        if (ApplicationRules.SubmitProblem(
                c.Status, Schedule.EntryOpen(c.Status, c.EntryCloseUtc, c.DeadlineUtc, now),
                fit, removedBefore, alreadyEntered, alreadyApplied, identityOwed) is { } problem)
            return identityOwed
                ? Outcome.Forbidden(new VerificationRequiredResponse { Error = problem, VerificationRequired = true })
                : Outcome.Conflict(problem);

        // The door the captcha guards moved here with the door.
        if (await Captcha.RequiredAsync(settings, ct)
            && !await Captcha.VerifyAsync(httpFactory, settings, request.CaptchaToken, ct))
            return Outcome.Invalid(
                "The captcha could not be verified — complete the challenge and try again.");

        // The past work they chose, copied as it stands. Their own
        // projects only: an id from anybody else's profile is dropped.
        var chosen = (request.ProjectIds ?? []).Distinct().Take(ApplicationRules.MaxPortfolio).ToList();
        var portfolio = chosen.Count == 0
            ? []
            : (await db.Profiles.AsNoTracking()
                .Where(p => p.UserId == me)
                .SelectMany(p => p.Projects.Where(x => chosen.Contains(x.Id)))
                .OrderBy(x => x.Order)
                .Select(x => new { x.Title, x.Description, x.Outcome, x.Role, x.Tech, x.Category })
                .ToListAsync(ct))
                .Select(x => new PortfolioItem(x.Title, x.Role, x.Tech, x.Outcome,
                    ApplicationRules.Relevance(
                        ProjectFacts.Of(x.Category, x.Title, x.Description, x.Outcome, x.Role, x.Tech),
                        requiredSkills, c.Category)))
                .ToList();

        var evaluation = fit is null
            ? new Evaluation(0, "Unread", [], [])
            : ApplicationRules.Evaluate(fit, portfolio.Count);
        var application = new Application
        {
            Id = Guid.NewGuid(),
            OpportunityId = c.Id,
            FreelancerId = me,
            Summary = summary,
            Approach = approach,
            PortfolioJson = ApplicationRules.PortfolioJson(portfolio),
            PortfolioCount = portfolio.Count,
            Commitment = commitment.Value,
            HoursPerWeek = request.HoursPerWeek.Value,
            Advantages = ApplicationRules.CleanAdvantages(request.Advantages),
            GithubUsername = githubUsername,
            MatchAtSubmit = fit?.Match ?? 0,
            MeritAtSubmit = fit?.MeritScore ?? 0,
            EvaluationJson = JsonSerializer.Serialize(evaluation),
            SubmittedAtUtc = now,
        };
        db.Applications.Add(application);

        // Both emails ride the same save as the row.
        var applicant = await db.Users.SingleAsync(u => u.Id == me, ct);
        var number = await db.Applications.CountAsync(a => a.OpportunityId == c.Id, ct) + 1;
        Notify.Queue(db, c.Client!, "application_received", Emails.ApplicationReceived(
            c.Title, c.Slug, applicant.DisplayName, application.MatchAtSubmit, number));
        Notify.Queue(db, applicant, "application_submitted", Emails.ApplicationSubmitted(
            c.Title, c.Slug, c.Client!.DisplayName));

        // The model's words for the evaluation, queued — the page shows
        // the portal's own lines until they land.
        var evaluate = await ai.IsFeatureEnabledAsync(AiFeature.ApplicationEvaluation, ct);
        if (evaluate) await AiWorker.QueueAsync(db, AiFeature.ApplicationEvaluation, application.Id, now, ct);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (DbErrors.IsUniqueViolation(e))
        {
            return Outcome.Conflict("You have already applied to this opportunity.");
        }
        if (evaluate) aiSignal.Wake();
        emailSignal.Wake();
        await live.OpportunityChangedAsync(c.Slug, ct); // the client's review box just grew
        return Outcome.Ok(await ApplicantViewAsync(db, application, c.Category, c.Status, startsAt, now, ct));
    }

    public async Task<Outcome<IEnumerable<MyApplicationRow>>> MineAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        var me = Principal.UserId(principal)!.Value;
        var viewer = await FitReader.ForAsync(db, me, Principal.Role(principal), ai, aiSignal, ct);
        var rows = await db.Applications.AsNoTracking()
            .Where(a => a.FreelancerId == me)
            .OrderBy(a => a.Status == ApplicationStatus.UnderReview ? 0 : 1)
            .ThenByDescending(a => a.SubmittedAtUtc)
            .Select(a => new
            {
                a.Id, a.Status, a.SubmittedAtUtc, a.DecidedAtUtc, a.EntryId,
                a.MatchAtSubmit, a.MeritAtSubmit, a.PortfolioCount, a.HoursPerWeek,
                OpportunitySlug = a.Opportunity!.Slug,
                OpportunityTitle = a.Opportunity.Title,
                a.Opportunity.AwardAmount,
                a.Opportunity.Currency,
                a.Opportunity.DeadlineUtc,
                a.Opportunity.PublishedAtUtc,
                a.Opportunity.StartsAtUtc,
                OpportunityStatus = a.Opportunity.Status,
                OpportunityDelivery = a.Opportunity.Delivery,
                a.Opportunity.MinMeritScore,
                OpportunityCategory = a.Opportunity.Category,
                OpportunitySkills = a.Opportunity.Skills.OrderBy(s => s.Order).Select(s => s.Name).ToList(),
                // The model's words still on their way: the row says so
                // rather than reading as if nothing were happening.
                EvaluationPending = db.AiArtifacts.Any(x =>
                    x.Feature == AiFeature.ApplicationEvaluation
                    && x.SubjectId == a.Id
                    && x.Status == AiArtifactStatus.Pending),
            })
            .ToListAsync(ct);

        return Outcome.Ok(rows.Select(a => new MyApplicationRow
        {
            Id = a.Id,
            Status = ApplicationRules.StatusName(a.Status),
            SubmittedAtUtc = a.SubmittedAtUtc,
            DecidedAtUtc = a.DecidedAtUtc,
            EntryId = a.EntryId,
            Match = a.MatchAtSubmit,
            MeritScore = a.MeritAtSubmit,
            PortfolioCount = a.PortfolioCount,
            HoursPerWeek = a.HoursPerWeek,
            EvaluationPending = a.EvaluationPending,
            Fit = viewer is null
                ? null
                : FitReader.Dto(viewer.Judge(
                    a.MinMeritScore, a.OpportunitySkills, a.OpportunityCategory,
                    Schedule.StartsAt(a.StartsAtUtc, a.PublishedAtUtc), a.DeadlineUtc)),
            Opportunity = new OpportunitySummary
            {
                Slug = a.OpportunitySlug,
                Title = a.OpportunityTitle,
                AwardAmount = a.AwardAmount,
                Currency = a.Currency,
                DeadlineUtc = a.DeadlineUtc,
                StartsAtUtc = a.StartsAtUtc,
                PublishedAtUtc = a.PublishedAtUtc,
                Status = OpportunityNames.StatusName(a.OpportunityStatus),
                Delivery = Delivery.Name(a.OpportunityDelivery),
            },
        }));
    }

    public async Task<Outcome<ApplicationView>> ReadAsync(Guid id, ClaimsPrincipal principal, CancellationToken ct)
    {
        var me = Principal.UserId(principal);
        var application = await db.Applications.AsNoTracking()
            .Include(a => a.Opportunity)
            .SingleOrDefaultAsync(a => a.Id == id, ct);
        if (application is null) return Outcome.NotFound();
        var c = application.Opportunity!;
        if (application.FreelancerId != me && c.ClientId != me && !principal.IsInRole(Roles.Admin))
            return Outcome.NotFound();
        var now = DateTimeOffset.UtcNow;
        return Outcome.Ok(await ApplicantViewAsync(
            db, application, c.Category, c.Status, Schedule.StartsAt(c.StartsAtUtc, c.PublishedAtUtc), now, ct));
    }

    public async Task<Outcome<DecisionResponse>> DecideAsync(Guid id, DecideRequest request, ClaimsPrincipal principal, CancellationToken ct)
    {
        var meId = Principal.UserId(principal)!.Value;
        var isAdmin = principal.IsInRole(Roles.Admin);
        var selected = request.Decision?.Trim().ToLowerInvariant() switch
        {
            "selected" => true,
            "not_selected" => false,
            _ => (bool?)null,
        };
        if (selected is null)
            return Outcome.Invalid("The decision is “selected” or “not_selected”.");
        // The activity row says which way, whatever happens next: a
        // refused selection is still a selection somebody tried.
        activity.Action = ActivityNames.Decision(selected.Value, takenBack: false);

        var application = await db.Applications
            .Include(a => a.Opportunity).ThenInclude(c => c!.Client)
            .Include(a => a.Opportunity).ThenInclude(c => c!.Milestones.OrderBy(m => m.Order))
            .Include(a => a.Opportunity).ThenInclude(c => c!.Skills.OrderBy(s => s.Order))
            .Include(a => a.Freelancer)
            .SingleOrDefaultAsync(a => a.Id == id, ct);
        // Not found rather than forbidden: whether an application id
        // exists is not something a stranger gets to learn.
        if (application is null) return Outcome.NotFound();
        var opportunity = application.Opportunity!;
        if (opportunity.ClientId != meId && !isAdmin) return Outcome.NotFound();
        // Taking a selection back undoes the entry it made, which is only
        // honest before any work has arrived in it: a push, a claimed
        // milestone, or a file handed in.
        var next = selected.Value ? ApplicationStatus.Selected : ApplicationStatus.NotSelected;
        var held = application.Status == ApplicationStatus.Selected && application.EntryId is { } heldEntryId
            ? await db.Entries
                .Where(e => e.Id == heldEntryId)
                .Select(e => new
                {
                    Entry = e,
                    Claimed = e.Checkpoints.Any(),
                    Uploaded = e.Submissions.Any(s => s.UploadedAtUtc != null),
                })
                .SingleOrDefaultAsync(ct)
            : null;
        var workArrived = held is not null && (held.Entry.PushCount > 0 || held.Claimed || held.Uploaded);
        if (ApplicationRules.DecisionProblem(application.Status, next, opportunity.Status, workArrived) is { } problem)
            return Outcome.Conflict(problem);

        var now = DateTimeOffset.UtcNow;
        var byAdmin = isAdmin && opportunity.ClientId != meId;
        var applicant = application.Freelancer!;
        var takenBack = !selected.Value && application.Status == ApplicationStatus.Selected;
        if (takenBack) activity.Action = ActivityNames.Decision(selected: false, takenBack: true);
        if (selected.Value)
        {
            // The entry, by the door's own rules: a merit floor the
            // applicant has since fallen under, a cap reached, entry
            // closed since — each refuses the selection with the reason.
            var joined = await EntryService.JoinAsync(
                db, opportunity, applicant.Id, Roles.Freelancer, application.GithubUsername, null,
                settings, ai, aiSignal, identity, ct);
            if (joined.Problem is not null) return Outcome.Conflict(joined.Problem);
            application.Status = ApplicationStatus.Selected;
            application.EntryId = joined.Entry!.Id;
            Notify.Queue(db, applicant, "application_selected", Emails.ApplicationSelected(
                opportunity.Title, opportunity.Slug, opportunity.Client!.DisplayName, opportunity.AwardAmount, opportunity.Currency,
                opportunity.Delivery, opportunity.DeadlineUtc, opportunity.EntryCloseUtc,
                opportunity.Milestones.Select(m => (m.Title, m.DueUtc)).ToList(),
                joined.EntrantNumber, application.GithubUsername));
        }
        else if (takenBack)
        {
            // The entry the selection made steps aside: out of the count
            // and the board, its repository archived by the worker, and
            // off the applicant's own list. A new selection makes a new one.
            var entry = held?.Entry;
            if (entry is { Status: EntryStatus.Active })
            {
                entry.Status = EntryStatus.Deselected;
                entry.WithdrawnAtUtc = now;
            }
            application.Status = ApplicationStatus.NotSelected;
            application.EntryId = null;
            Notify.Queue(db, applicant, "application_selection_taken_back", Emails.SelectionTakenBack(
                opportunity.Title, opportunity.Slug, hadRepository: entry?.RepoFullName is not null));
        }
        else
        {
            application.Status = ApplicationStatus.NotSelected;
            Notify.Queue(db, applicant, "application_not_selected", Emails.ApplicationNotSelected(
                opportunity.Title, opportunity.Slug));
        }
        application.DecidedAtUtc = now;
        application.DecidedByUserId = meId;
        // An administrator acting over the client: their field just
        // changed and they did not do it.
        if (byAdmin)
            Notify.Queue(db, opportunity.Client!, "application_decided_by_admin", Emails.ApplicationDecidedByAdmin(
                opportunity.Title, opportunity.Slug, applicant.DisplayName, selected.Value, takenBack));

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (DbErrors.IsUniqueViolation(e))
        {
            // The one-active-entry index: they got in by another road meanwhile.
            return Outcome.Conflict("This freelancer is already competing in the opportunity.");
        }
        if (selected.Value || takenBack)
        {
            await Recount.OpportunityAsync(db, opportunity.Id, ct); // the card's entrant counter
            githubSignal.Wake(); // the worker provisions the private repo, or archives one taken back
        }
        emailSignal.Wake();
        await live.OpportunityChangedAsync(opportunity.Slug, ct); // the entrant list or the review box changed
        return Outcome.Ok(new DecisionResponse
        {
            Status = ApplicationRules.StatusName(application.Status),
            EntryId = application.EntryId,
            DecidedAtUtc = now,
        });
    }

    public async Task<Outcome<AiDraftResponse>> DraftApproachAsync(string slug, ApproachDraftRequest request, ClaimsPrincipal principal, CancellationToken ct)
    {
        const AiFeature f = AiFeature.ProjectApproach;
        if (await AiService.GateProblemAsync(ai, f, ct) is { } gateProblem)
            return Outcome.Conflict(gateProblem);
        var me = Principal.UserId(principal)!.Value;
        var c = await db.Opportunities.AsNoTracking()
            .Include(x => x.Skills.OrderBy(s => s.Order))
            .Include(x => x.Milestones.OrderBy(m => m.Order))
            .Include(x => x.Requirements.OrderBy(r => r.Order))
            .SingleOrDefaultAsync(x => x.Slug == slug, ct);
        if (c is null || c.Status == OpportunityStatus.Draft) return Outcome.NotFound();
        var requiredSkills = c.Skills.Select(s => s.Name).ToList();
        var profile = await db.Profiles.AsNoTracking()
            .Where(p => p.UserId == me)
            .Select(p => new
            {
                p.Headline,
                Skills = p.Skills.OrderBy(s => s.Order).Select(s => new { s.Name, s.Level, s.Years }).ToList(),
                Projects = p.Projects.OrderBy(x => x.Order)
                    .Select(x => new { x.Title, x.Description, x.Outcome, x.Role, x.Tech, x.Category })
                    .ToList(),
            })
            .SingleOrDefaultAsync(ct);
        var fits = Assessment.RelevantTo(requiredSkills, c.Category);
        var json = AiInputs.ProjectApproach(
            c.Title, c.BriefMarkdown, OpportunityCategories.Find(c.Category)?.Label, requiredSkills,
            c.Milestones.Select(m => m.Title).ToList(),
            c.Requirements.Select(r => (r.Title, r.Detail)).ToList(),
            Assessment.TimelineWeeks(
                Schedule.StartsAt(c.StartsAtUtc, c.PublishedAtUtc) ?? DateTimeOffset.UtcNow, c.DeadlineUtc),
            profile?.Headline,
            (profile?.Skills ?? []).Select(s => (s.Name, s.Level.ToString(), s.Years)).ToList(),
            (profile?.Projects ?? [])
                .Where(x => fits is null || fits(ProjectFacts.Of(x.Category, x.Title, x.Description, x.Outcome, x.Role, x.Tech)))
                .Select(x => (x.Title, x.Tech, x.Outcome)).ToList(),
            request.Draft);
        return await AiService.DraftInlineAsync(f, AiPrompts.ProjectApproach(json), ai, quota, providerClient, log, ct);
    }

    /// <summary>
    /// The application as its owner reads it back — and as the client's row
    /// carries it: what was written, the figures at submission, the
    /// evaluation worded by the model where its words have landed, where it
    /// stands in the pipeline, and how it compares with the field.
    /// </summary>
    internal static async Task<ApplicationView> ApplicantViewAsync(
        AppDbContext db, Application a, string? category, OpportunityStatus opportunityStatus, DateTimeOffset? startsAt,
        DateTimeOffset now, CancellationToken ct)
    {
        var artifact = await db.AiArtifacts.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Feature == AiFeature.ApplicationEvaluation && x.SubjectId == a.Id, ct);
        var others = await db.Applications.AsNoTracking()
            .Where(x => x.OpportunityId == a.OpportunityId && x.Id != a.Id)
            .Select(x => x.MatchAtSubmit)
            .ToListAsync(ct);
        // Whether a selection won is asked only of an awarded opportunity.
        var won = opportunityStatus == OpportunityStatus.Awarded && a.EntryId is { } entryId
            && await db.Awards.AnyAsync(x => x.EntryId == entryId, ct);
        return View(a, artifact, ApplicationRules.StrongerThan(a.MatchAtSubmit, others),
            OpportunityCategories.Find(category)?.Label, opportunityStatus, won, startsAt, now);
    }

    internal static ApplicationView View(
        Application a, AiArtifact? artifact, int? strongerThan, string? categoryLabel,
        OpportunityStatus opportunityStatus, bool won, DateTimeOffset? startsAt, DateTimeOffset now)
    {
        var stored = StoredEvaluation(a.EvaluationJson);
        var evaluation = Worded(stored, artifact);
        var decided = a.DecidedAtUtc is not null;
        var evaluationState = artifact?.Status == AiArtifactStatus.Pending ? "current" : "done";
        var closedUndecided = ApplicationRules.ClosedUndecided(a.Status, opportunityStatus);
        return new ApplicationView
        {
            Id = a.Id,
            Status = ApplicationRules.StatusName(a.Status),
            SubmittedAtUtc = a.SubmittedAtUtc,
            DecidedAtUtc = a.DecidedAtUtc,
            EntryId = a.EntryId,
            Match = a.MatchAtSubmit,
            MeritScore = a.MeritAtSubmit,
            Summary = a.Summary,
            Approach = a.Approach,
            Portfolio = ApplicationRules.Portfolio(a.PortfolioJson),
            Commitment = ApplicationRules.CommitmentName(a.Commitment),
            HoursPerWeek = a.HoursPerWeek,
            Advantages = a.Advantages
                .Select(k => ApplicationRules.Advantages.FirstOrDefault(x => x.Key == k).Label)
                .Where(l => l is not null),
            GithubUsername = a.GithubUsername,
            Evaluation = evaluation,
            // "Stronger than N% of applicants" — null with nobody to compare with.
            StrongerThan = strongerThan,
            // After a no: what held up and what to strengthen, read off the
            // evaluation above. Null while under review, and once selected.
            Advice = a.Status == ApplicationStatus.NotSelected
                ? ApplicationRules.AdviceFor(stored, categoryLabel)
                : null,
            // Nobody answered before the opportunity closed: nothing more happens to it.
            ClosedUndecided = closedUndecided,
            // Once selected, how the competition went — open, reviewing, won,
            // lost or cancelled — so the page never congratulates over an
            // opportunity that ended otherwise. Null for every other status.
            Outcome = ApplicationRules.Outcome(a.Status, opportunityStatus, won),
            // Where it stands: the five stages the page draws, each done,
            // current or still to come.
            Stages = new[]
            {
                new ApplicationStage { Key = "submitted", Label = "Application Submitted", State = "done" },
                new ApplicationStage { Key = "evaluation", Label = "AI Evaluation", State = evaluationState },
                new ApplicationStage
                {
                    Key = "review", Label = "Client Review",
                    State = decided ? "done" : closedUndecided ? "todo" : evaluationState == "done" ? "current" : "todo",
                },
                new ApplicationStage
                {
                    Key = "selection", Label = "Competitor Selection",
                    State = decided ? "done" : "todo",
                },
                new ApplicationStage
                {
                    Key = "start", Label = "Competition Start",
                    State = a.Status != ApplicationStatus.Selected ? "todo"
                        : startsAt is { } s && s <= now ? "done"
                        : "current",
                },
            },
        };
    }

    /// <summary>The evaluation as it was stored at filing; an unreadable one reads as nothing.</summary>
    internal static Evaluation StoredEvaluation(string evaluationJson)
    {
        try
        {
            return JsonSerializer.Deserialize<Evaluation>(evaluationJson) ?? new(0, "Unread", [], []);
        }
        catch (JsonException)
        {
            return new(0, "Unread", [], []);
        }
    }

    /// <summary>
    /// The evaluation with the model's words joined on by id — the portal's
    /// own label stands wherever the model has not answered, or answered
    /// about a line that is not there. The figure and the lines are the
    /// portal's whatever the model said.
    /// </summary>
    private static EvaluationView Worded(Evaluation stored, AiArtifact? artifact)
    {
        var words = new Dictionary<string, string>(StringComparer.Ordinal);
        string? note = null;
        if (artifact?.Status == AiArtifactStatus.Done && artifact.OutputJson is not null)
        {
            using var doc = JsonDocument.Parse(artifact.OutputJson);
            var root = doc.RootElement;
            foreach (var list in new[] { "strengths", "risks" })
                if (root.TryGetProperty(list, out var arr) && arr.ValueKind == JsonValueKind.Array)
                    foreach (var item in arr.EnumerateArray())
                        if (item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                            && item.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                            words[id.GetString() ?? ""] = text.GetString() ?? "";
            if (root.TryGetProperty("note", out var n) && n.ValueKind == JsonValueKind.String)
                note = n.GetString();
        }
        WordedLine Line(EvaluationLine l) => new WordedLine
        {
            Id = l.Id,
            Label = l.Label,
            Text = words.TryGetValue(l.Id, out var w) && w.Length > 0 ? w : l.Label,
        };
        return new EvaluationView
        {
            Status = artifact?.Status switch
            {
                null => "none",
                AiArtifactStatus.Pending => "pending",
                AiArtifactStatus.Done => "done",
                AiArtifactStatus.Failed => "failed",
                _ => "skipped",
            },
            Percent = stored.Percent,
            Band = stored.Band,
            Strengths = stored.Strengths.Select(Line),
            Risks = stored.Risks.Select(Line),
            Note = note,
            Worded = words.Count > 0 || note is not null,
            Provider = AiBrand.Public(artifact?.Provider),
            CompletedAtUtc = artifact?.CompletedAtUtc,
        };
    }

    /// <summary>
    /// The client's review rows for one opportunity: every application, those
    /// still under review first, each with what the applicant wrote and
    /// the figures at submission. The applicant's name and picture come
    /// from the account, the headline from the profile as it is now.
    /// </summary>
    internal static async Task<IReadOnlyList<ApplicationReviewRow>> ReviewRowsAsync(
        AppDbContext db, Guid opportunityId, string? category, OpportunityStatus opportunityStatus, DateTimeOffset? startsAt,
        DateTimeOffset now, CancellationToken ct)
    {
        var rows = await db.Applications.AsNoTracking()
            .Where(a => a.OpportunityId == opportunityId)
            .OrderBy(a => a.Status == ApplicationStatus.UnderReview ? 0 : 1)
            .ThenBy(a => a.SubmittedAtUtc)
            .Select(a => new
            {
                Application = a,
                DisplayName = a.Freelancer!.DisplayName,
                AvatarAt = a.Freelancer.AvatarUpdatedAtUtc,
            })
            .ToListAsync(ct);
        if (rows.Count == 0) return [];
        var categoryLabel = OpportunityCategories.Find(category)?.Label;
        var winnerEntryId = opportunityStatus == OpportunityStatus.Awarded
            ? await db.Awards.AsNoTracking()
                .Where(x => x.OpportunityId == opportunityId)
                .Select(x => (Guid?)x.EntryId)
                .SingleOrDefaultAsync(ct)
            : null;
        var ids = rows.Select(r => r.Application.Id).ToList();
        var artifacts = await db.AiArtifacts.AsNoTracking()
            .Where(x => x.Feature == AiFeature.ApplicationEvaluation && ids.Contains(x.SubjectId))
            .ToDictionaryAsync(x => x.SubjectId, ct);
        var owners = rows.Select(r => r.Application.FreelancerId).ToList();
        var headlines = await db.Profiles.AsNoTracking()
            .Where(p => owners.Contains(p.UserId))
            .Select(p => new { p.UserId, p.Headline })
            .ToDictionaryAsync(p => p.UserId, p => p.Headline, ct);
        var matches = rows.Select(r => r.Application.MatchAtSubmit).ToList();
        // A withdrawal's reason lives on the entry it ended; the client reads
        // it under the Withdrawn row. These rows only ever reach the opportunity's
        // owner and administrators.
        var withdrawnEntryIds = rows
            .Where(r => r.Application.Status == ApplicationStatus.Withdrawn && r.Application.EntryId != null)
            .Select(r => r.Application.EntryId!.Value)
            .ToList();
        var withdrawnReasons = withdrawnEntryIds.Count == 0
            ? new Dictionary<Guid, string?>()
            : await db.Entries.AsNoTracking()
                .Where(e => withdrawnEntryIds.Contains(e.Id) && e.WithdrawnReason != null)
                .ToDictionaryAsync(e => e.Id, e => e.WithdrawnReason, ct);
        return rows.Select(r => new ApplicationReviewRow
        {
            Applicant = new Applicant
            {
                UserId = r.Application.FreelancerId,
                DisplayName = r.DisplayName,
                AvatarUrl = AvatarRules.Url(r.Application.FreelancerId, r.AvatarAt),
                Headline = headlines.GetValueOrDefault(r.Application.FreelancerId),
            },
            Application = View(
                r.Application,
                artifacts.GetValueOrDefault(r.Application.Id),
                ApplicationRules.StrongerThan(
                    r.Application.MatchAtSubmit,
                    matches.Where((_, i) => rows[i].Application.Id != r.Application.Id).ToList()),
                categoryLabel, opportunityStatus,
                winnerEntryId is not null && r.Application.EntryId == winnerEntryId, startsAt, now),
            WithdrawnReason = r.Application.Status == ApplicationStatus.Withdrawn && r.Application.EntryId is { } entryId
                ? withdrawnReasons.GetValueOrDefault(entryId)
                : null,
        }).ToList();
    }
}

public sealed record ApplyRequest(
    string? Summary,
    string? Approach,
    Guid[]? ProjectIds,
    string? Commitment,
    int? HoursPerWeek,
    string[]? Advantages,
    string? GithubUsername,
    bool DedicateTime,
    bool OnSchedule,
    bool Competitive,
    bool OriginalWork,
    string? CaptchaToken = null);

public sealed record DecideRequest(string? Decision);

/// <summary>What the applicant has typed so far, if anything — a draft to tighten rather than replace.</summary>
public sealed record ApproachDraftRequest(string? Draft);
