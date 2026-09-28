using System.Collections.Concurrent;
using StackExchange.Redis;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Services.Settings;

namespace WinnersPortal.Services.Common;

/// <summary>
/// The reads that answer the same for every caller — the opportunity feed's
/// rows, the figures over it, the members the leaderboard and the Talent
/// page are cut from — held in this process for a few seconds, so a
/// crowd on the landing page costs the database one read per interval
/// rather than one per visitor. The lifetime is the
/// <c>limits.publicCacheSeconds</c> setting, 0 for off, read on every
/// call and so in force at once.
/// </summary>
/// <remarks>
/// What is held is rows, never an answer: whatever depends on the caller
/// or the moment — the Recommended verdicts, whether entry is still open,
/// which board tab and filters — is worked out per request from them.
/// <para>
/// Callers that arrive while a read is running wait for that read rather
/// than starting their own, and the read runs in a scope of its own
/// under no caller's cancellation: a visitor who closes the tab stops
/// waiting, and nobody else's page fails with them. A read that fails is
/// not kept, so the next caller tries again.
/// </para>
/// <para>
/// The writes a person makes and then looks for — publishing an opportunity,
/// cancelling one, announcing its winner, erasing or locking an account —
/// call <see cref="Clear"/>, so the one who made the change sees it on
/// the next page. Everything else is at most one lifetime behind.
/// </para>
/// <para>
/// Where <c>REDIS_URL</c> is set, a clear is also said on
/// <see cref="ClearChannel"/>, and every other API process that hears it
/// empties its own copy — the settings cache's bell, rung for these lists.
/// The held rows never leave the process: one read per interval per
/// process costs nothing, and a hit stays a pointer rather than a round
/// trip. Without Redis, or with it unreachable, a clear stays in this
/// process and the others catch up within one lifetime.
/// </para>
/// </remarks>
public sealed class PublicReads
{
    /// <summary>The Redis channel a clear is said on; the message is the sending process's id.</summary>
    public const string ClearChannel = "wp:public:clear";

    public const string SettingKey = "limits.publicCacheSeconds";
    public const int DefaultSeconds = 10;
    public const int MaxSeconds = 300;

    /// <summary>More kept answers than this and the expired ones go; still more and they all do. The feed's searches are what could run it up.</summary>
    public const int MaxEntries = 500;

    private readonly IServiceScopeFactory _scopes;
    private readonly SettingsService _settings;
    private readonly RedisConnection? _redis;
    private readonly ILogger<PublicReads>? _log;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<object, Entry> _entries = new();

    /// <summary>This process on the channel, so it can tell its own clear coming back from another's.</summary>
    internal string Instance { get; } = Guid.NewGuid().ToString("N");

    public PublicReads(IServiceScopeFactory scopes, SettingsService settings,
        RedisConnection? redis = null, ILogger<PublicReads>? log = null, TimeProvider? clock = null)
    {
        _scopes = scopes;
        _settings = settings;
        _redis = redis;
        _log = log;
        _clock = clock ?? TimeProvider.System;

        // Asynchronous, so a Redis that is configured but down does not hold
        // up the start the way a synchronous subscribe waits out its timeout.
        // Optional by contract: a failure is a warning, and this process then
        // hears no other's clears and trails them by at most one lifetime.
        if (redis?.Muxer is { } muxer)
            _ = muxer.GetSubscriber()
                .SubscribeAsync(RedisChannel.Literal(ClearChannel), (_, sender) => Heard((string?)sender))
                .ContinueWith(t => _log?.LogWarning(t.Exception,
                        "Redis subscribe failed; this process will not hear the others clear the public lists."),
                    TaskContinuationOptions.OnlyOnFaulted);
    }

    private sealed record Entry(Lazy<Task<object>> Read, DateTimeOffset Expires)
    {
        public bool Serves(DateTimeOffset now) =>
            // Still running: wait for it, however long it has taken — a
            // second read beside a slow one is how a busy database drowns.
            !Read.IsValueCreated || !Read.Value.IsCompleted
            || (Read.Value.IsCompletedSuccessfully && Expires > now);
    }

    /// <summary>The lifetime as the setting holds it: whole seconds from 0 to <see cref="MaxSeconds"/>; null for anything else.</summary>
    public static int? ParseSeconds(string? raw) =>
        int.TryParse(raw?.Trim(), out var s) && s is >= 0 and <= MaxSeconds ? s : null;

    /// <summary>
    /// The value <paramref name="read"/> gives for <paramref name="key"/>,
    /// from a read no older than the lifetime in force. With the lifetime
    /// at 0 the read runs on the caller's own context, exactly as it would
    /// with no cache here at all.
    /// </summary>
    public async Task<T> GetAsync<T>(object key, AppDbContext db, Func<AppDbContext, CancellationToken, Task<T>> read, CancellationToken ct)
        where T : class
    {
        var seconds = ParseSeconds(await _settings.GetAsync(SettingKey, ct)) ?? DefaultSeconds;
        if (seconds == 0) return await read(db, ct);
        return await GetAsync(key, TimeSpan.FromSeconds(seconds), async () =>
        {
            await using var scope = _scopes.CreateAsyncScope();
            return await read(scope.ServiceProvider.GetRequiredService<AppDbContext>(), CancellationToken.None);
        }, ct);
    }

    /// <summary>The held value for <paramref name="key"/>, or <paramref name="read"/>'s, shared with whoever else asks meanwhile.</summary>
    internal async Task<T> GetAsync<T>(object key, TimeSpan lifetime, Func<Task<T>> read, CancellationToken ct)
        where T : class
    {
        var now = _clock.GetUtcNow();
        Entry Fresh() => new(
            new Lazy<Task<object>>(async () => await read(), LazyThreadSafetyMode.ExecutionAndPublication),
            now + lifetime);

        // One entry wins the key; every caller that loses the race is
        // handed the winner, so they all wait on the same read.
        var entry = _entries.AddOrUpdate(key, _ => Fresh(), (_, held) => held.Serves(now) ? held : Fresh());
        if (_entries.Count > MaxEntries) Trim(now);

        try
        {
            return (T)await entry.Read.Value.WaitAsync(ct);
        }
        catch when (!ct.IsCancellationRequested)
        {
            // Failed for everyone who waited on it; the next caller reads again.
            _entries.TryRemove(new KeyValuePair<object, Entry>(key, entry));
            throw;
        }
    }

    /// <summary>
    /// Drops everything held, here and in every API process on the channel,
    /// so the next caller of each read goes to the database. Said and not
    /// waited for: the write that called it has already committed, and a
    /// Redis that is down must not hold up the answer to it.
    /// </summary>
    public void Clear()
    {
        _entries.Clear();
        try
        {
            _redis?.Muxer?.GetSubscriber()
                .Publish(RedisChannel.Literal(ClearChannel), Instance, CommandFlags.FireAndForget);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "The public lists were cleared here, but the Redis clear for the other processes failed.");
        }
    }

    /// <summary>A clear heard on the channel: another process's empties this one; this one's own, coming back, is already done.</summary>
    internal void Heard(string? sender)
    {
        if (sender != Instance) _entries.Clear();
    }

    /// <summary>How many answers are held or being read, for the tests of the ceiling.</summary>
    internal int Count => _entries.Count;

    private void Trim(DateTimeOffset now)
    {
        foreach (var (key, entry) in _entries)
            if (!entry.Serves(now))
                _entries.TryRemove(new KeyValuePair<object, Entry>(key, entry));
        if (_entries.Count > MaxEntries) _entries.Clear();
    }
}
