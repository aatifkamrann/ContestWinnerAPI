using System.Collections.Concurrent;
using System.Threading.Channels;
using StackExchange.Redis;
using WinnersPortal.Services.Settings;

namespace WinnersPortal.Services.Common;

/// <summary>
/// A request's nudge to a background worker, so the work it has just
/// queued leaves in seconds rather than at the worker's next sweep. One
/// per worker: <see cref="Email.EmailWorkSignal"/>,
/// <see cref="GitHub.GitHubWorkSignal"/>, <see cref="Notifications.PushWorkSignal"/>
/// and <see cref="Ai.AiWorkSignal"/>. A wake that finds its worker already
/// woken is dropped: one sweep takes everything that is waiting.
/// </summary>
public abstract class WorkSignal
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly WorkRelay? _relay;

    /// <summary>The worker's name on <see cref="WorkRelay.WakeChannel"/>.</summary>
    public string Name { get; }

    protected WorkSignal(string name, WorkRelay? relay)
    {
        Name = name;
        _relay = relay;
        relay?.Listen(this);
    }

    /// <summary>
    /// Wakes the worker: this process's, and where this process runs no
    /// workers, the one that does, over Redis.
    /// </summary>
    public void Wake()
    {
        WakeHere();
        _relay?.Pass(Name);
    }

    internal void WakeHere() => _channel.Writer.TryWrite(true);

    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await _channel.Reader.ReadAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // timeout elapsed — a normal sweep tick
        }
    }
}

/// <summary>
/// Which API process does the background work, and how a wake reaches it
/// from one that does not.
/// </summary>
/// <remarks>
/// The workers do not coordinate: each reads what is pending and acts on
/// it, and two processes reading the same row both act — every email sent
/// twice, a second repository made for one entrant. So where there is more
/// than one API process, exactly one runs them, and the others start with
/// <c>WORKERS=false</c> (<see cref="RunsWorkers"/> false). A process that
/// runs none still queues work, and its wake has no worker of its own to
/// reach: where <c>REDIS_URL</c> is set it says the worker's name on
/// <see cref="WakeChannel"/>, and the process that runs the workers wakes
/// that one. Without Redis the work waits for that process's next sweep
/// (at most 30 seconds). A wake nobody hears is a warning, at most every
/// few minutes: it means no process is running the workers, or the one
/// that is has lost Redis.
/// </remarks>
public sealed class WorkRelay
{
    /// <summary>The Redis channel a wake crosses on; the message is the worker's name.</summary>
    public const string WakeChannel = "wp:work:wake";

    /// <summary>The environment switch; unset or true, this process runs the workers.</summary>
    public const string Switch = "WORKERS";

    /// <summary>How long after one "nobody heard" warning the next may be written.</summary>
    internal static readonly TimeSpan UnheardQuiet = TimeSpan.FromMinutes(5);

    private readonly RedisConnection? _redis;
    private readonly ILogger<WorkRelay>? _log;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, WorkSignal> _signals = new(StringComparer.Ordinal);
    private long _lastWarnedTicks = long.MinValue;

    /// <summary>Whether this process runs the background workers.</summary>
    public bool RunsWorkers { get; }

    public WorkRelay(bool runsWorkers, RedisConnection? redis = null, ILogger<WorkRelay>? log = null, TimeProvider? clock = null)
    {
        RunsWorkers = runsWorkers;
        _redis = redis;
        _log = log;
        _clock = clock ?? TimeProvider.System;

        // Only the process with the workers listens. Asynchronous, as the
        // public lists' subscribe is, so a Redis that is down does not hold
        // up the start; a failure is a warning, after which the others'
        // work waits here for the next sweep instead of arriving at once.
        if (runsWorkers && redis?.Muxer is { } muxer)
            _ = muxer.GetSubscriber()
                .SubscribeAsync(RedisChannel.Literal(WakeChannel), (_, name) => Heard((string?)name))
                .ContinueWith(t => _log?.LogWarning(t.Exception,
                        "Redis subscribe failed; work the other API processes queue waits for this one's next sweep."),
                    TaskContinuationOptions.OnlyOnFaulted);
    }

    /// <summary>A signal made in this process, so a wake heard on Redis can find it by name.</summary>
    internal void Listen(WorkSignal signal) => _signals[signal.Name] = signal;

    /// <summary>A wake from another process: the named worker here sweeps now. An unknown name is ignored.</summary>
    internal void Heard(string? name)
    {
        if (name is not null && _signals.TryGetValue(name, out var signal)) signal.WakeHere();
    }

    /// <summary>
    /// A wake in a process that runs no workers goes to the one that does.
    /// Never awaited: the request that queued the work has saved it, and a
    /// Redis that is down must not hold it up.
    /// </summary>
    internal void Pass(string name)
    {
        if (RunsWorkers || _redis?.Muxer is not { } muxer) return;
        try
        {
            _ = muxer.GetSubscriber()
                .PublishAsync(RedisChannel.Literal(WakeChannel), name)
                .ContinueWith(t => Delivered(name, t), TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            Warn(ex, "The {Worker} worker could not be woken over Redis; the work waits for its next sweep.", name);
        }
    }

    /// <summary>What Redis said about one wake: how many processes heard it, or why it did not go.</summary>
    internal void Delivered(string name, Task<long> publish)
    {
        if (publish.IsFaulted)
            Warn(publish.Exception, "The {Worker} worker could not be woken over Redis; the work waits for its next sweep.", name);
        else if (publish.IsCompletedSuccessfully && publish.Result == 0)
            Warn(null, "A wake for the {Worker} worker reached no API process: none is running the workers, or the one that is has lost Redis. "
                + "Work queued here waits until one does; exactly one API process should run without WORKERS=false.", name);
    }

    private void Warn(Exception? ex, string message, string name)
    {
        var now = _clock.GetUtcNow().UtcTicks;
        var last = Interlocked.Read(ref _lastWarnedTicks);
        if (last != long.MinValue && now - last < UnheardQuiet.Ticks) return;
        if (Interlocked.CompareExchange(ref _lastWarnedTicks, now, last) != last) return;
        _log?.LogWarning(ex, message, name);
    }
}
