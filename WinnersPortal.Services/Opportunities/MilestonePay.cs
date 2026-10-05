using WinnersPortal.Domain;

namespace WinnersPortal.Services.Opportunities;

/// <summary>Where one milestone of an opportunity paid by milestone stands, for the hired freelancer.</summary>
public enum MilestonePayState
{
    /// <summary>An earlier milestone is not paid yet; this one cannot be handed in.</summary>
    Locked = 0,

    /// <summary>The milestone being worked on: every one before it is paid.</summary>
    Open = 1,

    /// <summary>Handed in; the client is reviewing it.</summary>
    Submitted = 2,

    /// <summary>The client asked for changes; the freelancer hands it in again.</summary>
    ChangesRequested = 3,

    /// <summary>The client approved it; its payment is due.</summary>
    Approved = 4,

    /// <summary>The client confirmed paying it. The next one opens.</summary>
    Paid = 5,
}

/// <summary>
/// The rules of an opportunity paid by milestone, decided without a
/// database: the client hires one applicant; the milestones are worked in
/// order; each is handed in (claimed, as on the board), approved or sent
/// back for changes, and marked paid — and only a paid milestone opens the
/// next. The last payment completes the award, which is what transfers the
/// repository. Pure, so every door's sentence is pinned by tests.
/// </summary>
public static class MilestonePay
{
    public const string CompetitiveName = "competitive";
    public const string MilestonesName = "milestones";

    public static string KindName(OpportunityKind kind) =>
        kind == OpportunityKind.Milestones ? MilestonesName : CompetitiveName;

    /// <summary>The form's word for a kind; blank is competitive, the only kind there was. Null for any other word.</summary>
    public static OpportunityKind? ParseKind(string? word) => word?.Trim().ToLowerInvariant() switch
    {
        null or "" or CompetitiveName => OpportunityKind.Competitive,
        MilestonesName => OpportunityKind.Milestones,
        _ => null,
    };

    public static bool ByMilestone(OpportunityKind kind) => kind == OpportunityKind.Milestones;

    // ------------------------------------------------------------- amounts

    /// <summary>The largest amount a milestone may carry: what the column holds.</summary>
    public const decimal MaxAmount = 9_999_999_999.99m;

    /// <summary>Why one milestone's amount cannot be saved on a draft, or null. Blank is fine while drafting.</summary>
    public static string? DraftAmountProblem(decimal? amount, int position)
    {
        if (amount is not { } a) return null;
        if (a < 0) return $"Milestone {position}'s amount cannot be negative.";
        if (a > MaxAmount) return $"Milestone {position}'s amount is larger than the portal can hold.";
        if (decimal.Round(a, 2) != a) return $"Milestone {position}'s amount has more than two decimal places.";
        return null;
    }

    /// <summary>
    /// Why an opportunity paid by milestone cannot be published as its
    /// amounts stand, or null: every milestone pays something, and together
    /// they are exactly the total the card promises.
    /// </summary>
    public static string? PublishProblem(decimal total, IReadOnlyList<decimal?> amounts)
    {
        for (var i = 0; i < amounts.Count; i++)
            if (amounts[i] is not > 0)
                return $"Give milestone {i + 1} an amount — each milestone is paid on its own once it is approved.";
        var sum = amounts.Sum(a => a!.Value);
        if (sum != total)
            return $"The milestones add up to {sum:N2} but the total is {total:N2}. They must match: the total "
                + "is what the opportunity promises, and the milestones are how it is paid.";
        return null;
    }

    // --------------------------------------------------------------- state

    /// <summary>One milestone's facts, in checklist order: whether it is handed in, and what the client has done with it.</summary>
    public sealed record Step(
        bool Claimed, DateTimeOffset? ChangesRequestedAtUtc, DateTimeOffset? ApprovedAtUtc, DateTimeOffset? PaidAtUtc);

    /// <summary>Each milestone's state, in order. A milestone is open once every one before it is paid.</summary>
    public static IReadOnlyList<MilestonePayState> States(IReadOnlyList<Step> steps)
    {
        var states = new List<MilestonePayState>(steps.Count);
        var previousPaid = true;
        foreach (var step in steps)
        {
            var state = step.PaidAtUtc is not null ? MilestonePayState.Paid
                : step.ApprovedAtUtc is not null ? MilestonePayState.Approved
                : step.Claimed && step.ChangesRequestedAtUtc is not null ? MilestonePayState.ChangesRequested
                : step.Claimed ? MilestonePayState.Submitted
                : previousPaid ? MilestonePayState.Open
                : MilestonePayState.Locked;
            states.Add(state);
            previousPaid = state == MilestonePayState.Paid;
        }
        return states;
    }

    /// <summary>The position (0-based) of the milestone being worked on — the first not yet paid — or null once all are.</summary>
    public static int? Current(IReadOnlyList<MilestonePayState> states)
    {
        for (var i = 0; i < states.Count; i++)
            if (states[i] != MilestonePayState.Paid)
                return i;
        return null;
    }

    /// <summary>Every milestone paid: the award is complete.</summary>
    public static bool Complete(IReadOnlyList<MilestonePayState> states) =>
        states.Count > 0 && states.All(s => s == MilestonePayState.Paid);

