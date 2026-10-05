using System.Text.Json;
using WinnersPortal.Domain;
using WinnersPortal.Services.Ai;
using WinnersPortal.Services.Opportunities;
using Xunit;

namespace WinnersPortal.Api.Tests;

/// <summary>
/// The opportunity paid by milestone: one hired freelancer, the milestones
/// worked in order, each approved (or sent back) and paid before the next
/// opens, the last payment completing the award. The rules are pure, so
/// every door's sentence is pinned here.
/// </summary>
public class MilestonePayTests
{
    private static readonly DateTimeOffset T = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static MilestonePay.Step None => new(false, null, null, null);
    private static MilestonePay.Step Handed => new(true, null, null, null);
    private static MilestonePay.Step Sent => new(true, T, null, null);
    private static MilestonePay.Step Approved => new(true, null, T, null);
    private static MilestonePay.Step Paid => new(true, null, T, T);

    // ----------------------------------------------------------------- kind

    [Theory]
    [InlineData(null, OpportunityKind.Competitive)]
    [InlineData("", OpportunityKind.Competitive)]
    [InlineData("competitive", OpportunityKind.Competitive)]
    [InlineData(" Milestones ", OpportunityKind.Milestones)]
    public void The_form_names_a_kind_and_blank_is_competitive(string? word, OpportunityKind kind) =>
        Assert.Equal(kind, MilestonePay.ParseKind(word));

    [Fact]
    public void An_unknown_kind_is_refused_and_each_kind_has_one_wire_name()
    {
        Assert.Null(MilestonePay.ParseKind("hourly"));
        Assert.Equal("competitive", MilestonePay.KindName(OpportunityKind.Competitive));
        Assert.Equal("milestones", MilestonePay.KindName(OpportunityKind.Milestones));
    }

    // -------------------------------------------------------------- amounts

    [Fact]
    public void A_draft_may_leave_an_amount_blank_but_not_negative_or_past_the_cent()
    {
        Assert.Null(MilestonePay.DraftAmountProblem(null, 1));
        Assert.Null(MilestonePay.DraftAmountProblem(250.50m, 1));
        Assert.Contains("negative", MilestonePay.DraftAmountProblem(-1, 2));
        Assert.Contains("two decimal places", MilestonePay.DraftAmountProblem(10.005m, 3));
        Assert.Contains("larger", MilestonePay.DraftAmountProblem(MilestonePay.MaxAmount + 1, 1));
    }

    [Fact]
    public void Publishing_asks_every_milestone_to_pay_and_the_parts_to_be_the_whole()
    {
        Assert.Null(MilestonePay.PublishProblem(1000, [400m, 350m, 250m]));
        Assert.Contains("milestone 2 an amount", MilestonePay.PublishProblem(1000, [400m, null, 600m]));
        Assert.Contains("milestone 1 an amount", MilestonePay.PublishProblem(1000, [0m, 1000m]));
        var off = MilestonePay.PublishProblem(1000, [400m, 400m]);
        Assert.Contains("800.00", off);
        Assert.Contains("1,000.00", off);
    }

    // ---------------------------------------------------------------- state

    [Fact]
    public void Only_the_first_unpaid_milestone_is_open_and_the_rest_wait_their_turn()
    {
        var states = MilestonePay.States([Paid, None, None]);
        Assert.Equal([MilestonePayState.Paid, MilestonePayState.Open, MilestonePayState.Locked], states);
        Assert.Equal(1, MilestonePay.Current(states));
        Assert.False(MilestonePay.Complete(states));
    }

    [Fact]
    public void A_handed_in_milestone_moves_through_review_to_paid()
    {
        Assert.Equal(MilestonePayState.Submitted, MilestonePay.States([Handed])[0]);
        Assert.Equal(MilestonePayState.ChangesRequested, MilestonePay.States([Sent])[0]);
        Assert.Equal(MilestonePayState.Approved, MilestonePay.States([Approved])[0]);
        Assert.Equal(MilestonePayState.Paid, MilestonePay.States([Paid])[0]);
        // An approved milestone still unpaid keeps the next one locked.
        Assert.Equal(MilestonePayState.Locked, MilestonePay.States([Approved, None])[1]);
    }

    [Fact]
    public void Every_milestone_paid_completes_the_award()
    {
        var states = MilestonePay.States([Paid, Paid]);
        Assert.True(MilestonePay.Complete(states));
        Assert.Null(MilestonePay.Current(states));
        Assert.False(MilestonePay.Complete([]));
    }

    [Fact]
    public void Money_counts_what_is_paid_and_what_is_approved_and_owed()
    {
        var states = MilestonePay.States([Paid, Approved, None]);
        Assert.Equal((300m, 500m), MilestonePay.Money(states, [300m, 500m, 200m]));
    }

