using System.Security.Claims;
using System.Text.RegularExpressions;
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
using WinnersPortal.Services.Identity;
using WinnersPortal.Services.Settings;

namespace WinnersPortal.Services.Opportunities;

public sealed partial class EntryService(AppDbContext db, GitHubWorkSignal githubSignal, ILiveBoard live, ActivityNote activity, EmailWorkSignal emailSignal, AiOptions ai, AiWorkSignal aiSignal, Preview.BuildHostService buildHost)
{
    // GitHub's own rules: 1–39 chars, alphanumeric or hyphen, no leading,
    // trailing, or doubled hyphen. Wrong here means phase three's repo
    // invitation goes nowhere, so it is validated at the door.
    [GeneratedRegex("^[a-zA-Z0-9](?:[a-zA-Z0-9]|-(?=[a-zA-Z0-9])){0,38}$")]
    public static partial Regex GithubUsername();

    public async Task<Outcome> WithdrawAsync(Guid id, WithdrawEntryRequest? request, ClaimsPrincipal principal, CancellationToken ct)
    {
        // Why, if they said. Optional; kept on the entry for the client to
        // read under the Withdrawn application, and on the activity row —
        // set there before the checks, so a refused withdrawal still shows
        // what was said.
        var reason = ActivityNames.Detail(request?.Reason);
        activity.Detail = reason;
        var entry = await db.Entries.Include(e => e.Opportunity)
            .SingleOrDefaultAsync(e => e.Id == id, ct);
        if (entry is null || entry.FreelancerId != Principal.UserId(principal)) return Outcome.NotFound();
        if (entry.Status != EntryStatus.Active)
            return Outcome.Conflict("This entry is already withdrawn.");
        if (entry.Opportunity!.Status != OpportunityStatus.Open)
            return Outcome.Conflict("Entries cannot be withdrawn once the opportunity has moved to review.");

        entry.Status = EntryStatus.Withdrawn;
        entry.WithdrawnAtUtc = DateTimeOffset.UtcNow;
        entry.WithdrawnReason = reason;
        // The application this entry was selected from reads Withdrawn
        // from now on — their own answer, and final — so neither side is
        // left reading a selection that no longer stands.
        var application = await db.Applications.SingleOrDefaultAsync(a => a.EntryId == entry.Id, ct);
        if (application is not null)
        {
            application.Status = ApplicationStatus.Withdrawn;
            application.DecidedAtUtc = entry.WithdrawnAtUtc;
        }
        await db.SaveChangesAsync(ct);
        await Recount.OpportunityAsync(db, entry.OpportunityId, ct); // the card just lost an entrant
        githubSignal.Wake(); // the worker archives the withdrawn repo
        await live.OpportunityChangedAsync(entry.Opportunity.Slug, ct); // and the public list just shrank
        return Outcome.NoContent();
    }

