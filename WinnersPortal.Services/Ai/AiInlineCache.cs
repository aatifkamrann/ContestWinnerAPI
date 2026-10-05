using System.Collections.Concurrent;
using WinnersPortal.Domain;

namespace WinnersPortal.Services.Ai;

/// <summary>
/// The answers the inline tools gave in the last hour, by member, feature
/// and the hash of the exact prompt: a reload of the page, or a second
/// press on a form that has not changed, reads the draft already paid for
/// instead of buying it again. The browser keeps the same guard for one
/// page's life; this one outlives the page. In-process and bounded, like
/// the public-page cache — never Redis, never the database.
/// </summary>
public sealed class AiInlineCache
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);
    public const int MaxEntries = 500;

    private readonly ConcurrentDictionary<(Guid Member, AiFeature Feature, string Hash), (string Json, DateTimeOffset At)> entries = new();

    /// <summary>The canonical answer held for this member, feature and prompt, or null when there is none fresh enough.</summary>
    public string? Get(Guid member, AiFeature feature, string hash, DateTimeOffset now) =>
        entries.TryGetValue((member, feature, hash), out var hit) && now - hit.At < Lifetime ? hit.Json : null;

    public void Put(Guid member, AiFeature feature, string hash, string canonicalJson, DateTimeOffset now)
    {
        entries[(member, feature, hash)] = (canonicalJson, now);
        if (entries.Count <= MaxEntries) return;
        // Over the bound: drop what has expired, then the oldest, until it fits.
        foreach (var (key, value) in entries)
            if (now - value.At >= Lifetime) entries.TryRemove(key, out _);
        while (entries.Count > MaxEntries)
        {
            var oldest = entries.MinBy(e => e.Value.At);
            if (!entries.TryRemove(oldest.Key, out _)) break;
        }
    }

    public int Count => entries.Count;
}
