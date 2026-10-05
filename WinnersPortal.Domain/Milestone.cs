namespace WinnersPortal.Domain;

/// <summary>
/// One item of the opportunity's checklist. In phase three this list becomes each
/// entrant's board, stamped by webhook deliveries — which is why the draft the
/// client publishes matters: it is what progress gets measured against.
/// </summary>
public sealed class Milestone
{
    public Guid Id { get; set; }
    public Guid OpportunityId { get; set; }

    /// <summary>Position in the checklist, 0-based.</summary>
    public int Order { get; set; }

    public required string Title { get; set; }
    public string? Description { get; set; }

    /// <summary>
    /// When the client expects this one claimed. Optional, per milestone: a
    /// checklist with no dates behaves exactly as it always has. With one,
    /// the board can tell a claim on time from a claim behind, and an
    /// unclaimed milestone open from overdue — which is what the client
    /// reads while deciding who wins. Never later than the deadline.
    /// </summary>
    public DateTimeOffset? DueUtc { get; set; }

    /// <summary>
    /// This milestone's share of the whole, in percent. Optional: a checklist
    /// may state none, or state one on every milestone adding up to 100 —
    /// nothing between, which publish checks. Shown on the milestone, so an
    /// entrant knows which steps carry the work; it changes no arithmetic
    /// of the board's, whose standing counts milestones, not shares.
    /// </summary>
    public int? WeightPercent { get; set; }

    /// <summary>
    /// What this milestone pays, in the opportunity's currency — only on an
    /// opportunity paid by milestone, where every milestone has one and they
    /// add up to the award. Null on a competitive one.
    /// </summary>
    public decimal? Amount { get; set; }
}
