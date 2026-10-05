using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using WinnersPortal.Domain;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Services.Auth;
using WinnersPortal.Services.Common;
using WinnersPortal.Services.Email;
using WinnersPortal.Services.GitHub;
using WinnersPortal.Services.Live;

namespace WinnersPortal.Services.Opportunities;

/// <summary>
/// Validation for cancelling an opportunity — pure so the rules are testable
/// without a database and identical wherever they are enforced.
/// </summary>
public static class CancelRules
{
    public const int MinReason = 10;
    public const int MaxReason = 500;

    /// <summary>
    /// The two states with something to call off. A draft was never public,
    /// and an awarded opportunity is a promise already made — the award row is
    /// the record now, and cancelling cannot unwrite it.
    /// </summary>
    /// <remarks>
    /// Paid by milestone, a hired job may be called off too — a freelancer
    /// who stopped answering must not hold it forever — but never with a
    /// milestone handed in and waiting on the client
    /// (<see cref="MilestonePay.CancelProblem"/>). What was paid stays paid.
    /// </remarks>
    public static bool CanCancel(OpportunityStatus status, OpportunityKind kind = OpportunityKind.Competitive) =>
        status is OpportunityStatus.Open or OpportunityStatus.Reviewing
        || (MilestonePay.ByMilestone(kind) && status == OpportunityStatus.Awarded);

    /// <summary>Null when the cancellation is acceptable, else the message the form shows.</summary>
    /// <param name="states">Paid by milestone and hired: where each milestone stands.</param>
    public static string? Problem(
        OpportunityStatus status, string? reason,
        OpportunityKind kind = OpportunityKind.Competitive, IReadOnlyList<MilestonePayState>? states = null) =>
        status switch
        {
            OpportunityStatus.Draft => "A draft is not public — nothing was promised, so there is nothing to call off.",
            OpportunityStatus.Awarded when MilestonePay.ByMilestone(kind)
                && MilestonePay.CancelProblem(states ?? []) is { } waiting => waiting,
            OpportunityStatus.Awarded when !MilestonePay.ByMilestone(kind) =>
                "This opportunity has a winner. The award is the record now; cancelling cannot unwrite it.",
            OpportunityStatus.Cancelled => "This opportunity is already cancelled.",
            _ => CleanReason(reason) is not { } r
                ? $"Say why, in at least {MinReason} characters — the reason goes to every entrant who staked work on this brief."
                : r.Length > MaxReason
                    ? $"Keep the reason under {MaxReason} characters — it is an explanation, not a post-mortem."
                    : null,
        };

    /// <summary>Trimmed reason, or null when too short to tell entrants anything.</summary>
    public static string? CleanReason(string? reason) =>
        reason?.Trim() is { } r && r.Length >= MinReason ? r : null;
}

/// <summary>
/// The exit that is not a winner. Cancelling voids every entry, tells every
/// entrant why, and goes on the client's public record — the counterweight
/// that keeps "call the whole thing off" from being free. Repositories are
/// archived by the worker, never deleted: the work stays its author's.
/// </summary>
public sealed class CancelService(AppDbContext db, GitHubWorkSignal githubSignal, EmailWorkSignal emailSignal, ILiveBoard live, PublicReads publicReads)
{
    public async Task<Outcome<CancelResponse>> CancelOpportunityAsync(Guid id, CancelRequest request, ClaimsPrincipal principal, CancellationToken ct)
    {
        var clientId = Principal.UserId(principal)!.Value;
        var opportunity = await db.Opportunities
            .SingleOrDefaultAsync(c => c.Id == id, ct);
        if (opportunity is null || opportunity.ClientId != clientId) return Outcome.NotFound();

        // Paid by milestone and hired: where the milestones stand decides.
        var hired = MilestonePay.ByMilestone(opportunity.Kind) && opportunity.Status == OpportunityStatus.Awarded
            ? await db.Awards.Where(a => a.OpportunityId == opportunity.Id).Select(a => (Guid?)a.EntryId).SingleOrDefaultAsync(ct)
            : null;
        var states = hired is { } entryId
            ? (await MilestonePaymentService.ReadAsync(db, opportunity.Id, entryId, ct)).States
            : null;
        if (CancelRules.Problem(opportunity.Status, request.Reason, opportunity.Kind, states) is { } problem)
            return CancelRules.CanCancel(opportunity.Status, opportunity.Kind) && CancelRules.CleanReason(request.Reason) is null
                ? Outcome.Invalid(problem)
                : Outcome.Conflict(problem);

        await CancelAsync(db, opportunity, CancelRules.CleanReason(request.Reason)!, ct);
        await db.SaveChangesAsync(ct);
        publicReads.Clear(); // off the feed from the next page, not the next interval

        githubSignal.Wake(); // the worker archives every entry's repo — read-only, never deleted
        emailSignal.Wake();
        await live.OpportunityChangedAsync(opportunity.Slug, ct); // open tabs see the notice, not a stale board
        return Outcome.Ok(new CancelResponse
        {
            Status = OpportunityNames.StatusName(opportunity.Status),
            CancelledAtUtc = opportunity.CancelledAtUtc,
        });
    }

    /// <summary>
    /// The cancellation itself, in the caller's unit of work: the status,
    /// and every active entrant's notice queued in the same save that makes
    /// it true, so a rolled-back cancellation has never apologised. Entries
    /// stay Active rows -- the opportunity status carries the voiding, so the
    /// record still shows who was in when it died. Shared with the account
    /// erasure, which cancels whatever a deleted client left running.
    /// </summary>
    public static async Task CancelAsync(AppDbContext db, Opportunity opportunity, string reason, CancellationToken ct)
    {
        opportunity.Status = OpportunityStatus.Cancelled;
        opportunity.CancelledAtUtc = DateTimeOffset.UtcNow;
        opportunity.CancelReason = reason;

        var entrants = await db.Entries.Include(e => e.Freelancer)
            .Where(e => e.OpportunityId == opportunity.Id && e.Status == EntryStatus.Active)
            .ToListAsync(ct);
        foreach (var entry in entrants)
            Notify.Queue(db, entry.Freelancer!, "opportunity_cancelled",
                Emails.OpportunityCancelled(opportunity.Title, opportunity.Slug, reason));

        // Applicants nobody answered: the cancellation closes the door on
        // them too, and they are told in the same save.
        var undecided = await db.Applications.Include(a => a.Freelancer)
            .Where(a => a.OpportunityId == opportunity.Id && a.Status == ApplicationStatus.UnderReview)
            .ToListAsync(ct);
        foreach (var application in undecided)
            Notify.Queue(db, application.Freelancer!, "application_closed_undecided",
                Emails.ApplicationClosedUndecided(opportunity.Title, cancelled: true));
    }
}

public sealed record CancelRequest(string? Reason);
