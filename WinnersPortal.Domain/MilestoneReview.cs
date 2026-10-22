namespace WinnersPortal.Domain;

/// <summary>
/// One decision on a milestone claimed: approved, or sent back for changes
/// — with the remarks that went with it and, where the reviewer had any,
/// advice for the milestones ahead. Every decision is kept, so a milestone
/// sent back twice shows both rounds, and the freelancer reads the whole
/// conversation on the board.
///
/// On an opportunity paid by milestone the claim is the hired freelancer's
/// hand-in, and the decision gates its payment: the claim
/// (<see cref="Checkpoint"/>) carries the state, these rows its record. On
/// a competitive opportunity the claim is an entrant's, and the decision is
/// feedback alone — these rows are all there is of it, read by that entrant,
/// the client and administrators, never by the other entrants.
/// </summary>
public sealed class MilestoneReview
{
    /// <summary>The longest remark and the longest advice a reviewer may write.</summary>
    public const int MaxNote = 1000;
    public const int MaxAdvice = 1000;

    public Guid Id { get; set; }

    public Guid CheckpointId { get; set; }
    public Checkpoint? Checkpoint { get; set; }

    public MilestoneReviewDecision Decision { get; set; }

    /// <summary>
    /// The remarks: what should change when sent back (always written), what
    /// was good and worth keeping when approved (optional).
    /// </summary>
    public string? Note { get; set; }

    /// <summary>Advice for the milestones ahead — direction, not a verdict. Optional either way.</summary>
    public string? Advice { get; set; }

    /// <summary>Who decided: the client, or an administrator acting for them. Not a foreign key, like a decision's.</summary>
    public Guid ByUserId { get; set; }

    /// <summary>An administrator reviewed on the client's behalf; the client was told.</summary>
    public bool OnBehalf { get; set; }

    public DateTimeOffset AtUtc { get; set; }
}

public enum MilestoneReviewDecision
{
    /// <summary>The work is accepted — paid by milestone, its payment is due; competitive, it meets the brief so far.</summary>
    Approved = 1,

    /// <summary>Sent back with what to change; the freelancer hands it in again, or — competitive — updates it before the deadline.</summary>
    ChangesRequested = 2,
}
