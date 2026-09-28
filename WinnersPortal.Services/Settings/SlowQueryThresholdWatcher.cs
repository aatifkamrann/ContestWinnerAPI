using WinnersPortal.Infrastructure.Data;

namespace WinnersPortal.Services.Settings;

/// <summary>
/// Carries the <c>limits.slowQueryMs</c> setting, and the
/// <c>limits.slowQueryBackground</c> switch beside it, to the slow-query
/// interceptor, so a value saved on Settings → Limits &amp; maintenance
/// is in force within seconds, with no restart. A poll rather than a read
/// in the interceptor: the interceptor runs inside every command, and a
/// settings read after a save is itself a command. Between saves the read
/// is the settings cache, a dictionary lookup.
/// </summary>
public sealed class SlowQueryThresholdWatcher(
    SettingsService settings, SlowQueryStats stats, ILogger<SlowQueryThresholdWatcher> log) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                var ms = SlowQueryStats.ParseMs(await settings.GetAsync(SlowQueryStats.SettingKey, stoppingToken))
                    ?? SlowQueryStats.DefaultMs;
                if (stats.SetThreshold(ms))
                    log.LogInformation(ms == 0
                        ? "Slow-query log is off."
                        : "Slow-query threshold is now {ThresholdMs} ms; the tally starts again.", ms);

                var background = SlowQueryStats.ParseIncludesBackground(
                    await settings.GetAsync(SlowQueryStats.BackgroundSettingKey, stoppingToken));
                if (stats.SetIncludesBackground(background))
                    log.LogInformation(background
                        ? "Slow-query log now counts background work; the tally starts again."
                        : "Slow-query log now leaves background work out; the tally starts again.");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A database that is briefly unreachable keeps what is
                // already in force; the next tick tries again.
                log.LogDebug(ex, "Could not read the slow-query settings; keeping {ThresholdMs} ms.", stats.ThresholdMs);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
