using WinnersPortal.Domain;
using WinnersPortal.Services.Preview;
using Xunit;

namespace WinnersPortal.Api.Tests;

/// <summary>
/// The ticket the browser carries from the portal to a preview, and the
/// rules of who may run what. The fixed vector here is also in the agent's
/// self-test (preview-agent.py --self-test): the two sides compute the same
/// bytes or no preview opens.
/// </summary>
public class PreviewTicketsTests
{
    private const string Token = "agent-token-123456";
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_789_999_900);

    [Fact]
    public void The_ticket_is_the_same_bytes_the_agent_computes()
    {
        Assert.Equal(
            "p-1a2b3c4d.1790000000.00112233445566778899aabbccddeeff.5054f3f01b0c8b2363bacd3c6a9b76a1d88ab9d8738aea3fce4df5e3f273c28e",
            PreviewTickets.Mint(Token, "p-1a2b3c4d", 1_790_000_000, "00112233445566778899aabbccddeeff"));
    }

    [Fact]
    public void A_fresh_ticket_names_its_preview_and_lapses_in_two_minutes()
    {
        var ticket = PreviewTickets.Mint(Token, "p-1a2b3c4d", Now);
        var read = PreviewTickets.Read(Token, ticket, Now);
        Assert.NotNull(read);
        Assert.Equal("p-1a2b3c4d", read!.Value.Label);
        Assert.Equal(Now.AddMinutes(2).ToUnixTimeSeconds(), read.Value.Expires);
        Assert.Matches("^[0-9a-f]{32}$", read.Value.Nonce);

        Assert.Null(PreviewTickets.Read(Token, ticket, Now.AddMinutes(2).AddSeconds(1))); // lapsed
        Assert.NotEqual(ticket, PreviewTickets.Mint(Token, "p-1a2b3c4d", Now)); // a new nonce each time
    }

    [Theory]
    [InlineData("other-agent-token-99")] // another host's token
    public void A_ticket_signed_with_another_token_is_refused(string other) =>
        Assert.Null(PreviewTickets.Read(other, PreviewTickets.Mint(Token, "p-1a2b3c4d", Now), Now));

    [Fact]
    public void A_ticket_changed_in_any_part_is_refused()
    {
        var ticket = PreviewTickets.Mint(Token, "p-1a2b3c4d", 1_790_000_000, "00112233445566778899aabbccddeeff");
        var parts = ticket.Split('.');
        Assert.Null(PreviewTickets.Read(Token, string.Join('.', "p-99999999", parts[1], parts[2], parts[3]), Now)); // another preview
        Assert.Null(PreviewTickets.Read(Token, string.Join('.', parts[0], "1799999999", parts[2], parts[3]), Now)); // a longer life
        Assert.Null(PreviewTickets.Read(Token, string.Join('.', parts[0], parts[1], "ff112233445566778899aabbccddeeff", parts[3]), Now));
        Assert.Null(PreviewTickets.Read(Token, "not a ticket", Now));
        Assert.Null(PreviewTickets.Read(Token, null, Now));
    }

    [Fact]
    public void The_ticket_rides_in_the_query_of_the_previews_own_address()
    {
        var url = PreviewTickets.Url(new Uri("https://p-1a2b3c4d.previews.example.net/"), "p-1a2b3c4d.1.ab.cd");
        Assert.Equal("https://p-1a2b3c4d.previews.example.net/?wp_ticket=p-1a2b3c4d.1.ab.cd", url.ToString());
    }

    // ------------------------------------------------------------ the rules

    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, true, true)]
    [InlineData(false, false, false, false)]
    public void The_entrant_the_client_and_an_administrator_may_run_a_preview(bool entrant, bool owner, bool admin, bool may) =>
        Assert.Equal(may, Previews.CanRun(entrant, owner, admin));

    [Fact]
    public void The_final_runs_once_the_entry_is_frozen_on_an_opportunity_that_asked_for_compose()
    {
        Assert.Null(Previews.FinalProblem(OpportunityStatus.Reviewing, true, true, EntryStatus.Active));
        Assert.Null(Previews.FinalProblem(OpportunityStatus.Awarded, true, true, EntryStatus.Active));
        Assert.Contains("deadline", Previews.FinalProblem(OpportunityStatus.Open, true, false, EntryStatus.Active));
        Assert.Contains("deadline", Previews.FinalProblem(OpportunityStatus.Reviewing, true, false, EntryStatus.Active)); // not frozen yet
        Assert.Contains("Docker Compose", Previews.FinalProblem(OpportunityStatus.Reviewing, false, true, EntryStatus.Active));
        Assert.Contains("no longer", Previews.FinalProblem(OpportunityStatus.Reviewing, true, true, EntryStatus.Removed));
    }

    [Fact]
    public void The_states_and_the_stops_have_words()
    {
        Assert.Equal("running", Previews.StatusName(PreviewStatus.Running));
        Assert.Equal("none", Previews.StatusName(PreviewStatus.None));
        Assert.Equal("Stopped after 30 minutes without a visit.", Previews.StopReasonText("idle", 30));
        Assert.Equal("Stopped.", Previews.StopReasonText(null, 30));
        Assert.Equal("1 preview is already running on the build host — stop one, or wait for one to stop itself.", Previews.MaxRunningText(1));
        Assert.StartsWith("3 previews are", Previews.MaxRunningText(3));
        Assert.Contains("20 minutes", Previews.StartTimedOut(TimeSpan.FromMinutes(15)));
    }
}
