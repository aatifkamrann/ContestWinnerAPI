using WinnersPortal.Services.Help;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Domain;

namespace WinnersPortal.Services.Settings;

/// <summary>
/// The single door to configuration. Rules, per the blueprint:
/// env vars override stored values (surfaced as locked), secrets are
/// encrypted at rest and never returned to the browser, every write is
/// audited, and writes publish a Redis invalidation so other instances
/// drop their cache.
/// </summary>
public sealed class SettingsService
{
    public const string InvalidationChannel = "wp:settings:changed";
    private const string SecretMask = "(secret)";

    private readonly IServiceScopeFactory _scopes;
    private readonly IDataProtector _protector;
    private readonly RedisConnection _redis;
    private readonly ILogger<SettingsService> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile Dictionary<string, string?>? _cache;

    public SettingsService(
        IServiceScopeFactory scopes,
        IDataProtectionProvider dataProtection,
        RedisConnection redis,
        ILogger<SettingsService> log)
    {
        _scopes = scopes;
        _protector = dataProtection.CreateProtector("WinnersPortal.Settings");
        _redis = redis;
        _log = log;
        // Optional by contract, so a configured-but-unreachable Redis must not
        // take the process down. Subscribe is synchronous and throws out of the
        // backlog when no connection is available, so it is guarded exactly as
        // the Publish in SaveAsync already is.
        try
        {
            _redis.Muxer?.GetSubscriber()
                .Subscribe(RedisChannel.Literal(InvalidationChannel), (_, _) => Invalidate());
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Redis subscribe failed; this process will not see invalidations from others.");
        }
    }

    public void Invalidate() => _cache = null;

