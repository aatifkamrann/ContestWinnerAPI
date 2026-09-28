using StackExchange.Redis;

namespace WinnersPortal.Services.Settings;

/// <summary>
/// Optional Redis connection, opened as the process starts from the choice
/// <see cref="RedisSettings"/> read before the host was built. Off — the
/// default — the portal still runs, and everything Redis would carry stays
/// in this process: settings invalidation, the public lists' clears, the
/// workers' wakes and the live board's groups.
/// </summary>
public sealed class RedisConnection : IDisposable
{
    public IConnectionMultiplexer? Muxer { get; }

    /// <summary>What this process was started with, for the restart-pending check against what is saved now.</summary>
    public RedisSettings.Choice Bound { get; }

    public RedisConnection(RedisSettings.Choice choice, ILogger<RedisConnection> log)
    {
        Bound = choice;
        // Off means no connection at all. A localhost:6379 default once stood
        // here and served nobody; because AbortOnConnectFail is false below,
        // Connect() then succeeded against nothing, and the failure surfaced
        // much later as a command timing out in the backlog.
        if (choice.Active is not { } url)
        {
            if (choice.OnButUnusable)
                log.LogWarning("Redis is switched on but its address is missing or unreadable ({Problem}); it is off for this process.",
                    RedisSettings.Problem(RedisSettings.UrlKey, choice.Url) ?? "no address");
            return;
        }

        try
        {
            var options = ConfigurationOptions.Parse(url);
            options.AbortOnConnectFail = false; // reconnects in the background
            options.ConnectTimeout = 2000;
            Muxer = ConnectionMultiplexer.Connect(options);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex,
                "Redis unavailable at {Endpoints}; what it would carry stays in this process.", choice.Endpoints);
        }
    }

    public void Dispose() => Muxer?.Dispose();
}
