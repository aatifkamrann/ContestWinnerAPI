namespace WinnersPortal.Services.Live;

/// <summary>
/// What the rest of the portal holds instead of a hub: one nudge, sent after
/// the save that made it true. The web edge implements it over SignalR; the
/// workers and services here never see a socket.
/// </summary>
public interface ILiveBoard
{
    Task OpportunityChangedAsync(string slug, CancellationToken ct = default);
}
