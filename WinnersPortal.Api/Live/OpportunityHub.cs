using Microsoft.AspNetCore.SignalR;
using WinnersPortal.Services.Live;

namespace WinnersPortal.Api.Live;

/// <summary>
/// The socket end of the live board. Anonymous on purpose — the opportunity page
/// is public, and nothing sent over the hub says more than the page itself.
/// A connection calls <see cref="Watch"/> once per page; the group membership
/// dies with the connection, so there is no Unwatch to forget.
/// </summary>
public sealed class OpportunityHub(LiveOpportunities opportunities) : Hub
{
    public async Task Watch(string slug)
    {
        if (!await opportunities.WatchableAsync(slug, Context.ConnectionAborted))
            throw new HubException("This opportunity is not watchable.");

        await Groups.AddToGroupAsync(
            Context.ConnectionId, LiveRules.OpportunityGroup(slug), Context.ConnectionAborted);
    }
}
