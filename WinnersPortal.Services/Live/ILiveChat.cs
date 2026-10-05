namespace WinnersPortal.Services.Live;

/// <summary>
/// The conversation's doorbell, the board's twin: one nudge to a member's
/// room after the save that made it true, and the dock or the Messages
/// page they have open refetches. The web edge implements it over SignalR;
/// the service here never sees a socket.
/// </summary>
public interface ILiveChat
{
    Task ChatChangedAsync(Guid userId, Guid entryId, CancellationToken ct = default);
}
