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
/// An opportunity paid by milestone, after the hire: the client approves a
/// milestone the freelancer handed in, or sends it back with what to
/// change; marks it paid once the money has gone; and the freelancer hands
/// a sent-back one in again. The last milestone paid completes the award —
/// the same moment a competitive award is marked paid, and the same
/// handover. The rules are <see cref="MilestonePay"/>'s.
/// </summary>
public sealed class MilestonePaymentService(
    AppDbContext db, GitHubService github, GitHubWorkSignal githubSignal, EmailWorkSignal emailSignal,
    ILiveBoard live, PublicReads publicReads)
{
    /// <summary>
    /// One hired entry's milestones as the rules read them: the checklist
    /// in order, the claim on each (null where none), and each one's state.
    /// </summary>
    public sealed record Book(
        IReadOnlyList<Milestone> Milestones, IReadOnlyList<Checkpoint?> Claims, IReadOnlyList<MilestonePayState> States)
    {
        public int IndexOf(Guid milestoneId)
        {
            for (var i = 0; i < Milestones.Count; i++)
                if (Milestones[i].Id == milestoneId) return i;
            return -1;
        }
    }

    /// <summary>The book of one entry, its claims tracked so a caller may change them.</summary>
    public static async Task<Book> ReadAsync(AppDbContext db, Guid opportunityId, Guid entryId, CancellationToken ct)
    {
        var milestones = await db.Milestones
            .Where(m => m.OpportunityId == opportunityId)
            .OrderBy(m => m.Order)
            .ToListAsync(ct);
        var claims = await db.Checkpoints.Where(cp => cp.EntryId == entryId).ToListAsync(ct);
        var ordered = milestones.Select(m => claims.FirstOrDefault(cp => cp.MilestoneId == m.Id)).ToList();
        return new Book(milestones, ordered, States(ordered));
    }

    private static IReadOnlyList<MilestonePayState> States(IReadOnlyList<Checkpoint?> claims) =>
        MilestonePay.States(claims
            .Select(cp => new MilestonePay.Step(cp is not null, cp?.ChangesRequestedAtUtc, cp?.ApprovedAtUtc, cp?.PaidAtUtc))
            .ToList());

    // --------------------------------------------------------------- claims

    /// <summary>
    /// A milestone handed in on an opportunity paid by milestone — by tag,
    /// pull request or upload. The first hand-in makes the claim; after the
    /// client asked for changes, handing it in again replaces what the
    /// claim points at and puts it back in front of the client. Saved here,
    /// with the client's email in the same save. The problem when it may
    /// not be handed in now; null and the claim when it was.
    /// </summary>
    public static async Task<(string? Problem, Checkpoint? Claim, bool Again)> HandInAsync(
        AppDbContext db, Entry entry, Opportunity opportunity, int order,
        string via, string reference, string? sha, DateTimeOffset now, CancellationToken ct)
    {
        var book = await ReadAsync(db, opportunity.Id, entry.Id, ct);
        if (MilestonePay.ClaimProblem(opportunity.Status, book.States, order) is { } problem)
            return (problem, null, false);

        var build = opportunity.RequiresCompose && sha is not null;
        var claim = book.Claims[order];
        var again = claim is not null;
        if (claim is null)
        {
            claim = new Checkpoint
            {
                Id = Guid.NewGuid(),
                EntryId = entry.Id,
                MilestoneId = book.Milestones[order].Id,
                Via = via,
                Ref = reference,
            };
            db.Checkpoints.Add(claim);
        }
        else
        {
            // Handed in again after a request for changes: the claim now
            // points at the new work, and the request stays on it as the
            // last one made, answered.
            claim.ChangesRequestedAtUtc = null;
            claim.Via = via;
            claim.Ref = reference;
        }
        claim.CommitSha = sha;
        claim.ClaimedAtUtc = now;
        claim.BuildStatus = build ? PreviewBuildStatus.Pending : PreviewBuildStatus.None;
        claim.BuildDueAtUtc = build ? now : null;
        claim.BuildAttempts = 0;

        var client = await db.Users.SingleAsync(u => u.Id == opportunity.ClientId, ct);
        var freelancer = await db.Users.SingleAsync(u => u.Id == entry.FreelancerId, ct);
        Notify.Queue(db, client, "milestone_submitted", Emails.MilestoneSubmitted(
            opportunity.Title, opportunity.Slug, freelancer.DisplayName, order + 1, book.Milestones[order].Title,
            book.Milestones[order].Amount, opportunity.Currency, again));
        await db.SaveChangesAsync(ct);
        return (null, claim, again);
    }

    // ------------------------------------------------------------- the client

    public Task<Outcome<MilestonePaymentResponse>> ApproveAsync(Guid checkpointId, ClaimsPrincipal principal, CancellationToken ct) =>
        ActAsync(checkpointId, principal, asClient: true, ct, (x, i) =>
        {
            if (MilestonePay.ApproveProblem(x.Book.States[i]) is { } problem) return problem;
            var claim = x.Book.Claims[i]!;
            claim.ApprovedAtUtc = x.Now;
            var m = x.Book.Milestones[i];
            Notify.Queue(db, x.Freelancer, "milestone_approved", Emails.MilestoneApproved(
                x.Opportunity.Title, x.Opportunity.Slug, i + 1, m.Title, m.Amount, x.Opportunity.Currency));
            return null;
        });

    public Task<Outcome<MilestonePaymentResponse>> RequestChangesAsync(
        Guid checkpointId, MilestoneChangesRequest request, ClaimsPrincipal principal, CancellationToken ct) =>
        ActAsync(checkpointId, principal, asClient: true, ct, (x, i) =>
        {
            if (MilestonePay.ChangesProblem(x.Book.States[i], request.Note) is { } problem) return problem;
            var claim = x.Book.Claims[i]!;
            var note = request.Note!.Trim();
            claim.ChangesRequestedAtUtc = x.Now;
            claim.ChangesNote = note;
            Notify.Queue(db, x.Freelancer, "milestone_changes", Emails.MilestoneChangesRequested(
                x.Opportunity.Title, x.Opportunity.Slug, i + 1, x.Book.Milestones[i].Title, note));
            return null;
        });

    public Task<Outcome<MilestonePaymentResponse>> MarkPaidAsync(Guid checkpointId, ClaimsPrincipal principal, CancellationToken ct) =>
        ActAsync(checkpointId, principal, asClient: true, ct, async (x, i) =>
        {
            if (MilestonePay.PayProblem(x.Book.States[i]) is { } problem) return problem;
            var claim = x.Book.Claims[i]!;
            // Paying a milestone handed in approves it: nobody pays for work
            // they have not accepted.
            claim.ApprovedAtUtc ??= x.Now;
            claim.PaidAtUtc = x.Now;
            var states = States(x.Book.Claims);
            var m = x.Book.Milestones[i];

            if (MilestonePay.Complete(states))
            {
                // The last one: the award is paid in full, and the handover
                // starts exactly as a competitive award's does.
                var award = await db.Awards
                    .Include(a => a.Entry)
                    .Include(a => a.Opportunity).ThenInclude(o => o!.Client)
                    .SingleAsync(a => a.OpportunityId == x.Opportunity.Id, ct);
                award.PaidAtUtc = x.Now;
                AwardService.StartHandover(award, await github.IsConfiguredAsync(ct));
                x.Completed = true;
                Notify.Queue(db, x.Freelancer, "award_paid", Emails.MilestonesComplete(
                    x.Opportunity.Title, x.Opportunity.Slug, award.Amount, award.Currency, award.HandoverNote));
            }
            else
            {
                var next = MilestonePay.Current(states);
                Notify.Queue(db, x.Freelancer, "milestone_paid", Emails.MilestonePaid(
                    x.Opportunity.Title, x.Opportunity.Slug, i + 1, m.Title, m.Amount, x.Opportunity.Currency,
                    next + 1, next is { } n ? x.Book.Milestones[n].Title : null));
            }
            return null;
        });

    // --------------------------------------------------------- the freelancer

    /// <summary>
    /// A milestone sent back for changes, handed in again from the page — for
    /// work that is in the repository already, or files already uploaded,
    /// where there is no new tag or file to carry it.
    /// </summary>
    public async Task<Outcome<MilestonePaymentResponse>> ResubmitAsync(Guid checkpointId, ClaimsPrincipal principal, CancellationToken ct)
    {
        var me = Principal.UserId(principal)!.Value;
        var claim = await db.Checkpoints
            .Include(cp => cp.Entry).ThenInclude(e => e!.Opportunity)
            .SingleOrDefaultAsync(cp => cp.Id == checkpointId, ct);
        if (claim?.Entry?.Opportunity is not { } opportunity || claim.Entry.FreelancerId != me
            || claim.Entry.Status != EntryStatus.Active || !MilestonePay.ByMilestone(opportunity.Kind))
            return Outcome.NotFound();
        var book = await ReadAsync(db, opportunity.Id, claim.EntryId, ct);
        var i = book.IndexOf(claim.MilestoneId);
        if (MilestonePay.ResubmitProblem(book.States[i]) is { } problem) return Outcome.Conflict(problem);
        var (refused, _, _) = await HandInAsync(
            db, claim.Entry, opportunity, i, claim.Via, claim.Ref, claim.CommitSha, DateTimeOffset.UtcNow, ct);
        if (refused is not null) return Outcome.Conflict(refused);
        emailSignal.Wake();
        await live.OpportunityChangedAsync(opportunity.Slug, ct);
        return Outcome.Ok(await AnswerAsync(opportunity, claim.EntryId, i, completed: false, ct));
    }

    // ---------------------------------------------------------------- shared

    private sealed class Act(Opportunity opportunity, User freelancer, Book book, DateTimeOffset now)
    {
        public Opportunity Opportunity { get; } = opportunity;
        public User Freelancer { get; } = freelancer;
        public Book Book { get; } = book;
        public DateTimeOffset Now { get; } = now;
        public bool Completed { get; set; }
    }

    private Task<Outcome<MilestonePaymentResponse>> ActAsync(
        Guid checkpointId, ClaimsPrincipal principal, bool asClient, CancellationToken ct, Func<Act, int, string?> step) =>
        ActAsync(checkpointId, principal, asClient, ct, (x, i) => Task.FromResult(step(x, i)));

    /// <summary>The client's side of one milestone: found, checked, changed, saved, and every open tab told.</summary>
    private async Task<Outcome<MilestonePaymentResponse>> ActAsync(
        Guid checkpointId, ClaimsPrincipal principal, bool asClient, CancellationToken ct, Func<Act, int, Task<string?>> step)
    {
        var me = Principal.UserId(principal)!.Value;
        var claim = await db.Checkpoints
            .Include(cp => cp.Entry).ThenInclude(e => e!.Opportunity)
            .Include(cp => cp.Entry).ThenInclude(e => e!.Freelancer)
            .SingleOrDefaultAsync(cp => cp.Id == checkpointId, ct);
        // Not found rather than forbidden: whose milestone it is is not
        // something another client gets to learn.
        if (claim?.Entry?.Opportunity is not { } opportunity || (asClient && opportunity.ClientId != me)
            || !MilestonePay.ByMilestone(opportunity.Kind) || claim.Entry.Status != EntryStatus.Active)
            return Outcome.NotFound();
        if (opportunity.Status != OpportunityStatus.Awarded)
            return Outcome.Conflict(opportunity.Status == OpportunityStatus.Cancelled
                ? "This opportunity was cancelled."
                : "Nobody is hired on this opportunity.");

        var book = await ReadAsync(db, opportunity.Id, claim.EntryId, ct);
        var i = book.IndexOf(claim.MilestoneId);
        var act = new Act(opportunity, claim.Entry.Freelancer!, book, DateTimeOffset.UtcNow);
        if (await step(act, i) is { } problem) return Outcome.Conflict(problem);
        await db.SaveChangesAsync(ct);

        if (act.Completed)
        {
            // The client's payment record just improved, on every card.
            await Recount.UserAsync(db, opportunity.ClientId, ct);
            publicReads.Clear();
            githubSignal.Wake(); // the transfer runs now, not on the request thread
        }
        emailSignal.Wake();
        await live.OpportunityChangedAsync(opportunity.Slug, ct);
        return Outcome.Ok(await AnswerAsync(opportunity, claim.EntryId, i, act.Completed, ct));
    }

    private async Task<MilestonePaymentResponse> AnswerAsync(Opportunity opportunity, Guid entryId, int index, bool completed, CancellationToken ct)
    {
        var book = await ReadAsync(db, opportunity.Id, entryId, ct);
        return new MilestonePaymentResponse
        {
            Number = index + 1,
            State = MilestonePay.StateName(book.States[index]),
            Current = MilestonePay.Current(book.States) + 1,
            Completed = completed || MilestonePay.Complete(book.States),
        };
    }
}

/// <summary>The client's request for changes: what to change, in a sentence the freelancer can act on.</summary>
public sealed record MilestoneChangesRequest(string? Note);
