using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WinnersPortal.Api.Common;
using WinnersPortal.Services.Ai;
using WinnersPortal.Services.Common;
using WinnersPortal.Services.Email;
using WinnersPortal.Services.GitHub;
using WinnersPortal.Services.Identity;
using WinnersPortal.Services.Preview;
using WinnersPortal.Services.Leaderboard;
using WinnersPortal.Services.Notifications;
using WinnersPortal.Services.Settings;
using Xunit;

namespace WinnersPortal.Api.Tests;

/// <summary>
/// The background workers held to one API process (Api/Common/BackgroundWork.cs)
/// and the wakes that reach it (Services/Common/WorkSignal.cs): which process
/// registers the workers, a wake here and a wake heard from another process,
/// a Redis that is down costing a request nothing, and a wake nobody heard
/// being said, but not over and over.
/// </summary>
public class WorkRelayTests
{
    private static readonly TimeSpan Soon = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(200);

    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Capture : ILogger<WorkRelay>
    {
        public List<(LogLevel Level, string Text)> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Lines.Add((logLevel, formatter(state, exception)));
    }

    /// <summary>How long a worker waiting on the signal waits before it would sweep anyway.</summary>
    private static async Task<TimeSpan> WaitedAsync(WorkSignal signal, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        await signal.WaitAsync(timeout, CancellationToken.None);
        return clock.Elapsed;
    }

    private static IConfiguration Config(string? workers) => new ConfigurationBuilder()
        .AddInMemoryCollection(workers is null
            ? new Dictionary<string, string?>()
            : new Dictionary<string, string?> { [WorkRelay.Switch] = workers })
        .Build();

    // ------------------------------------------------ which process runs them

