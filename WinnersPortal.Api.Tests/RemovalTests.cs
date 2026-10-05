using WinnersPortal.Services.Ai;
using WinnersPortal.Services.Email;
using Microsoft.EntityFrameworkCore;
using WinnersPortal.Services.Activity;
using WinnersPortal.Services.Opportunities;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Domain;
using Xunit;

namespace WinnersPortal.Api.Tests;

/// <summary>
/// Taking an entrant off an opportunity. Every rule here exists because the
/// action costs one person real work, so each one is pinned: the reason
/// they receive, the states where it may happen at all, the window they get
/// to clone what they built, and the door that stays shut afterwards.
/// </summary>
public class RemovalTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-04T12:00:00Z");

    [Fact]
    public void A_reason_short_enough_to_be_no_reason_is_refused()
    {
        Assert.False(Removal.CleanReason(null).Ok);
        Assert.False(Removal.CleanReason("").Ok);
        Assert.False(Removal.CleanReason("   ").Ok);
        Assert.False(Removal.CleanReason("spam").Ok);
        // Whitespace does not pad a reason into existence.
        Assert.False(Removal.CleanReason("  spam      ").Ok);
    }

    [Fact]
    public void A_real_reason_is_trimmed_and_capped()
    {
        var (ok, reason) = Removal.CleanReason("  Entry is a copy of another entrant's repository.  ");
        Assert.True(ok);
        Assert.Equal("Entry is a copy of another entrant's repository.", reason);

        var (longOk, capped) = Removal.CleanReason(new string('x', Removal.MaxReason + 200));
        Assert.True(longOk);
        Assert.Equal(Removal.MaxReason, capped!.Length);
    }

    [Fact]
    public void The_winner_can_never_be_removed()
    {
        // An announced award is a public promise. Undoing it by deleting the
        // person it was made to is the one thing this must not become.
        foreach (var status in Enum.GetValues<OpportunityStatus>())
            Assert.Contains("won the opportunity",
                Removal.Problem(status, EntryStatus.Active, isWinner: true, byAdmin: false)!);
    }

    [Theory]
    [InlineData(OpportunityStatus.Open)]
    [InlineData(OpportunityStatus.Reviewing)]
    public void A_live_opportunity_may_lose_an_active_entrant(OpportunityStatus status) =>
        Assert.Null(Removal.Problem(status, EntryStatus.Active, isWinner: false, byAdmin: false));

    [Fact]
    public void A_decided_opportunity_keeps_its_record()
    {
        Assert.Contains("record", Removal.Problem(
            OpportunityStatus.Awarded, EntryStatus.Active, isWinner: false, byAdmin: false)!);
        Assert.Contains("already void", Removal.Problem(
            OpportunityStatus.Cancelled, EntryStatus.Active, isWinner: false, byAdmin: false)!);
        Assert.Contains("no entrants", Removal.Problem(
            OpportunityStatus.Draft, EntryStatus.Active, isWinner: false, byAdmin: false)!);
    }

    [Fact]
    public void Somebody_who_already_left_is_not_removed_again()
    {
        Assert.Contains("already withdrew", Removal.Problem(
            OpportunityStatus.Open, EntryStatus.Withdrawn, isWinner: false, byAdmin: false)!);
        Assert.Contains("already been removed", Removal.Problem(
            OpportunityStatus.Open, EntryStatus.Removed, isWinner: false, byAdmin: false)!);
    }

    [Fact]
    public void The_clone_window_opens_at_the_removal_and_the_sweep_waits_for_it()
    {
        var ends = Removal.AccessEndsAt(Now);
        Assert.Equal(Now.AddDays(Removal.CloneGraceDays), ends);

        // Not yet: the entrant is still inside the window the email promised.
        Assert.False(Removal.AccessDue(ends, null, Now));
        Assert.False(Removal.AccessDue(ends, null, ends.AddSeconds(-1)));
        // Due, and then done — the sweep runs every cycle and must not keep
        // calling GitHub about somebody it already took off.
        Assert.True(Removal.AccessDue(ends, null, ends));
        Assert.False(Removal.AccessDue(ends, ends, ends.AddDays(1)));
        // An entry that was never removed has no window and is never due.
        Assert.False(Removal.AccessDue(null, null, Now));
    }

    [Fact]
    public void A_removed_entrant_cannot_simply_enter_again()
    {
        // The database's partial unique index only guards active rows, so
        // without this a removal would last exactly one click.
        Assert.NotNull(Removal.ReentryProblem(wasRemoved: true));
        Assert.Contains("removed from this opportunity", Removal.ReentryProblem(true)!);
        Assert.Null(Removal.ReentryProblem(wasRemoved: false));
    }

    [Fact]
    public void The_entrant_is_told_which_of_the_two_decisions_this_was()
    {
        var byClient = Emails.EntryRemoved(
            "Inventory dashboard", "inventory", "Duplicate of another entry.", 7, "org/entry-1", byAdmin: false);
        var byAdmin = Emails.EntryRemoved(
            "Inventory dashboard", "inventory", "Duplicate of another entry.", 7, "org/entry-1", byAdmin: true);

        Assert.Contains("The client removed your entry", byClient.TextBody);
        Assert.Contains("A portal administrator removed your entry", byAdmin.TextBody);
        // …and the closing line does not call an administrator's decision
        // the client's, which is the sort of slip that reads as a form letter.
        Assert.Contains("one client's decision", byClient.TextBody);
        Assert.DoesNotContain("one client's decision", byAdmin.TextBody);
        // The reason travels word for word, in both.
        Assert.Contains("Duplicate of another entry.", byClient.TextBody);
        Assert.Contains("Duplicate of another entry.", byAdmin.TextBody);
        // And the window, and where the work stands.
        Assert.Contains("7 days", byClient.TextBody);
        Assert.Contains("org/entry-1", byClient.TextBody);
        Assert.Contains("never deleted", byClient.TextBody);
        // …and no promise of a channel the portal does not have: it sends
        // from a no-reply address unless an operator changed it.
        Assert.DoesNotContain("reply to this email", byClient.TextBody);
    }

    [Fact]
    public void A_withdrawal_reason_holds_as_much_as_a_removal_reason_and_the_log_keeps_the_same()
    {
        // The model, not a database: EF builds it from AppDbContext alone.
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=nowhere;Database=none;Username=x;Password=x").Options);
        var entry = db.Model.FindEntityType(typeof(Entry))!;

        Assert.Equal(Removal.MaxReason, entry.FindProperty(nameof(Entry.WithdrawnReason))!.GetMaxLength());
        Assert.Equal(Removal.MaxReason, entry.FindProperty(nameof(Entry.RemovedReason))!.GetMaxLength());
        // The same cleaning serves the entry and the activity row, so the
        // client and an administrator read the same words.
        Assert.Equal(Removal.MaxReason, ActivityNames.MaxDetail);
    }

    [Fact]
    public void An_entry_with_no_repository_yet_is_not_told_to_clone_one()
    {
        var mail = Emails.EntryRemoved(
            "Inventory dashboard", "inventory", "Duplicate of another entry.", 7, null, byAdmin: false);

        Assert.Contains("nothing to collect", mail.TextBody);
        Assert.DoesNotContain("clone it", mail.TextBody);
    }
}