    [Theory]
    [InlineData(MilestonePayState.Locked, "locked")]
    [InlineData(MilestonePayState.Open, "open")]
    [InlineData(MilestonePayState.Submitted, "submitted")]
    [InlineData(MilestonePayState.ChangesRequested, "changes_requested")]
    [InlineData(MilestonePayState.Approved, "approved")]
    [InlineData(MilestonePayState.Paid, "paid")]
    public void Each_state_has_one_wire_name(MilestonePayState state, string name) =>
        Assert.Equal(name, MilestonePay.StateName(state));

    // --------------------------------------------------------------- claims

    [Fact]
    public void Only_the_open_milestone_may_be_handed_in_and_only_once_hired()
    {
        var states = MilestonePay.States([Paid, None, None]);
        Assert.Null(MilestonePay.ClaimProblem(OpportunityStatus.Awarded, states, 1));
        Assert.Contains("opens once milestone 2", MilestonePay.ClaimProblem(OpportunityStatus.Awarded, states, 2));
        Assert.Contains("already paid", MilestonePay.ClaimProblem(OpportunityStatus.Awarded, states, 0));
        Assert.Contains("no milestone 4", MilestonePay.ClaimProblem(OpportunityStatus.Awarded, states, 3));
        Assert.Contains("hired", MilestonePay.ClaimProblem(OpportunityStatus.Open, states, 1));
        Assert.Contains("cancelled", MilestonePay.ClaimProblem(OpportunityStatus.Cancelled, states, 1));
    }

    [Fact]
    public void A_milestone_is_handed_in_once_unless_the_client_sent_it_back()
    {
        Assert.Contains("waiting for the client", MilestonePay.ClaimProblem(OpportunityStatus.Awarded, MilestonePay.States([Handed]), 0));
        Assert.Null(MilestonePay.ClaimProblem(OpportunityStatus.Awarded, MilestonePay.States([Sent]), 0));
        Assert.Contains("approved", MilestonePay.ClaimProblem(OpportunityStatus.Awarded, MilestonePay.States([Approved]), 0));
        Assert.Contains("complete", MilestonePay.ClaimProblem(OpportunityStatus.Awarded, MilestonePay.States([Paid]), 0));
    }

    // ------------------------------------------------------- client's doors

    [Fact]
    public void The_client_approves_only_what_was_handed_in_and_not_sent_back()
    {
        Assert.Null(MilestonePay.ApproveProblem(MilestonePayState.Submitted));
        Assert.Contains("asked for changes", MilestonePay.ApproveProblem(MilestonePayState.ChangesRequested));
        Assert.Contains("already approved", MilestonePay.ApproveProblem(MilestonePayState.Approved));
        Assert.Contains("not been handed in", MilestonePay.ApproveProblem(MilestonePayState.Open));
    }

    [Fact]
    public void Sending_a_milestone_back_takes_a_sentence_the_freelancer_can_act_on()
    {
        Assert.Null(MilestonePay.ChangesProblem(MilestonePayState.Submitted, "The totals ignore the discount column."));
        Assert.Contains("Say what should change", MilestonePay.ChangesProblem(MilestonePayState.Submitted, " fix "));
        Assert.Contains("under", MilestonePay.ChangesProblem(MilestonePayState.Submitted, new string('x', Checkpoint.MaxChangesNote + 1)));
        Assert.Contains("already asked", MilestonePay.ChangesProblem(MilestonePayState.ChangesRequested, "Another round of fixes."));
        Assert.Contains("already approved", MilestonePay.ChangesProblem(MilestonePayState.Approved, "Too late for this one."));
    }

    [Fact]
    public void Marking_paid_approves_a_handed_in_milestone_but_never_one_sent_back()
    {
        Assert.Null(MilestonePay.PayProblem(MilestonePayState.Submitted));
        Assert.Null(MilestonePay.PayProblem(MilestonePayState.Approved));
        Assert.Contains("asked for changes", MilestonePay.PayProblem(MilestonePayState.ChangesRequested));
        Assert.Contains("already marked paid", MilestonePay.PayProblem(MilestonePayState.Paid));
        Assert.Contains("not been handed in", MilestonePay.PayProblem(MilestonePayState.Locked));
    }

    [Fact]
    public void Only_a_milestone_sent_back_is_handed_in_again_from_the_page()
    {
        Assert.Null(MilestonePay.ResubmitProblem(MilestonePayState.ChangesRequested));
        Assert.NotNull(MilestonePay.ResubmitProblem(MilestonePayState.Submitted));
    }

