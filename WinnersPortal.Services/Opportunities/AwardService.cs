using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using WinnersPortal.Domain;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Services.Auth;
using WinnersPortal.Services.Common;
using WinnersPortal.Services.Email;
using WinnersPortal.Services.GitHub;
using WinnersPortal.Services.Live;
using WinnersPortal.Services.Notifications;

namespace WinnersPortal.Services.Opportunities;

/// <summary>
/// The endgame: announce a winner during review, then mark the award paid.
/// The announcement is a promise and the payment is the deal — the repository
/// transfer fires on confirmed payment, never at the announcement, and the
/// worker only writes "verified" after re-reading the repo.
/// </summary>
public sealed partial class AwardService(AppDbContext db, GitHubService github, GitHubWorkSignal githubSignal, EmailWorkSignal emailSignal, PushWorkSignal pushSignal, UnsubscribeTokens unsubscribe, ILiveBoard live, PublicReads publicReads)
{
    public async Task<Outcome<AnnounceResponse>> AnnounceAsync(Guid id, AnnounceRequest request, ClaimsPrincipal principal, CancellationToken ct)
    {
        var clientId = Principal.UserId(principal)!.Value;
        var opportunity = await db.Opportunities.Include(c => c.Client)
            .SingleOrDefaultAsync(c => c.Id == id, ct);
        if (opportunity is null || opportunity.ClientId != clientId) return Outcome.NotFound();
        if (MilestonePay.ByMilestone(opportunity.Kind))
            return Outcome.Conflict("This opportunity is paid by milestone: it has no winner to announce — hire one "
                + "applicant from the review box instead.");
        if (opportunity.Status != OpportunityStatus.Reviewing)
            return Outcome.Conflict(
                opportunity.Status == OpportunityStatus.Awarded
                    ? "This opportunity already has a winner."
                    : "A winner can be announced once the deadline has passed and the opportunity is in review.");

        var entry = await db.Entries.Include(e => e.Freelancer)
            .SingleOrDefaultAsync(e => e.Id == request.EntryId && e.OpportunityId == opportunity.Id, ct);
        if (entry is null || entry.Status != EntryStatus.Active)
            return Outcome.Invalid("That entry is not an active entry in this opportunity.");

        // The announce button stays disabled until GitHub is connected —
        // the winning repository must have somewhere to go. Only enforced
        // while the integration exists at all, and only where there is a
        // repository: files are handed over on this page, not on GitHub.
        if (Delivery.UsesRepository(opportunity.Delivery)
            && await github.IsConfiguredAsync(ct) && opportunity.Client!.GithubLogin is null)
            return Outcome.Conflict(
                "Connect your GitHub account before announcing — the winning repository transfers to it once you pay.");

        var award = new Award
        {
            Id = Guid.NewGuid(),
            OpportunityId = opportunity.Id,
            EntryId = entry.Id,
            Amount = opportunity.AwardAmount,
            Currency = opportunity.Currency,
            AnnouncedAtUtc = DateTimeOffset.UtcNow,
        };
        db.Awards.Add(award);
        opportunity.Status = OpportunityStatus.Awarded;

        // Both sides hear it the moment it is true, in the same save as
        // the award itself — a conflicted announce congratulates nobody.
        Notify.Queue(db, entry.Freelancer!, "award_won",
            Emails.AwardWon(opportunity.Title, opportunity.Slug, award.Amount, award.Currency));
        var losers = await db.Entries.Include(e => e.Freelancer)
            .Where(e => e.OpportunityId == opportunity.Id && e.Status == EntryStatus.Active && e.Id != entry.Id)
            .ToListAsync(ct);
        foreach (var loser in losers)
            Notify.Queue(db, loser.Freelancer!, "award_lost", Emails.AwardLost(opportunity.Title));
        // And everyone who asked to hear about winners — in the same
        // save, for the same reason. The entrants have their own mail.
        await Broadcast.WinnerAnnouncedAsync(db, unsubscribe, opportunity, entry.Freelancer!.DisplayName,
            losers.Select(l => l.FreelancerId).Append(entry.FreelancerId).ToHashSet(), ct);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (DbErrors.IsUniqueViolation(e))
        {
            return Outcome.Conflict("This opportunity already has a winner.");
        }
        publicReads.Clear(); // the card and the winner's standing, from the next page

        githubSignal.Wake(); // losing repos archive now
        emailSignal.Wake();
        pushSignal.Wake();
        // The winner banner is the page's biggest change — every open tab
        // sees it the moment it is true.
        await live.OpportunityChangedAsync(opportunity.Slug, ct);
        return Outcome.Ok(new AnnounceResponse
        {
            Id = award.Id,
            Winner = entry.Freelancer!.DisplayName,
            AnnouncedAtUtc = award.AnnouncedAtUtc,
        });
    }