    public async Task<Outcome<RemoveEntryResponse>> RemoveAsync(Guid id, RemoveEntryRequest request, ClaimsPrincipal principal, CancellationToken ct)
    {
        var meId = Principal.UserId(principal)!.Value;
        var isAdmin = principal.IsInRole(Roles.Admin);
        // The reason is the account of the decision, so the activity row
        // carries it — including on a refusal, which says what was tried.
        activity.Detail = ActivityNames.Detail(request.Reason);

        var entry = await db.Entries
            .Include(e => e.Opportunity).ThenInclude(c => c!.Client)
            .Include(e => e.Freelancer)
            .SingleOrDefaultAsync(e => e.Id == id, ct);
        // Not found rather than forbidden: whether a given entry id
        // exists is not something a stranger gets to learn.
        if (entry is null) return Outcome.NotFound();
        var opportunity = entry.Opportunity!;
        if (opportunity.ClientId != meId && !isAdmin) return Outcome.NotFound();

        var (reasonOk, reason) = Removal.CleanReason(request.Reason);
        if (!reasonOk)
            return Outcome.Invalid(
                $"Give a reason of at least {Removal.MinReason} characters. It is sent to "
                    + "the entrant word for word, and is the only account they will get.");

        var isWinner = await db.Awards.AnyAsync(a => a.EntryId == entry.Id, ct);
        var problem = Removal.Problem(opportunity.Status, entry.Status, isWinner, isAdmin);
        if (problem is not null) return Outcome.Conflict(problem);

        var now = DateTimeOffset.UtcNow;
        entry.Status = EntryStatus.Removed;
        entry.RemovedAtUtc = now;
        entry.RemovedReason = reason;
        entry.RemovedByUserId = meId;
        entry.RepoAccessEndsAtUtc = Removal.AccessEndsAt(now);
        // The application this entry was selected from reads Removed from
        // now on, in the client's review box and on the applicant's page.
        var application = await db.Applications.SingleOrDefaultAsync(a => a.EntryId == entry.Id, ct);
        if (application is not null)
        {
            application.Status = ApplicationStatus.Removed;
            application.DecidedAtUtc = now;
            application.DecidedByUserId = meId;
        }

        Notify.Queue(db, entry.Freelancer!, "entry_removed", Emails.EntryRemoved(
            opportunity.Title, opportunity.Slug, reason!, Removal.CloneGraceDays,
            entry.RepoFullName, byAdmin: isAdmin && opportunity.ClientId != meId));
        // An administrator acting over the client: their opportunity just
        // lost an entrant and they did not do it.
        if (isAdmin && opportunity.ClientId != meId)
            Notify.Queue(db, opportunity.Client!, "entrant_removed_by_admin", Emails.EntrantRemovedByAdmin(
                opportunity.Title, opportunity.Slug, entry.Freelancer!.DisplayName, reason!));

        await db.SaveChangesAsync(ct);
        await Recount.OpportunityAsync(db, opportunity.Id, ct); // the card just lost an entrant
        // The worker archives the repository — which is what stops every
        // push — and takes the collaborator off once the window ends.
        githubSignal.Wake();
        emailSignal.Wake();
        await live.OpportunityChangedAsync(opportunity.Slug, ct); // and the public board just lost a row
        return Outcome.Ok(new RemoveEntryResponse
        {
            RemovedAtUtc = now,
            RepoAccessEndsAtUtc = entry.RepoAccessEndsAtUtc,
        });
    }

