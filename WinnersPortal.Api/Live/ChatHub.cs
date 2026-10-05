using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using WinnersPortal.Services.Auth;
using WinnersPortal.Services.Chat;

namespace WinnersPortal.Api.Live;

/// <summary>
/// The socket end of the conversations. Signed-in only: the cookie (or, for
/// a socket that cannot set a header, the access_token query string) names
/// the member, and every connection of theirs joins their own room on
/// arrival — there is nothing to call. A nudge to that room says only
/// "a conversation of yours changed"; the dock and the Messages page
/// refetch through the same API they already trust. Group membership dies
/// with the connection.
/// </summary>
[Authorize]
public sealed class ChatHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        if (Principal.UserId(Context.User!) is { } me)
            await Groups.AddToGroupAsync(Context.ConnectionId, ChatRules.UserGroup(me), Context.ConnectionAborted);
        await base.OnConnectedAsync();
    }
}