    public static string StateName(MilestonePayState state) => state switch
    {
        MilestonePayState.Locked => "locked",
        MilestonePayState.Open => "open",
        MilestonePayState.Submitted => "submitted",
        MilestonePayState.ChangesRequested => "changes_requested",
        MilestonePayState.Approved => "approved",
        _ => "paid",
    };

    /// <summary>How much of the total has been paid, and how much is approved and owed.</summary>
    public static (decimal Paid, decimal Owed) Money(IReadOnlyList<MilestonePayState> states, IReadOnlyList<decimal?> amounts)
    {
        decimal paid = 0, owed = 0;
        for (var i = 0; i < states.Count && i < amounts.Count; i++)
        {
            if (states[i] == MilestonePayState.Paid) paid += amounts[i] ?? 0;
            else if (states[i] == MilestonePayState.Approved) owed += amounts[i] ?? 0;
        }
        return (paid, owed);
    }

    // --------------------------------------------------------------- doors

    /// <summary>
    /// Why a milestone may not be handed in now, or null — the same rule for
    /// a tag, a pull request and an upload. Only while the freelancer is
    /// hired and the award not complete; only the milestone being worked on;
    /// once, unless the client asked for changes, when handing it in again
    /// replaces the first.
    /// </summary>
    public static string? ClaimProblem(OpportunityStatus status, IReadOnlyList<MilestonePayState> states, int order)
    {
        if (status != OpportunityStatus.Awarded)
            return status == OpportunityStatus.Cancelled
                ? "This opportunity was cancelled; nothing more can be handed in."
                : "Milestones are handed in once the client has hired someone.";
        if (order < 0 || order >= states.Count) return $"This opportunity has no milestone {order + 1}.";
        if (Current(states) is not { } current) return "Every milestone is paid; the work is complete.";
        return states[order] switch
        {
            MilestonePayState.Open or MilestonePayState.ChangesRequested => null,
            MilestonePayState.Locked =>
                $"Milestone {order + 1} opens once milestone {current + 1} is approved and paid.",
            MilestonePayState.Submitted => $"Milestone {order + 1} is handed in and waiting for the client's review.",
            MilestonePayState.Approved => $"Milestone {order + 1} is approved; its payment is on the way.",
            _ => $"Milestone {order + 1} is already paid.",
        };
    }

    /// <summary>Why the client may not approve this milestone, or null.</summary>
    public static string? ApproveProblem(MilestonePayState state) => state switch
    {
        MilestonePayState.Submitted => null,
        MilestonePayState.ChangesRequested => "You asked for changes; approve once it is handed in again.",
        MilestonePayState.Approved => "This milestone is already approved.",
        MilestonePayState.Paid => "This milestone is already paid.",
        _ => "This milestone has not been handed in yet.",
    };

    public const int MinChangesNote = 10;

    /// <summary>Why the client may not send this milestone back, or null: only one handed in and not yet approved, with a reason the freelancer can act on.</summary>
    public static string? ChangesProblem(MilestonePayState state, string? note)
    {
        if (state != MilestonePayState.Submitted)
            return state switch
            {
                MilestonePayState.ChangesRequested => "You have already asked for changes to this milestone.",
                MilestonePayState.Approved or MilestonePayState.Paid => "This milestone is already approved.",
                _ => "This milestone has not been handed in yet.",
            };
        var text = note?.Trim() ?? "";
        if (text.Length < MinChangesNote) return "Say what should change — a sentence the freelancer can act on.";
        if (text.Length > Checkpoint.MaxChangesNote) return $"Keep the request under {Checkpoint.MaxChangesNote} characters.";
        return null;
    }

    /// <summary>
    /// Why the client may not mark this milestone paid, or null. A milestone
    /// handed in but not yet approved may be: paying it approves it.
    /// </summary>
    public static string? PayProblem(MilestonePayState state) => state switch
    {
        MilestonePayState.Submitted or MilestonePayState.Approved => null,
        MilestonePayState.ChangesRequested => "You asked for changes to this milestone; it is paid once it is handed in again and approved.",
        MilestonePayState.Paid => "This milestone is already marked paid.",
        _ => "This milestone has not been handed in yet.",
    };

    /// <summary>Why the freelancer may not hand this milestone in again from the page, or null.</summary>
    public static string? ResubmitProblem(MilestonePayState state) =>
        state == MilestonePayState.ChangesRequested ? null : "Only a milestone the client sent back for changes is handed in again.";

    /// <summary>
    /// Why a hired opportunity may not be cancelled now, or null: never with
    /// a milestone handed in and waiting on the client — that one is
    /// approved, sent back or paid first — and never once complete.
    /// </summary>
    public static string? CancelProblem(IReadOnlyList<MilestonePayState> states)
    {
        if (Complete(states)) return "Every milestone is paid; there is nothing left to cancel.";
        if (states.Any(s => s is MilestonePayState.Submitted or MilestonePayState.Approved))
            return "A milestone is handed in and waiting on you. Approve and pay it, or ask for changes, before cancelling.";
        return null;
    }
}