    public async Task<Outcome<IEnumerable<MyEntryRow>>> MineAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        var freelancerId = Principal.UserId(principal)!.Value;
        // Their own side of the fit, read once: the ring on each row is
        // the same judgement the browse card and the door make.
        var viewer = await FitReader.ForAsync(db, freelancerId, Principal.Role(principal), ai, aiSignal, ct);
        var rows = await db.Entries.AsNoTracking()
            // A selection taken back is not an entry they made or left:
            // their application, now not selected, says what happened.
            .Where(e => e.FreelancerId == freelancerId && e.Status != EntryStatus.Deselected)
            .OrderByDescending(e => e.CreatedAtUtc)
            .Select(e => new
            {
                e.Id, e.OpportunityId, e.Status, e.GithubUsername, e.RepoFullName, e.CreatedAtUtc,
                e.ProvisionStatus, e.LastPushAtUtc,
                e.RemovedAtUtc, e.RemovedReason, e.RepoAccessEndsAtUtc,
                DoneCount = e.Checkpoints.Count,
                MilestoneCount = e.Opportunity!.Milestones.Count,
                OpportunitySlug = e.Opportunity.Slug,
                OpportunityTitle = e.Opportunity.Title,
                e.Opportunity.AwardAmount,
                e.Opportunity.Currency,
                e.Opportunity.DeadlineUtc,
                e.Opportunity.PublishedAtUtc,
                e.Opportunity.StartsAtUtc,
                OpportunityStatus = e.Opportunity.Status,
                OpportunityDelivery = e.Opportunity.Delivery,
                e.Opportunity.MinMeritScore,
                OpportunityCategory = e.Opportunity.Category,
                OpportunitySkills = e.Opportunity.Skills.OrderBy(s => s.Order).Select(s => s.Name).ToList(),
                FilesUploaded = e.Submissions.Count(s => s.UploadedAtUtc != null),
                // Whether this entry is the one the award went to. Two
                // entrants in the same awarded opportunity see the same
                // "Awarded" badge; only this tells them apart.
                Won = db.Awards.Any(a => a.EntryId == e.Id),
                AwardPaidAtUtc = db.Awards
                    .Where(a => a.EntryId == e.Id)
                    .Select(a => a.PaidAtUtc)
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        // Where each live entry stands on its board — the same number the
        // opportunity page orders by, so this list and that page agree.
        var standings = await StandingReader.ForOpportunitiesAsync(
            db,
            rows.Where(e => e.Status == EntryStatus.Active
                    && e.OpportunityStatus is OpportunityStatus.Open or OpportunityStatus.Reviewing)
                .Select(e => e.OpportunityId).Distinct().ToList(),
            DateTimeOffset.UtcNow, await buildHost.ReadyAsync(ct), ct);

        return Outcome.Ok(rows.Select(e => new MyEntryRow
        {
            Id = e.Id,
            Status = EntryNames.StatusName(e.Status),
            Standing = standings.TryGetValue(e.Id, out var st)
                ? new StandingSummary { Score = st.Score, Band = Standing.BandName(st.Band), Rank = st.Rank, Of = st.Of }
                : null,
            // The client's reason, unedited — this page is the only place
            // a removed entrant can read it back after the email.
            RemovedAtUtc = e.RemovedAtUtc,
            RemovedReason = e.RemovedReason,
            RepoAccessEndsAtUtc = e.RepoAccessEndsAtUtc,
            GithubUsername = e.GithubUsername,
            RepoFullName = e.RepoFullName,
            ProvisionStatus = OpportunityNames.ProvisionName(e.ProvisionStatus),
            LastPushAtUtc = e.LastPushAtUtc,
            MilestonesDone = e.DoneCount,
            MilestoneCount = e.MilestoneCount,
            FilesUploaded = e.FilesUploaded,
            EnteredAtUtc = e.CreatedAtUtc,
            Won = e.Won,
            AwardPaidAtUtc = e.AwardPaidAtUtc,
            Fit = viewer is null
                ? null
                : FitReader.Dto(viewer.Judge(
                    e.MinMeritScore, e.OpportunitySkills, e.OpportunityCategory,
                    Schedule.StartsAt(e.StartsAtUtc, e.PublishedAtUtc), e.DeadlineUtc)),
            Opportunity = new OpportunitySummary
            {
                Slug = e.OpportunitySlug,
                Title = e.OpportunityTitle,
                AwardAmount = e.AwardAmount,
                Currency = e.Currency,
                DeadlineUtc = e.DeadlineUtc,
                StartsAtUtc = e.StartsAtUtc,
                PublishedAtUtc = e.PublishedAtUtc,
                Status = OpportunityNames.StatusName(e.OpportunityStatus),
                Delivery = Delivery.Name(e.OpportunityDelivery),
            },
        }));
    }

    /// <summary>The outcome of trying to put one freelancer into an opportunity: the entry and their number, or why not.</summary>
    internal sealed record Joined(Entry? Entry, int EntrantNumber, string? Problem);

    /// <summary>
    /// The door, as it always was, and the row behind it. The opportunity's
    /// state, its last joining date, its merit floor and required skills
    /// judged against the freelancer's profile as it is now, a removal
    /// that bars re-entry, and the portal's cap — each refuses with the
    /// reason. Past them, the entry is added to the context and handed
    /// back unsaved: the caller's save commits it with whatever else the
    /// moment carries (the application's decision, the emails), and the
    /// one-active-entry index turns a race into one row. The opportunity must
    /// come with its skills loaded.
    /// </summary>
    internal static async Task<Joined> JoinAsync(
        AppDbContext db, Opportunity opportunity, Guid freelancerId, string? role, string githubUsername, string? note,
        SettingsService settings, AiOptions ai, AiWorkSignal aiSignal, IdentityOptions identity, CancellationToken ct)
    {
        static Joined No(string problem) => new(null, 0, problem);

        // The username is where the repository invitation goes. An opportunity
        // delivered by upload alone sends none, so it stores none, so
        // nothing downstream mistakes "" for a login.
        if (Delivery.NeedsGithubUsername(opportunity.Delivery))
        {
            if (!GithubUsername().IsMatch(githubUsername))
                return No("That does not look like a GitHub username (letters, digits, single hyphens; max 39).");
        }
        else
        {
            githubUsername = "";
        }

        if (opportunity.Status != OpportunityStatus.Open)
            return No("This opportunity is no longer open for entry.");
        // The last joining date, which is the deadline unless the client
        // set an earlier one. An opportunity can be mid-build and shut to new
        // entrants at the same time, so the two are asked separately. Paid
        // by milestone, the dates close applications, not the hire: a
        // client may read every application and hire after they close.
        if (!MilestonePay.ByMilestone(opportunity.Kind) && !Schedule.EntryOpen(opportunity.Status, opportunity.EntryCloseUtc, opportunity.DeadlineUtc, DateTimeOffset.UtcNow))
            return No(Schedule.EntryClosesAt(opportunity.EntryCloseUtc, opportunity.DeadlineUtc) == opportunity.DeadlineUtc
                ? "The deadline has passed; entry is closed."
                : "The last joining date has passed — this opportunity is closed to new entrants, "
                    + "though the entrants already in it build on until the deadline.");

        // The portal's own door, judged now — a verification an
        // administrator took back since the application was read shuts
        // it again.
        if (await identity.IsEnabledAsync(ct) && await identity.RequiredForApplyAsync(ct)
            && await db.Users.Where(u => u.Id == freelancerId).Select(u => u.IdentityVerifiedAtUtc).SingleAsync(ct) is null)
            return No(Identity.IdentityRules.ApplyProblem);

        // The client's own terms for who may enter: a merit floor and the
        // skills a profile must list, judged now — a profile can have
        // changed since the application was read.
        var viewer = await FitReader.ForAsync(db, freelancerId, role, ai, aiSignal, ct);
        if (viewer is not null
            && OpportunityFit.EntryProblem(viewer.Judge(
                opportunity.MinMeritScore,
                opportunity.Skills.OrderBy(s => s.Order).Select(s => s.Name).ToList(),
                opportunity.Category,
                Schedule.StartsAt(opportunity.StartsAtUtc, opportunity.PublishedAtUtc),
                opportunity.DeadlineUtc)) is { } unfit)
            return No(unfit);

        // Removal has to mean something: the database's partial unique
        // index only guards active rows, so nothing but this stops a
        // removed entrant from simply coming back in.
        var removedBefore = await db.Entries.AnyAsync(
            e => e.OpportunityId == opportunity.Id && e.FreelancerId == freelancerId && e.Status == EntryStatus.Removed, ct);
        if (Removal.ReentryProblem(removedBefore) is { } shut) return No(shut);

        var activeEntries = await db.Entries.CountAsync(
            e => e.OpportunityId == opportunity.Id && e.Status == EntryStatus.Active, ct);

        // limits.maxEntriesPerOpportunity: zero means unlimited and is the
        // intended default. A best-effort check, not a lock: a photo-finish
        // race can land one entry over, which is the right trade against
        // serialising every join.
        _ = int.TryParse(await settings.GetAsync("limits.maxEntriesPerOpportunity", ct), out var cap);
        if (cap > 0 && activeEntries >= cap)
            return No($"This opportunity is full — the portal caps entries at {cap} per opportunity. "
                + "Withdrawals free a slot.");

        var entry = new Entry
        {
            Id = Guid.NewGuid(),
            OpportunityId = opportunity.Id,
            FreelancerId = freelancerId,
            GithubUsername = githubUsername,
            Note = note,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        db.Entries.Add(entry);
        return new(entry, activeEntries + 1, null);
    }
}

public sealed record RemoveEntryRequest(string? Reason);

/// <summary>An optional reason; a withdrawal with no body at all is still a withdrawal.</summary>
public sealed record WithdrawEntryRequest(string? Reason);
