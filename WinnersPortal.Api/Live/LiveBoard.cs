using Microsoft.AspNetCore.SignalR;
using WinnersPortal.Services.Live;

namespace WinnersPortal.Api.Live;

/// <summary>
/// What the rest of the API holds instead of a hub context: one method,
/// called after the save that made it true. A nudge is advisory — the page
/// it wakes refetches everything — so a failed broadcast is a log line,
/// never a failed request or a broken worker sweep.
/// </summary>
/// <remarks>
/// Rung and not waited for. With the Redis backplane a broadcast is a
/// publish, and a Redis that is down makes a publish wait out its timeout —
/// seconds on every write that nudges, and on every opportunity a worker sweep
/// touches, for a doorbell the page can do without. The save has already
/// committed; the caller goes on, and a failure is still the log line.
/// </remarks>
public sealed class LiveBoard(IHubContext<OpportunityHub> hub, ILogger<LiveBoard> log) : ILiveBoard
{
    public Task OpportunityChangedAsync(string slug, CancellationToken ct = default)
    {
        _ = RingAsync(slug);
        return Task.CompletedTask;
    }

    // Under no caller's token: the request that rang may be over before
    // the bell is, and that is no reason to take the bell back.
    private async Task RingAsync(string slug)
    {
        try
        {
            await hub.Clients.Group(LiveRules.OpportunityGroup(slug))
                .SendAsync(LiveRules.OpportunityChanged, slug, CancellationToken.None);
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Live nudge for opportunity '{Slug}' failed; watchers refresh by hand.", slug);
        }
    }
}
