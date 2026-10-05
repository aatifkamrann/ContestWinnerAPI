using WinnersPortal.Domain;
using WinnersPortal.Services.Live;
using Xunit;

namespace WinnersPortal.Api.Tests;

public class LiveRulesTests
{
    // The group name is the contract between every nudge call site and every
    // connected page — normalisation drift would split one room into two.
    [Theory]
    [InlineData("build-a-parser", "opportunity:build-a-parser")]
    [InlineData("Build-A-Parser", "opportunity:build-a-parser")]
    [InlineData("  build-a-parser  ", "opportunity:build-a-parser")]
    public void Opportunity_group_is_normalised(string slug, string expected)
        => Assert.Equal(expected, LiveRules.OpportunityGroup(slug));

    [Fact]
    public void Same_slug_different_casing_lands_in_one_room()
        => Assert.Equal(LiveRules.OpportunityGroup("MyOpportunity"), LiveRules.OpportunityGroup("myopportunity"));

    // Drafts are the author's alone — a socket on a guessed draft slug would
    // announce that it exists and when it is edited. Everything published
    // stays watchable, because published pages keep changing (a review flip,
    // a winner banner, a rating) long after entry closes.
    [Theory]
    [InlineData(OpportunityStatus.Draft, false)]
    [InlineData(OpportunityStatus.Open, true)]
    [InlineData(OpportunityStatus.Reviewing, true)]
    [InlineData(OpportunityStatus.Awarded, true)]
    [InlineData(OpportunityStatus.Cancelled, true)]
    public void Only_drafts_are_unwatchable(OpportunityStatus status, bool watchable)
        => Assert.Equal(watchable, LiveRules.CanWatch(status));

    [Fact]
    public void Every_status_has_a_watch_decision()
    {
        // A new status must make a deliberate choice here, not inherit one.
        foreach (var status in Enum.GetValues<OpportunityStatus>())
            _ = LiveRules.CanWatch(status);
        Assert.Equal(5, Enum.GetValues<OpportunityStatus>().Length);
    }

    [Fact]
    public void Event_name_is_the_wire_contract()
        // The web client subscribes to this exact string; renaming it strands
        // every open tab silently — no error, just a board that stopped moving.
        => Assert.Equal("opportunityChanged", LiveRules.OpportunityChanged);
}