    /// <summary>Resolved value: environment variable → stored row → default.</summary>
    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        var def = SettingsRegistry.Find(key)
            ?? throw new ArgumentException($"Unknown setting '{key}'.", nameof(key));
        var env = Environment.GetEnvironmentVariable(SettingsRegistry.EnvVarName(key));
        if (env is not null) return env;
        var stored = await LoadAsync(ct);
        return stored.TryGetValue(key, out var value) && value is not null ? value : def.Default;
    }

    public async Task<bool> IsSetupCompletedAsync(CancellationToken ct = default) =>
        string.Equals(await GetAsync("system.setupCompleted", ct), "true", StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------ setups

    /// <summary>
    /// A connection's setups as ids, names and states, at most one of them
    /// active. The main setup cannot be listed away while an environment
    /// variable still sets one of its values: the deployment says it exists,
    /// so it is shown whatever the stored list says — and active, when no
    /// other setup is.
    /// </summary>
    public async Task<IReadOnlyList<SetupEntry>> ListAsync(SetupKind kind, CancellationToken ct = default)
    {
        var list = Setups.Parse(await GetAsync(kind.ListKey, ct));
        if (list.Any(s => s.Id == Setups.MainId) || !kind.Fields.Any(IsLocked)) return list;
        var name = list.Any(s => string.Equals(s.Name, Setups.MainName, StringComparison.OrdinalIgnoreCase))
            ? "Main (environment)"
            : Setups.MainName;
        return [.. list, new SetupEntry(Setups.MainId, name, list.All(s => !s.Enabled))];
    }

    /// <summary>The active setup of a connection with its values resolved, or null while none is active.</summary>
    public async Task<SetupValues?> ActiveSetupAsync(SetupKind kind, CancellationToken ct = default) =>
        (await ListAsync(kind, ct)).FirstOrDefault(s => s.Enabled) is { } active
            ? await ValuesAsync(kind, active, ct)
            : null;

    /// <summary>Every setup of a connection with its values resolved — the inactive ones included.</summary>
    public async Task<IReadOnlyList<SetupValues>> SetupsAsync(SetupKind kind, CancellationToken ct = default)
    {
        var result = new List<SetupValues>();
        foreach (var entry in await ListAsync(kind, ct))
            result.Add(await ValuesAsync(kind, entry, ct));
        return result;
    }

    /// <summary>One setup by id, whether or not it is switched on; null when the list has no such setup.</summary>
    public async Task<SetupValues?> SetupAsync(SetupKind kind, string id, CancellationToken ct = default)
    {
        var entry = (await ListAsync(kind, ct)).FirstOrDefault(s => s.Id == id);
        return entry is null ? null : await ValuesAsync(kind, entry, ct);
    }

    private async Task<SetupValues> ValuesAsync(SetupKind kind, SetupEntry entry, CancellationToken ct)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var field in kind.Fields)
            values[field] = await GetAsync(Setups.FieldKey(field, entry.Id), ct);
        return new SetupValues(entry.Id, entry.Name, entry.Enabled, values);
    }

    /// <summary>Whether an environment variable sets this key, so the deployment rather than the screen decides it.</summary>
    public static bool IsLocked(string key) =>
        Environment.GetEnvironmentVariable(SettingsRegistry.EnvVarName(key)) is not null;

    /// <summary>Everything the admin settings screen shows. Secret values never leave as plaintext.</summary>
    public async Task<List<SettingsGroupDto>> GetForAdminAsync(CancellationToken ct = default)
    {
        var stored = await LoadAsync(ct);
        SettingDto Dto(SettingDefinition d)
        {
            var env = Environment.GetEnvironmentVariable(SettingsRegistry.EnvVarName(d.Key));
            var resolved = env ?? stored.GetValueOrDefault(d.Key) ?? d.Default;
            return new SettingDto(
                Key: d.Key,
                Label: d.Label,
                IsSecret: d.IsSecret,
                IsBoolean: d.IsBoolean,
                IsMultiline: d.IsMultiline,
                Choices: d.Choices,
                Locked: env is not null,
                EnvVar: env is not null ? SettingsRegistry.EnvVarName(d.Key) : null,
                Value: d.IsSecret ? null : resolved,
                HasValue: !string.IsNullOrEmpty(resolved),
                Default: d.Default,
                HelpTopic: Help.HelpRegistry.TopicIdForSetting(SettingsRegistry.MainKey(d.Key)));
        }

        var groups = new List<SettingsGroupDto>();
        foreach (var (name, label) in SettingsRegistry.Groups)
        {
            var kind = Setups.KindOfGroup(name);
            var items = SettingsRegistry.All
                .Where(d => d.Group == name && (kind is null || (d.Key != kind.ListKey && !kind.Fields.Contains(d.Key))))
                .Select(Dto)
                .ToList();
            if (kind is null)
            {
                groups.Add(new SettingsGroupDto(name, label, items));
                continue;
            }

            var setups = new List<SetupDto>();
            foreach (var entry in await ListAsync(kind, ct))
            {
                var fields = kind.Fields.Select(f => Dto(SettingsRegistry.Find(Setups.FieldKey(f, entry.Id))!)).ToList();
                setups.Add(new SetupDto(entry.Id, entry.Name, entry.Enabled, fields.Any(f => f.Locked), fields));
            }
            // What a setup added on the screen starts from, before it is saved:
            // the fields with the defaults a new setup gets, and nothing else.
            var template = kind.Fields
                .Select(f => SettingsRegistry.Find(f)!)
                .Select(d => Dto(d) with
                {
                    Locked = false,
                    EnvVar = null,
                    Value = d.IsSecret || d.MainOnlyDefault ? null : d.Default,
                    HasValue = false,
                    Default = d.MainOnlyDefault ? null : d.Default,
                })
                .ToList();
            groups.Add(new SettingsGroupDto(name, label, items, kind.ListKey, setups, template));
        }
        return groups;
    }

    /// <summary>
    /// Validate, encrypt secrets, upsert, audit, invalidate. Null (or empty)
    /// clears a value. Env-locked keys either throw or, for the setup path,
    /// are skipped with a log line.
    /// </summary>
    public async Task SetManyAsync(
        IReadOnlyDictionary<string, string?> updates,
        string changedBy,
        bool allowSystem = false,
        bool skipLocked = false,
        CancellationToken ct = default)
    {
        var accepted = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (key, value) in updates)
        {
            var def = SettingsRegistry.Find(key)
                ?? throw new SettingsValidationException($"Unknown setting '{key}'.");
            if (def.Group == SettingsRegistry.SystemGroup && !allowSystem)
                throw new SettingsValidationException($"'{key}' is not editable.");
            if (Environment.GetEnvironmentVariable(SettingsRegistry.EnvVarName(key)) is not null)
            {
                if (skipLocked)
                {
                    _log.LogInformation("Skipping '{Key}': locked by {Env}.",
                        key, SettingsRegistry.EnvVarName(key));
                    continue;
                }
                throw new SettingsValidationException(
                    $"'{key}' is locked by environment variable {SettingsRegistry.EnvVarName(key)}.");
            }
            var given = Entered(def, value);
            if (ValueProblem(def, given) is { } refused)
                throw new SettingsValidationException(refused);
            // A list of setups is stored as it will be read back: checked,
            // names trimmed. Empty goes back to the main setup alone.
            if (Setups.KindOfList(key) is not null && !string.IsNullOrEmpty(given))
            {
                var (list, problem) = Setups.Read(given);
                if (problem is not null) throw new SettingsValidationException(problem);
                accepted[key] = Setups.Write(list!);
                continue;
            }
            accepted[key] = string.IsNullOrEmpty(given) ? null : given;
        }
        // The two addresses are judged together, as they will stand after
        // this batch: the one being saved beside the other as it is.
        if (accepted.ContainsKey(WebOrigin.WebUrlKey) || accepted.ContainsKey(WebOrigin.ApiUrlKey))
        {
            var webUrl = accepted.TryGetValue(WebOrigin.WebUrlKey, out var w) ? w : await GetAsync(WebOrigin.WebUrlKey, ct);
            var apiUrl = accepted.TryGetValue(WebOrigin.ApiUrlKey, out var a) ? a : await GetAsync(WebOrigin.ApiUrlKey, ct);
            if (WebOrigin.PairProblem(webUrl, apiUrl) is { } pair)
                throw new SettingsValidationException(pair);
        }
        await SetupChangesAsync(accepted, ct);
        if (accepted.Count == 0) return;

        await _gate.WaitAsync(ct);
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var keys = accepted.Keys.ToList();
            var rows = await db.Settings.Where(s => keys.Contains(s.Key)).ToDictionaryAsync(s => s.Key, ct);
            var now = DateTimeOffset.UtcNow;

            foreach (var (key, value) in accepted)
            {
                var def = SettingsRegistry.Find(key)!;
                rows.TryGetValue(key, out var row);
                var oldForAudit = row?.Value is null ? null : def.IsSecret ? SecretMask : Audited(def, row.Value);

                if (row is null)
                {
                    row = new Setting { Key = key };
                    db.Settings.Add(row);
                }
                row.Value = value is null ? null : def.IsSecret ? _protector.Protect(value) : value;
                row.IsSecret = def.IsSecret;
                row.UpdatedAtUtc = now;
                row.UpdatedBy = changedBy;

                db.SettingAudits.Add(new SettingAudit
                {
                    Key = key,
                    OldValue = oldForAudit,
                    NewValue = value is null ? null : def.IsSecret ? SecretMask : Audited(def, value),
                    ChangedBy = changedBy,
                    ChangedAtUtc = now,
                });
            }
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            _gate.Release();
        }

        Invalidate();
        try
        {
            _redis.Muxer?.GetSubscriber()
                .Publish(RedisChannel.Literal(InvalidationChannel), "changed");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Settings saved, but the Redis invalidation publish failed.");
        }
    }

    /// <summary>
    /// A value as it will be judged and stored: without surrounding spaces
    /// for a <see cref="SettingDefinition.Trimmed"/> setting, as typed for
    /// any other — a password may end in a space on purpose.
    /// </summary>
    public static string? Entered(SettingDefinition def, string? value) =>
        def.Trimmed ? value?.Trim() : value;

    /// <summary>
    /// Why a value cannot be stored under a setting, or null when it can: the
    /// few settings with a shape. Pure, so the setup wizard can refuse a value
    /// before it creates the administrator rather than after.
    /// </summary>
    public static string? ValueProblem(SettingDefinition def, string? value)
    {
        // A terms version that is not a whole number would silently fall
        // back to 1 and re-prompt nobody.
        if (def.Key == Terms.VersionKey && !string.IsNullOrEmpty(value) && !Terms.IsValidVersion(value))
            return "Terms version must be a whole number of 1 or more.";
        // A choice is refused rather than stored: a provider nothing
        // answers to would read as "configured" and text nobody.
        if (def.Choices is { } choices && !string.IsNullOrEmpty(value)
            && !choices.Any(c => string.Equals(c.Value, value, StringComparison.Ordinal)))
            return $"'{def.Key}' must be one of: {string.Join(", ", choices.Select(c => c.Value))}.";
        // A threshold that is not a number would leave the old one in force
        // with nothing on the screen to say so.
        if (def.Key == SlowQueryStats.SettingKey && !string.IsNullOrEmpty(value) && SlowQueryStats.ParseMs(value) is null)
            return "Slow-query threshold must be a whole number of milliseconds: 0 or more, where 0 turns it off.";
        // Likewise the cache's lifetime, and one with no ceiling would let
        // the public lists fall minutes behind with nothing to say why.
        if (def.Key == Common.PublicReads.SettingKey && !string.IsNullOrEmpty(value) && Common.PublicReads.ParseSeconds(value) is null)
            return $"Public page cache must be a whole number of seconds from 0 to {Common.PublicReads.MaxSeconds}, where 0 turns it off.";
        // A Web URL that is not an address, or an API URL that is not an
        // origin, would leave the browser unable to reach the API at all.
        // A JWT value that would not bind is refused now, not quietly
        // replaced by its default at the next restart.
        // A Redis address nothing could parse would be found out at the
        // next restart, by a process that then runs without Redis.
        // A build host address that is not one, or a timeout that is not a
        // number of minutes, would leave every claim queued for nothing.
        return WebOrigin.Problem(def.Key, value)
            ?? Auth.JwtSettings.Problem(def.Key, value)
            ?? RedisSettings.Problem(def.Key, value)
            ?? Preview.PreviewHost.Problem(def.Key, value);
    }

    /// <summary>
    /// Holds a batch of writes to what the setup lists say, and completes it.
    /// A value may only go to a setup the list — as this batch leaves it —
    /// still has, so a stale screen cannot write into a setup somebody else
    /// just removed. A setup the batch removes takes its stored values with
    /// it, secrets included, unless the deployment sets one of them: that
    /// setup is the deployment's to remove.
    /// </summary>
    private async Task SetupChangesAsync(Dictionary<string, string?> accepted, CancellationToken ct)
    {
        var stored = await LoadAsync(ct);
        foreach (var kind in Setups.Kinds)
        {
            var before = await ListAsync(kind, ct);
            var after = accepted.TryGetValue(kind.ListKey, out var listed)
                ? Setups.Parse(listed)
                : before;

            foreach (var key in accepted.Keys)
            {
                if (Setups.KindOfField(SettingsRegistry.MainKey(key)) != kind) continue;
                var id = Setups.ParseScoped(key)?.SetupId ?? Setups.MainId;
                if (after.All(s => s.Id != id))
                    throw new SettingsValidationException(
                        $"'{key}' belongs to a setup that is no longer in the list — reload the settings and try again.");
            }

            foreach (var gone in before.Where(b => after.All(a => a.Id != b.Id)))
            {
                foreach (var field in kind.Fields)
                {
                    var key = Setups.FieldKey(field, gone.Id);
                    if (IsLocked(key))
                        throw new SettingsValidationException(
                            $"“{gone.Name}” cannot be removed: environment variable {SettingsRegistry.EnvVarName(key)} sets one of its values. "
                            + "Remove it from the deployment first, or switch the setup off.");
                    if (stored.GetValueOrDefault(key) is not null) accepted[key] = null;
                }
            }
        }
    }

    /// <summary>
    /// What the audit keeps: the value itself, except for a machine-written
    /// system blob (the uploaded logo), where its size is the whole story and
    /// the bytes would only bloat the trail.
    /// </summary>
    private static string Audited(SettingDefinition def, string value) =>
        def.Group == SettingsRegistry.SystemGroup && value.Length > 200 ? $"[{value.Length} characters]" : value;

    private async Task<Dictionary<string, string?>> LoadAsync(CancellationToken ct)
    {
        var cache = _cache;
        if (cache is not null) return cache;

        await _gate.WaitAsync(ct);
        try
        {
            if (_cache is not null) return _cache;
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var rows = await db.Settings.AsNoTracking().ToListAsync(ct);

            var loaded = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                if (row.Value is null || !row.IsSecret)
                {
                    loaded[row.Key] = row.Value;
                    continue;
                }
                try
                {
                    loaded[row.Key] = _protector.Unprotect(row.Value);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex,
                        "Could not decrypt setting '{Key}' — treating it as unset. Did the data-protection keys change?",
                        row.Key);
                    loaded[row.Key] = null;
                }
            }
            _cache = loaded;
            return loaded;
        }
        finally
        {
            _gate.Release();
        }
    }
}

