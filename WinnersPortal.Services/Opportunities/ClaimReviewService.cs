using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using WinnersPortal.Domain;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Services.Auth;
using WinnersPortal.Services.Common;
using WinnersPortal.Services.Email;
using WinnersPortal.Services.Live;

namespace WinnersPortal.Services.Opportunities;

/// <summary>
/// A milestone an entrant claimed on a competitive opportunity, reviewed by
/// the client — or by an administrator for them, which the client is told
/// about. Each decision is a <see cref="MilestoneReview"/> row, emailed to
/// the entrant with its words; nothing else changes. The rules are
/// <see cref="ClaimReview"/>'s; a job paid by milestone is reviewed by
/// <see cref="MilestonePaymentService"/>, where the review gates payment.
/// </summary>
public sealed class ClaimReviewService(AppDbContext db, EmailWorkSignal emailSignal, ILiveBoard live)
{
    public async Task<Outcome> ReviewAsync(
        Guid checkpointId, ClaimReviewRequest request, ClaimsPrincipal principal, CancellationToken ct)
    {
        var me = Principal.UserId(principal)!.Value;
        var claim = await db.Checkpoints
            .Include(cp => cp.Milestone)
            .Include(cp => cp.Entry).ThenInclude(e => e!.Opportunity).ThenInclude(o => o!.Client)
            .Include(cp => cp.Entry).ThenInclude(e => e!.Freelancer)
            .SingleOrDefaultAsync(cp => cp.Id == checkpointId, ct);
        // Not found rather than forbidden, for anyone but the client and an
        // administrator: whose claim it is is not another member's to learn.
        // A job paid by milestone has its own review, which gates payment.
        var byAdmin = principal.IsInRole(Roles.Admin) && claim?.Entry?.Opportunity?.ClientId != me;
        if (claim?.Entry?.Opportunity is not { } opportunity || (opportunity.ClientId != me && !byAdmin)
            || MilestonePay.ByMilestone(opportunity.Kind) || claim.Entry.Status != EntryStatus.Active)
            return Outcome.NotFound();
        if (ClaimReview.ParseDecision(request.Decision) is not { } decision)
            return Outcome.Invalid("Choose approve or changes.");
        if (ClaimReview.Problem(opportunity.Status, decision, request.Note, request.Advice) is { } problem)
            return Outcome.Conflict(problem);

        var review = new MilestoneReview
        {
            Id = Guid.NewGuid(),
            CheckpointId = claim.Id,
            Decision = decision,
            Note = MilestonePay.Clean(request.Note),
            Advice = MilestonePay.Clean(request.Advice),
            ByUserId = me,
            OnBehalf = byAdmin,
            AtUtc = DateTimeOffset.UtcNow,
        };
        db.MilestoneReviews.Add(review);

        var approved = decision == MilestoneReviewDecision.Approved;
        var number = claim.Milestone!.Order + 1;
        Notify.Queue(db, claim.Entry.Freelancer!, approved ? "claim_approved" : "claim_changes", Emails.ClaimReviewed(
            opportunity.Title, opportunity.Slug, opportunity.Delivery, number, claim.Milestone.Title,
            approved, review.Note, review.Advice, byAdmin,
            deadlinePassed: opportunity.Status != OpportunityStatus.Open));
        if (byAdmin)
            Notify.Queue(db, opportunity.Client!, "claim_reviewed_by_admin", Emails.ClaimReviewedByAdmin(
                opportunity.Title, opportunity.Slug, claim.Entry.Freelancer!.DisplayName, number, claim.Milestone.Title,
                approved, review.Note, review.Advice));
        await db.SaveChangesAsync(ct);

        emailSignal.Wake();
        await live.OpportunityChangedAsync(opportunity.Slug, ct);
        return Outcome.NoContent();
    }
}

/// <summary>
/// A review of a claimed milestone on a competitive opportunity: the
/// decision — "approve" or "changes" — and, as on a job paid by milestone,
/// the remarks (required when asking for changes) and optional advice.
/// </summary>
public sealed record ClaimReviewRequest(string? Decision, string? Note, string? Advice);
