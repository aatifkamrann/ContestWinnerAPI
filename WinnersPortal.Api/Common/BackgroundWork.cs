using WinnersPortal.Services.Ai;
using WinnersPortal.Services.Identity;
using WinnersPortal.Services.Common;
using WinnersPortal.Services.Email;
using WinnersPortal.Services.GitHub;
using WinnersPortal.Services.Leaderboard;
using WinnersPortal.Services.Notifications;
using WinnersPortal.Services.Preview;
using WinnersPortal.Services.Settings;

namespace WinnersPortal.Api.Common;

/// <summary>
/// The workers that act on what the database holds pending — GitHub
/// provisioning and the deadline sweep, the email and push outboxes, AI
/// drafting, the daily merit snapshot — and whether this process runs them.
/// </summary>
/// <remarks>
/// They do not coordinate, so where there is more than one API process
/// exactly one runs them and the others start with <c>WORKERS=false</c>;
/// see <see cref="WorkRelay"/> for how a wake reaches the one that does.
/// The activity log's writer and the slow-query watcher are not among
/// them: each serves only the process it runs in, and every process keeps
/// its own.
/// </remarks>
public static class BackgroundWork
{
    /// <summary>
    /// Unset or <c>true</c>: this process runs the workers. <c>false</c>: it
    /// runs none. Anything else stops the start, naming the variable.
    /// </summary>
    public static bool RunsHere(IConfiguration config) => config.GetValue(WorkRelay.Switch, true);

    /// <summary>The relay, and, where this process runs them, the workers, in the order they start.</summary>
    public static IServiceCollection AddBackgroundWork(this IServiceCollection services, bool here)
    {
        services.AddSingleton(sp => new WorkRelay(here,
            sp.GetRequiredService<RedisConnection>(), sp.GetRequiredService<ILogger<WorkRelay>>()));
        if (!here) return services;
        services.AddHostedService<GitHubWorker>();
        services.AddHostedService<EmailWorker>();
        services.AddHostedService<PushWorker>();
        services.AddHostedService<AiWorker>();
        services.AddHostedService<IdentityProofWorker>();
        services.AddHostedService<PreviewWorker>();
        services.AddHostedService<MeritSnapshotWorker>();
        return services;
    }

    /// <summary>The start's one line on it, so a log says which process sends the email.</summary>
    public static void Announce(ILogger log, bool here, bool redis)
    {
        if (here)
            log.LogInformation("Background workers: running in this process.");
        else if (redis)
            log.LogInformation(
                "Background workers: off in this process ({Switch}=false); its wakes go over Redis to the API process that runs them.",
                WorkRelay.Switch);
        else
            log.LogWarning(
                "Background workers: off in this process ({Switch}=false), and with no Redis its wakes reach nobody: "
                + "what it queues waits for the next sweep of the API process that runs them, up to 30 seconds.",
                WorkRelay.Switch);
    }
}