    public async Task<Outcome<MarkPaidResponse>> MarkPaidAsync(Guid id, ClaimsPrincipal principal, CancellationToken ct)
    {
        var clientId = Principal.UserId(principal)!.Value;
        var award = await db.Awards
            .Include(a => a.Opportunity).ThenInclude(c => c!.Client)
            .Include(a => a.Entry).ThenInclude(e => e!.Freelancer)
            .SingleOrDefaultAsync(a => a.Id == id, ct);
        if (award is null || award.Opportunity!.ClientId != clientId) return Outcome.NotFound();
        if (award.PaidAtUtc is not null)
            return Outcome.Conflict("This award is already marked paid.");
        if (MilestonePay.ByMilestone(award.Opportunity.Kind))
            return Outcome.Conflict("This opportunity is paid by milestone: mark each milestone paid instead — "
                + "the last one completes the award.");

        award.PaidAtUtc = DateTimeOffset.UtcNow;
        StartHandover(award, await github.IsConfiguredAsync(ct));

        // Payment confirmed is the winner's news — with the honest caveat
        // when no transfer could start, because "paid" without the repo
        // moving is exactly what a winner would want to hear about early.
        Notify.Queue(db, award.Entry!.Freelancer!, "award_paid", Emails.AwardPaid(
            award.Opportunity.Title, award.Opportunity.Slug, award.Amount, award.Currency, award.HandoverNote));

        await db.SaveChangesAsync(ct);

        // The client's payment record just improved — on every card they have.
        await Recount.UserAsync(db, clientId, ct);
        publicReads.Clear(); // and on the public lists from the next page
        githubSignal.Wake(); // the transfer job runs now, not on the request thread
        emailSignal.Wake();
        await live.OpportunityChangedAsync(award.Opportunity.Slug, ct); // "payment pending" just turned "paid"
        return Outcome.Ok(new MarkPaidResponse
        {
            PaidAtUtc = award.PaidAtUtc,
            Handover = AwardNames.HandoverName(award.Handover),
            TransferTargetLogin = award.TransferTargetLogin,
            Note = award.HandoverNote,
        });
    }
}

public sealed record AnnounceRequest(Guid EntryId);

public sealed partial class AwardService
{
    /// <summary>
    /// A paid award's handover, started: the repository's transfer to the
    /// client's connected GitHub account is requested — the worker makes
    /// and verifies it — or the note says why nothing transfers. The award
    /// comes with its entry and its opportunity's client loaded. Shared by
    /// a competitive award marked paid and the last milestone of one paid
    /// by milestone.
    /// </summary>
    public static void StartHandover(Award award, bool githubConfigured)
    {
        if (githubConfigured && award.Entry!.RepoFullName is not null && award.Opportunity!.Client!.GithubLogin is not null)
        {
            award.Handover = HandoverStatus.Requested;
            award.TransferTargetLogin = award.Opportunity.Client.GithubLogin;
            return;
        }
        award.HandoverNote = !Delivery.UsesRepository(award.Opportunity!.Delivery)
            ? "The work was handed in as files on this page — they are yours to download; nothing transfers."
            : !githubConfigured
                ? "GitHub is not configured; no repository to transfer."
                : award.Entry!.RepoFullName is null
                    ? "The winning entry has no repository; nothing to transfer."
                    : "No connected GitHub account to transfer to.";
    }
}