    [Fact]
    public void A_hired_job_is_cancelled_only_with_nothing_waiting_on_the_client()
    {
        Assert.Null(MilestonePay.CancelProblem(MilestonePay.States([Paid, None])));
        Assert.Null(MilestonePay.CancelProblem(MilestonePay.States([Paid, Sent])));
        Assert.Contains("waiting on you", MilestonePay.CancelProblem(MilestonePay.States([Paid, Handed])));
        Assert.Contains("waiting on you", MilestonePay.CancelProblem(MilestonePay.States([Approved, None])));
        Assert.Contains("nothing left", MilestonePay.CancelProblem(MilestonePay.States([Paid, Paid])));

        Assert.True(CancelRules.CanCancel(OpportunityStatus.Awarded, OpportunityKind.Milestones));
        Assert.False(CancelRules.CanCancel(OpportunityStatus.Awarded));
        Assert.Null(CancelRules.Problem(OpportunityStatus.Awarded, "The freelancer stopped answering.",
            OpportunityKind.Milestones, MilestonePay.States([Paid, None])));
        Assert.Contains("waiting on you", CancelRules.Problem(OpportunityStatus.Awarded, "The freelancer stopped answering.",
            OpportunityKind.Milestones, MilestonePay.States([Handed])));
        Assert.Contains("has a winner", CancelRules.Problem(OpportunityStatus.Awarded, "The freelancer stopped answering."));
    }

    // ----------------------------------------------------------- the hire

    [Fact]
    public void Paid_by_milestone_the_hire_can_be_taken_back_until_work_arrives()
    {
        var yes = ApplicationStatus.Selected;
        var no = ApplicationStatus.NotSelected;
        var review = ApplicationStatus.UnderReview;
        Assert.Null(ApplicationRules.DecisionProblem(review, yes, OpportunityStatus.Open, false, OpportunityKind.Milestones));
        Assert.Null(ApplicationRules.DecisionProblem(yes, no, OpportunityStatus.Awarded, false, OpportunityKind.Milestones));
        Assert.Contains("hire can no longer be taken back",
            ApplicationRules.DecisionProblem(yes, no, OpportunityStatus.Awarded, true, OpportunityKind.Milestones));
        Assert.Contains("Somebody is hired",
            ApplicationRules.DecisionProblem(review, yes, OpportunityStatus.Awarded, false, OpportunityKind.Milestones));
        Assert.Contains("Somebody is hired",
            ApplicationRules.DecisionProblem(review, no, OpportunityStatus.Awarded, false, OpportunityKind.Milestones));
        // A competitive winner is never taken back through the review box.
        Assert.Contains("no longer open", ApplicationRules.DecisionProblem(yes, no, OpportunityStatus.Awarded, false));
    }

    [Fact]
    public void The_hired_applicant_reads_hired_rather_than_won()
    {
        Assert.Equal("hired", ApplicationRules.Outcome(ApplicationStatus.Selected, OpportunityStatus.Awarded, won: true, hired: true));
        Assert.Equal("won", ApplicationRules.Outcome(ApplicationStatus.Selected, OpportunityStatus.Awarded, won: true));
    }

    // ------------------------------------------------------------ uploads

    [Fact]
    public void Paid_by_milestone_files_land_while_hired_and_stop_once_complete()
    {
        var past = T.AddDays(-1);
        Assert.Null(Delivery.UploadProblem(OpportunityStatus.Awarded, past, T, OpportunityKind.Milestones, complete: false));
        Assert.Contains("complete", Delivery.UploadProblem(OpportunityStatus.Awarded, past, T, OpportunityKind.Milestones, complete: true));
        Assert.Contains("hired", Delivery.UploadProblem(OpportunityStatus.Open, null, T, OpportunityKind.Milestones));
        // Competitive files still freeze at the winner.
        Assert.Contains("winner", Delivery.UploadProblem(OpportunityStatus.Awarded, past, T));
    }

    // ------------------------------------------------------------- the AI

    [Fact]
    public void The_milestone_draft_reads_the_total_only_when_paid_by_milestone()
    {
        var parts = AiFormReads.Of(AiFeature.MilestoneExtraction);
        Assert.True(parts.HasFlag(FormPart.Pay));
        OpportunityFormSnapshot Form(string? kind) => new(
            "A booking site", "Online booking for a clinic.", "web", null, null, null, "repository", false, null, null,
            kind, 900m);
        using var paid = JsonDocument.Parse(AiInputs.OpportunityForm(Form("milestones"), parts));
        Assert.Equal(900m, paid.RootElement.GetProperty("payment").GetProperty("totalBudgetUsd").GetDecimal());
        // A competitive form reads exactly as before: no award, no payment.
        Assert.DoesNotContain("payment", AiInputs.OpportunityForm(Form(null), parts));
        Assert.DoesNotContain("900", AiInputs.OpportunityForm(Form("competitive"), parts));
    }

    [Fact]
    public void A_drafted_amount_is_kept_where_usable_and_left_out_otherwise()
    {
        var canonical = AiOutputs.Validate(AiFeature.MilestoneExtraction,
            """{"milestones":[{"title":"Ordering","description":null,"amount":700.004},{"title":"Board","amount":-5},{"title":"SMS","amount":"lots"}]}""",
            out var error);
        Assert.Null(error);
        using var doc = JsonDocument.Parse(canonical!);
        var items = doc.RootElement.GetProperty("milestones");
        Assert.Equal(700.00m, items[0].GetProperty("amount").GetDecimal());
        Assert.False(items[1].TryGetProperty("amount", out _));
        Assert.False(items[2].TryGetProperty("amount", out _));
    }
}