public sealed class SettingsValidationException(string message) : Exception(message);

public sealed record SettingDto(
    string Key,
    string Label,
    bool IsSecret,
    bool IsBoolean,
    bool IsMultiline,
    bool Locked,
    string? EnvVar,
    string? Value,
    bool HasValue,
    string? Default,
    string? HelpTopic,
    IReadOnlyList<SettingChoice>? Choices = null);

/// <param name="Locked">An environment variable sets one of its values, so the deployment — not the screen — can remove it.</param>
/// <param name="InUse">What still lives in it, in words ("12 files"), when that stops it being removed.</param>
/// <param name="LastTest">The last test run on it, which the badge on its card reads; null when it has never been tested.</param>
public sealed record SetupDto(
    string Id,
    string Name,
    bool Enabled,
    bool Locked,
    List<SettingDto> Settings,
    string? InUse = null,
    SetupTestDto? LastTest = null);

/// <param name="Settings">The group's own fields — for a connection with setups, the ones that govern all of them.</param>
/// <param name="ListKey">The key the screen writes the list of setups to; null for a group without setups.</param>
/// <param name="Template">The fields a setup added on the screen starts from.</param>
public sealed record SettingsGroupDto(
    string Name,
    string Label,
    List<SettingDto> Settings,
    string? ListKey = null,
    List<SetupDto>? Setups = null,
    List<SettingDto>? Template = null);
