using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using WinnersPortal.Infrastructure.Data;

namespace WinnersPortal.Services.Settings;

/// <summary>
/// Whether the API uses Redis, and where it is: the two settings of the
/// <c>redis</c> group, off by default. Off, everything Redis would carry —
/// settings invalidation, the public lists' clears, the workers' wakes, the
/// live board's groups — stays in this process, which is all one process
/// needs; on, they cross to every API process on the same Redis.
/// </summary>
/// <remarks>
/// Read once, before the host is built: the connection is opened and its
/// subscriptions made as the process starts, and SignalR chooses its
/// backplane while the container is being composed, so a saved change waits
/// for the next restart of the API, and <see cref="RestartPending"/> says
/// when one is owed. That is the JWT group's contract, for the same reason.
/// Like every setting, each can be given by the environment
/// (<c>WP_REDIS_ENABLED</c>, <c>WP_REDIS_URL</c>), in force only while
/// nothing is saved; the older <c>REDIS_URL</c>, which compose sets, is read
/// as both variables.
/// </remarks>
public static class RedisSettings
{
    public const string Group = "redis";
    public const string EnabledKey = "redis.enabled";
    public const string UrlKey = "redis.url";

    /// <summary>The variable that named Redis before the settings did; still honoured, as the pair of pins.</summary>
    public const string LegacyEnv = "REDIS_URL";

    public const int MaxUrlLength = 500;

    /// <summary>What the API was, or would be, started with.</summary>
    /// <param name="Enabled">The switch.</param>
    /// <param name="Url">The address as stored, trimmed; null for blank.</param>
    /// <param name="Source">Where the pair came from: <c>setting</c>, <c>environment</c> or <c>default</c>.</param>
    public sealed record Choice(bool Enabled, string? Url, string Source)
    {
        public static readonly Choice Off = new(false, null, "default");

        /// <summary>The address to connect to, or null: off, no address, or an address that does not parse.</summary>
        public string? Active => Enabled && Url is not null && Problem(UrlKey, Url) is null ? Url : null;

        /// <summary>On with an address that will not do: switched on, and going nowhere.</summary>
        public bool OnButUnusable => Enabled && Active is null;

        /// <summary>The endpoints alone, for a log line or a screen: never the password an address may carry.</summary>
        public string? Endpoints => Active is null ? null
            : string.Join(", ", ConfigurationOptions.Parse(Active).EndPoints.Select(e => e switch
            {
                // DnsEndPoint.ToString() leads with the address family.
                System.Net.DnsEndPoint dns => $"{dns.Host}:{dns.Port}",
                _ => e.ToString() ?? "",
            }));
    }

    /// <summary>Why a value cannot be saved under one of these keys, or null when it can — or when the key is not one of these.</summary>
    public static string? Problem(string key, string? value)
    {
        if (key != UrlKey || string.IsNullOrWhiteSpace(value)) return null;
        if (value.Trim().Length > MaxUrlLength) return $"The Redis address can be at most {MaxUrlLength} characters.";
        try
        {
            var options = ConfigurationOptions.Parse(value.Trim());
            return options.EndPoints.Count == 0 ? "The Redis address names no server: host:port, or redis://host:port." : null;
        }
        catch (Exception e) when (e is ArgumentException or FormatException)
        {
            return "The Redis address is not one StackExchange.Redis can read: host:port, or redis://host:port, with any options after a comma.";
        }
    }

    /// <summary>The choice the two raw values make, from wherever they were read.</summary>
    public static Choice Resolve(string? enabled, string? url, string source) =>
        new(string.Equals(enabled?.Trim(), "true", StringComparison.OrdinalIgnoreCase),
            string.IsNullOrWhiteSpace(url) ? null : url.Trim(),
            source);

    /// <summary>
    /// What <see cref="LegacyEnv"/> stands for: both pins, where neither is
    /// set already. Null when there is nothing to adopt.
    /// </summary>
    public static (string Enabled, string Url)? LegacyPins(string? legacy, string? enabledPin, string? urlPin) =>
        string.IsNullOrWhiteSpace(legacy) || enabledPin is not null || urlPin is not null
            ? null
            : ("true", legacy.Trim());

    /// <summary>
    /// The pair as the database and the environment hold it now, read
    /// before the host exists on a context of its own: what is saved wins,
    /// and each variable fills in only where nothing is. A database that
    /// cannot be read yet — a fresh install with no tables, a server that
    /// is not up — leaves the variables, or the default, off, and says why:
    /// the migrations a moment later report the real trouble properly.
    /// </summary>
    public static async Task<Choice> ReadAsync(DatabaseProvider provider, string connectionString, Action<string>? warn, CancellationToken ct)
    {
        var enabledEnv = SettingsService.EnvOf(EnabledKey);
        var urlEnv = SettingsService.EnvOf(UrlKey);
        string? enabledRow = null, urlRow = null;
        try
        {
            await using var db = new AppDbContext(AppDbContextOptions.Build(provider, connectionString));
            var rows = await db.Settings.AsNoTracking()
                .Where(s => s.Key == EnabledKey || s.Key == UrlKey)
                .ToListAsync(ct);
            enabledRow = rows.FirstOrDefault(r => r.Key == EnabledKey)?.Value;
            urlRow = rows.FirstOrDefault(r => r.Key == UrlKey)?.Value;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            warn?.Invoke($"The Redis settings could not be read before the start ({e.GetType().Name}); Redis is off for this process unless the environment says otherwise.");
        }
        return Resolve(
            string.IsNullOrEmpty(enabledRow) ? enabledEnv : enabledRow,
            string.IsNullOrEmpty(urlRow) ? urlEnv : urlRow,
            SourceName(SettingsService.SourceOf(enabledEnv, enabledRow), SettingsService.SourceOf(urlEnv, urlRow)));
    }

    /// <summary>The pair as saved now, through the settings service, for the restart-pending check.</summary>
    public static async Task<Choice> LoadAsync(SettingsService settings, CancellationToken ct) =>
        Resolve(await settings.GetAsync(EnabledKey, ct), await settings.GetAsync(UrlKey, ct),
            SourceName(await settings.SourceAsync(EnabledKey, ct), await settings.SourceAsync(UrlKey, ct)));

    /// <summary>Where the pair comes from: a saved half makes it the setting's, else a variable the environment's.</summary>
    private static string SourceName(SettingSource enabled, SettingSource url) =>
        enabled == SettingSource.Saved || url == SettingSource.Saved ? "setting"
        : enabled == SettingSource.Environment || url == SettingSource.Environment ? "environment"
        : "default";

    /// <summary>Whether a restart would change what this process does with Redis: on to off, off to on, or another address.</summary>
    public static bool RestartPending(Choice bound, Choice saved) =>
        !string.Equals(bound.Active, saved.Active, StringComparison.Ordinal);
}
