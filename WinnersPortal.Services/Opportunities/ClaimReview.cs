using WinnersPortal.Domain;

namespace WinnersPortal.Services.Opportunities;

/// <summary>
/// A milestone claimed on a competitive opportunity, reviewed. The client —
/// or an administrator for them — approves the claim or asks for changes,
/// with remarks and advice for the milestones ahead, from its cell on the
/// Progress board: while entries run, on the files tagged with the milestone
/// (they open to the client as the claim lands), and after the deadline on
/// the whole entry. It is feedback, not a gate: it pays nothing, opens and
/// closes nothing, and does not pick the winner; only the entrant it is
/// about reads it. The words follow the paid-by-milestone rules
/// (<see cref="MilestonePay"/>): a request for changes says what, in a
/// sentence; the rest is optional and only ever too long.
/// </summary>
public static class ClaimReview
{
    /// <summary>
    /// Why a claim may not be reviewed now, or null: while entries run and
    /// while the client reads them, not once the winner is announced — the
    /// board is a record then — nor on a cancelled opportunity.
    /// </summary>
    public static string? StatusProblem(OpportunityStatus status) => status switch
    {
        OpportunityStatus.Open or OpportunityStatus.Reviewing => null,
        OpportunityStatus.Awarded => "The winner is announced; the board is a record now.",
        OpportunityStatus.Cancelled => "This opportunity was cancelled.",
        _ => "This opportunity is not open.",
    };

    /// <summary>Why this review cannot be saved, or null.</summary>
    public static string? Problem(OpportunityStatus status, MilestoneReviewDecision decision, string? note, string? advice) =>
        StatusProblem(status)
        ?? (decision == MilestoneReviewDecision.ChangesRequested
            ? MilestonePay.ChangesWordsProblem(note, advice)
            : MilestonePay.ApprovalWordsProblem(note, advice));

    /// <summary>The decision a request names — "approve" or "changes" — or null for anything else.</summary>
    public static MilestoneReviewDecision? ParseDecision(string? decision) => decision?.Trim().ToLowerInvariant() switch
    {
        "approve" or "approved" => MilestoneReviewDecision.Approved,
        "changes" or "changes_requested" => MilestoneReviewDecision.ChangesRequested,
        _ => null,
    };

    /// <summary>
    /// Where one board cell stands with its reviewer: unclaimed; claimed and
    /// not yet reviewed; approved; changes asked for — or updated, once work
    /// for it landed after the request, so the client knows to look again.
    /// </summary>
    /// <param name="claimed">Whether the entrant has claimed the milestone.</param>
    /// <param name="latest">The latest decision on the claim, if any.</param>
    /// <param name="latestAtUtc">When it was made.</param>
    /// <param name="workAtUtc">The latest work for the milestone: a file tagged with it, or a push to the repository.</param>
    public static string StateName(bool claimed, MilestoneReviewDecision? latest, DateTimeOffset? latestAtUtc, DateTimeOffset? workAtUtc) =>
        !claimed ? "unclaimed"
        : latest switch
        {
            null => "unreviewed",
            MilestoneReviewDecision.Approved => "approved",
            _ => workAtUtc is { } work && latestAtUtc is { } at && work > at ? "updated" : "changes_requested",
        };
}
