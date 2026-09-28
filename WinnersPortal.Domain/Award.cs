namespace WinnersPortal.Domain;

public enum HandoverStatus
{
    /// <summary>Winner announced; payment not yet confirmed. No repo moves.</summary>
    NotStarted = 0,

    /// <summary>Payment confirmed and the transfer API call made. A transfer to a
    /// personal account sits pending until the recipient accepts, so this is not done.</summary>
    Requested = 1,

    /// <summary>A re-read of the repository showed the new owner. Only the worker
    /// that re-reads may write this — never the code that fired the transfer.</summary>
    Verified = 2,
}

/// <summary>
/// Written when the winner is announced. Freezes the entry and the amount as
/// they were at that moment — this is the only payout row an opportunity ever has,
/// and the paper trail if a result is contested. The repo transfer fires on
/// confirmed payment, not at the announcement: the announcement is a promise,
/// the payment is the deal.
/// </summary>
public sealed class Award
{
    public Guid Id { get; set; }

    /// <summary>One award per opportunity, enforced by a unique index.</summary>
    public Guid OpportunityId { get; set; }
    public Opportunity? Opportunity { get; set; }

    public Guid EntryId { get; set; }
    public Entry? Entry { get; set; }

    /// <summary>The award as announced. Copied, not joined — the opportunity row may drift, this must not.</summary>
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "USD";

    public DateTimeOffset AnnouncedAtUtc { get; set; }

    /// <summary>Set when the client confirms the payment happened. Triggers the transfer.</summary>
    public DateTimeOffset? PaidAtUtc { get; set; }

    public HandoverStatus Handover { get; set; }

    /// <summary>The connected GitHub login the repository transfers to, captured at payment time.</summary>
    public string? TransferTargetLogin { get; set; }

    public DateTimeOffset? TransferRequestedAtUtc { get; set; }
    public DateTimeOffset? HandoverVerifiedAtUtc { get; set; }

    /// <summary>Operational note — the last transfer error, if the worker hit one.</summary>
    public string? HandoverNote { get; set; }
}
