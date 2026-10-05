using WinnersPortal.Api.Opportunities;
using WinnersPortal.Services.Opportunities;
using WinnersPortal.Domain;
using Xunit;

namespace WinnersPortal.Api.Tests;

/// <summary>
/// The rules of the one exit that is not a winner. Pure — the same checks the
/// endpoint enforces, held still here so a lifecycle change has to argue.
/// </summary>
public class CancelTests
{
    [Theory]
    [InlineData(OpportunityStatus.Draft, false)]     // never public — nothing promised
    [InlineData(OpportunityStatus.Open, true)]
    [InlineData(OpportunityStatus.Reviewing, true)]
    [InlineData(OpportunityStatus.Awarded, false)]   // the award is the record now
    [InlineData(OpportunityStatus.Cancelled, false)]
    public void Only_live_opportunities_can_be_cancelled(OpportunityStatus status, bool can) =>
        Assert.Equal(can, CancelRules.CanCancel(status));

    [Fact]
    public void Every_status_has_a_cancellation_decision()
    {
        // A sixth status must decide whether it is cancellable — this fails
        // until CanCancel and Problem both know the answer.
        Assert.Equal(5, Enum.GetValues<OpportunityStatus>().Length);
        foreach (var status in Enum.GetValues<OpportunityStatus>())
        {
            var problem = CancelRules.Problem(status, "a perfectly adequate reason");
            Assert.Equal(CancelRules.CanCancel(status), problem is null);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("too short")] // nine characters — one under the floor
    public void A_cancellation_without_a_real_reason_is_refused(string? reason) =>
        Assert.NotNull(CancelRules.Problem(OpportunityStatus.Open, reason));

    [Fact]
    public void The_reason_has_a_floor_and_a_ceiling()
    {
        Assert.Null(CancelRules.Problem(OpportunityStatus.Open, new string('x', CancelRules.MinReason)));
        Assert.Null(CancelRules.Problem(OpportunityStatus.Open, new string('x', CancelRules.MaxReason)));
        Assert.NotNull(CancelRules.Problem(OpportunityStatus.Open, new string('x', CancelRules.MaxReason + 1)));
    }

    [Fact]
    public void The_stored_reason_is_trimmed_and_padding_does_not_beat_the_floor()
    {
        Assert.Equal("changed direction entirely",
            CancelRules.CleanReason("  changed direction entirely  "));
        // Nine characters dressed up in whitespace is still nine characters.
        Assert.Null(CancelRules.CleanReason("   too short   "));
    }
}
