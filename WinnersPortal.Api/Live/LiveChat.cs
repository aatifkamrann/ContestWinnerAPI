using Microsoft.AspNetCore.SignalR;
using WinnersPortal.Services.Chat;
using WinnersPortal.Services.Live;

namespace WinnersPortal.Api.Live;

/// <summary>
/// What the chat service holds instead of a hub context: one nudge to a
/// member's room after the save that made it true, with the entry it is
/// about so an open conversation can tell whether it was the one. Rung and
/// not waited for, for the board's reason (<see cref="LiveBoard"/>): a
/// Redis that is down must not hold up the send, and a failed nudge is a
/// log line — the next poll brings the line anyway.
/// </summary>
public sealed class LiveChat(IHubContext<ChatHub> hub, ILogger<LiveChat> log) : ILiveChat
{
    public Task ChatChangedAsync(Guid userId, Guid entryId, CancellationToken ct = default)
    {
        _ = RingAsync(userId, entryId);
        return Task.CompletedTask;
    }

    private async Task RingAsync(Guid userId, Guid entryId)
    {
        try
        {
            await hub.Clients.Group(ChatRules.UserGroup(userId))
                .SendAsync(ChatRules.ChatChanged, entryId, CancellationToken.None);
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Live nudge for a conversation of member {UserId} failed; their pages refresh by themselves.", userId);
        }
    }
}