    [Theory]
    [InlineData(null, true)]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("false", false)]
    [InlineData("FALSE", false)]
    public void Unset_or_true_runs_the_workers_here_and_false_runs_none(string? value, bool expected) =>
        Assert.Equal(expected, BackgroundWork.RunsHere(Config(value)));

    [Fact]
    public void A_switch_that_is_not_true_or_false_stops_the_start_naming_it()
    {
        var e = Assert.Throws<InvalidOperationException>(() => BackgroundWork.RunsHere(Config("off")));
        Assert.Contains(WorkRelay.Switch, e.Message);
    }

    private static List<Type> HostedIn(IServiceCollection services) => services
        .Where(d => d.ServiceType == typeof(IHostedService))
        .Select(d => d.ImplementationType!)
        .ToList();

    [Fact]
    public void The_process_that_runs_the_workers_registers_all_seven_in_their_order()
    {
        var services = new ServiceCollection().AddBackgroundWork(true);
        Assert.Equal(
            [typeof(GitHubWorker), typeof(EmailWorker), typeof(PushWorker), typeof(AiWorker), typeof(IdentityProofWorker), typeof(PreviewWorker), typeof(MeritSnapshotWorker)],
            HostedIn(services));
        Assert.Contains(services, d => d.ServiceType == typeof(WorkRelay));
    }

    [Fact]
    public void A_process_with_workers_off_registers_none_but_keeps_the_relay_its_wakes_go_through()
    {
        var services = new ServiceCollection().AddBackgroundWork(false);
        Assert.Empty(HostedIn(services));
        Assert.Contains(services, d => d.ServiceType == typeof(WorkRelay));
    }

    // ------------------------------------------------------ a wake, here

    [Fact]
    public void Each_worker_has_its_own_name_on_the_channel()
    {
        var relay = new WorkRelay(true);
        Assert.Equal(
            ["email", "github", "push", "ai", "preview"],
            new WorkSignal[] { new EmailWorkSignal(relay), new GitHubWorkSignal(relay), new PushWorkSignal(relay), new AiWorkSignal(relay), new PreviewWorkSignal(relay) }
                .Select(s => s.Name));
    }

    [Fact]
    public async Task A_wake_in_the_process_that_runs_the_workers_wakes_its_own_at_once()
    {
        var email = new EmailWorkSignal(new WorkRelay(true));
        email.Wake();
        Assert.True(await WaitedAsync(email, Soon) < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task A_signal_made_without_a_relay_still_wakes_its_own_worker()
    {
        var push = new PushWorkSignal();
        push.Wake();
        Assert.True(await WaitedAsync(push, Soon) < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Wakes_that_find_the_worker_already_woken_come_to_one_sweep()
    {
        var ai = new AiWorkSignal(new WorkRelay(true));
        ai.Wake();
        ai.Wake();
        ai.Wake();
        Assert.True(await WaitedAsync(ai, Soon) < TimeSpan.FromSeconds(1));
        // The rest were one sweep's worth: the next wait is the timer's.
        Assert.True(await WaitedAsync(ai, Short) >= Short - TimeSpan.FromMilliseconds(50));
    }

    // ------------------------------------------- a wake from another process

    [Fact]
    public async Task A_wake_heard_from_another_process_wakes_the_named_worker_and_no_other()
    {
        var relay = new WorkRelay(true);
        var email = new EmailWorkSignal(relay);
        var github = new GitHubWorkSignal(relay);

        relay.Heard("github");

        Assert.True(await WaitedAsync(github, Soon) < TimeSpan.FromSeconds(1));
        Assert.True(await WaitedAsync(email, Short) >= Short - TimeSpan.FromMilliseconds(50));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nobody")]
    [InlineData("EMAIL")]
    public async Task A_name_that_is_not_a_worker_here_wakes_nothing(string? name)
    {
        var relay = new WorkRelay(true);
        var email = new EmailWorkSignal(relay);
        relay.Heard(name);
        Assert.True(await WaitedAsync(email, Short) >= Short - TimeSpan.FromMilliseconds(50));
    }

    [Fact]
    public void A_process_with_workers_off_and_a_Redis_that_is_down_wakes_without_waiting()
    {
        // Nothing listens on port 1. Connecting is RedisConnection's own
        // business; what is measured is that the wake adds nothing to the
        // request that queued the work, however long the publish takes to fail.
        using var redis = new RedisConnection(new RedisSettings.Choice(true, "127.0.0.1:1", "test"), NullLogger<RedisConnection>.Instance);
        Assert.NotNull(redis.Muxer);
        var relay = new WorkRelay(false, redis, NullLogger<WorkRelay>.Instance);
        var email = new EmailWorkSignal(relay);

        var clock = Stopwatch.StartNew();
        for (var i = 0; i < 20; i++) email.Wake();
        clock.Stop();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"took {clock.Elapsed}");
    }

    // ---------------------------------------------- a wake nobody heard

    [Fact]
    public void A_wake_nobody_heard_is_a_warning_and_the_next_few_minutes_are_quiet()
    {
        var clock = new Clock(new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero));
        var log = new Capture();
        var relay = new WorkRelay(false, null, log, clock);

        relay.Delivered("email", Task.FromResult(0L));
        relay.Delivered("github", Task.FromResult(0L));
        clock.Now += WorkRelay.UnheardQuiet - TimeSpan.FromSeconds(1);
        relay.Delivered("email", Task.FromResult(0L));

        var (level, text) = Assert.Single(log.Lines);
        Assert.Equal(LogLevel.Warning, level);
        Assert.Contains("email", text);
        Assert.Contains(WorkRelay.Switch, text);

        clock.Now += TimeSpan.FromSeconds(1);
        relay.Delivered("push", Task.FromResult(0L));
        Assert.Equal(2, log.Lines.Count);
        Assert.Contains("push", log.Lines[1].Text);
    }

    [Fact]
    public void A_wake_the_worker_process_heard_says_nothing()
    {
        var log = new Capture();
        var relay = new WorkRelay(false, null, log);
        relay.Delivered("email", Task.FromResult(1L));
        Assert.Empty(log.Lines);
    }

    [Fact]
    public void A_wake_Redis_would_not_take_is_a_warning()
    {
        var log = new Capture();
        var relay = new WorkRelay(false, null, log);
        relay.Delivered("ai", Task.FromException<long>(new InvalidOperationException("No connection is available")));
        var (level, text) = Assert.Single(log.Lines);
        Assert.Equal(LogLevel.Warning, level);
        Assert.Contains("ai", text);
    }
}
