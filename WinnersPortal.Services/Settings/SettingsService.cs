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

    /// <summary>What secret values are protected under; a database test opens one with it to know the database is this portal's.</summary>
    public const string ProtectorPurpose = "WinnersPortal.Settings";
    private volatile Dictionary<string, string?>? _cache;

    public SettingsService(
        IServiceScopeFactory scopes,
        IDataProtectionProvider dataProtection,
        RedisConnection redis,
        ILogger<SettingsService> log)
    {
        _scopes = scopes;
        _protector = dataProtection.CreateProtector(ProtectorPurpose);
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

    /// <summary>Resolved value: stored row → environment variable → default (<see cref="Resolve"/>).</summary>
    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        var def = SettingsRegistry.Find(key)
            ?? throw new ArgumentException($"Unknown setting '{key}'.", nameof(key));
        var stored = await LoadAsync(ct);
        return Resolve(def, EnvOf(key), stored.GetValueOrDefault(key));
    }

    /// <summary>
    /// A value from its three sources. What is saved wins. Blank is not a
    /// value, so a cleared field falls through to the environment variable
    /// (<c>WP_&lt;GROUP&gt;_&lt;KEY&gt;</c>), which is a deployment's starting
    /// value and never a lock, and then to the default.
    /// </summary>
    public static string? Resolve(SettingDefinition def, string? env, string? stored) =>
        !string.IsNullOrEmpty(stored) ? stored : env ?? def.Default;

    /// <summary>The key's environment variable, when the deployment sets one.</summary>
    public static string? EnvOf(string key) => Environment.GetEnvironmentVariable(SettingsRegistry.EnvVarName(key));

    /// <summary>Where a value comes from: saved on the screen, the deployment's environment, or the default.</summary>
    public static SettingSource SourceOf(string? env, string? stored) =>
        !string.IsNullOrEmpty(stored) ? SettingSource.Saved
        : env is not null ? SettingSource.Environment
        : SettingSource.Default;

    /// <summary>Where this key's value comes from now.</summary>
    public async Task<SettingSource> SourceAsync(string key, CancellationToken ct = default)
    {
        _ = SettingsRegistry.Find(key) ?? throw new ArgumentException($"Unknown setting '{key}'.", nameof(key));
        var stored = await LoadAsync(ct);
        return SourceOf(EnvOf(key), stored.GetValueOrDefault(key));
    }

    public async Task<bool> IsSetupCompletedAsync(CancellationToken ct = default) =>
        string.Equals(await GetAsync("system.setupCompleted", ct), "true", StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------ setups

    /// <summary>
    /// A connection's setups as ids, names and states, at most one of them
    /// active — the saved list, which wins like any saved value: a setup
    /// removed on the screen stays removed whatever the environment sets.
    /// </summary>
    public async Task<IReadOnlyList<SetupEntry>> ListAsync(SetupKind kind, CancellationToken ct = default) =>
        Setups.Parse(await GetAsync(kind.ListKey, ct));

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

    /// <summary>Everything the admin settings screen shows. Secret values never leave as plaintext.</summary>
    public async Task<List<SettingsGroupDto>> GetForAdminAsync(CancellationToken ct = default)
    {
        var stored = await LoadAsync(ct);
        SettingDto Dto(SettingDefinition d)
        {
            var env = EnvOf(d.Key);
            var saved = stored.GetValueOrDefault(d.Key);
            var resolved = Resolve(d, env, saved);
            return new SettingDto(
                Key: d.Key,
                Label: d.Label,
                IsSecret: d.IsSecret,
                IsBoolean: d.IsBoolean,
                IsMultiline: d.IsMultiline,
                Choices: d.Choices,
                Source: SourceOf(env, saved) switch
                {
                    SettingSource.Saved => "saved",
                    SettingSource.Environment => "environment",
                    _ => "default",
                },
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
                setups.Add(new SetupDto(entry.Id, entry.Name, entry.Enabled, fields));
            }
            // What a setup added on the screen starts from, before it is saved:
            // the fields with the defaults a new setup gets, and nothing else.
            var template = kind.Fields
                .Select(f => SettingsRegistry.Find(f)!)
                .Select(d => Dto(d) with
                {
                    Source = "default",
                    EnvVar = null,
                    Value = d.IsSecret || d.MainOnlyDefault ? null : d.Default,
                    HasValue = false,
                    Default = d.MainOnlyDefault ? null : d.Default,
                })
                .ToList();
            groups.Add(new SettingsGroupDto(name, label, items, kind.ListKey, setups, template));
        }

        // The AI switch has a consequence outside its group: on, what
        // members type leaves the server, and the privacy policy under
        // Legal should say so. The gap is read here, from the two values
        // as they are in force, and shown on the group whose switch
        // caused it rather than on the policy nobody reopens.
        if (AiNotice(key => SettingsRegistry.Find(key) is { } d ? Resolve(d, EnvOf(key), stored.GetValueOrDefault(key)) : null) is { } gap
            && groups.FindIndex(g => g.Name == "ai") is var ai and >= 0)
            groups[ai] = groups[ai] with { Notice = gap };
        return groups;
    }

    /// <summary>The AI group's notice from the values in force: the switch, read as the screen reads it, and the policy.</summary>
    public static string? AiNotice(Func<string, string?> resolved) =>
        Privacy.AiGap(
            string.Equals(resolved("ai.enabled")?.Trim(), "true", StringComparison.OrdinalIgnoreCase),
            resolved(Privacy.MarkdownKey));

    /// <summary>
    /// Validate, encrypt secrets, upsert, audit, invalidate. Null (or empty)
    /// clears a value, and the environment variable or the default fills in.
    /// Every key can be saved: what is saved wins over the environment.
    /// </summary>
    public async Task SetManyAsync(
        IReadOnlyDictionary<string, string?> updates,
        string changedBy,
        bool allowSystem = false,
        CancellationToken ct = default)
    {
        var accepted = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (key, value) in updates)
        {
            var def = SettingsRegistry.Find(key)
                ?? throw new SettingsValidationException($"Unknown setting '{key}'.");
            if (def.Group == SettingsRegistry.SystemGroup && !allowSystem)
                throw new SettingsValidationException($"'{key}' is not editable.");
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
        // An AI timeout or cap that is not a number would quietly become its
        // default while the screen showed something else.
        return WebOrigin.Problem(def.Key, value)
            ?? Auth.JwtSettings.Problem(def.Key, value)
            ?? RedisSettings.Problem(def.Key, value)
            ?? Preview.PreviewHost.Problem(def.Key, value)
            ?? Ai.AiLimits.Problem(def.Key, value);
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

/// <summary>Where a setting's value comes from: what is saved wins, then the deployment's environment variable, then the default.</summary>
public enum SettingSource { Saved, Environment, Default }

/// <param name="Source">saved, environment or default: where the value in force comes from.</param>
/// <param name="EnvVar">The deployment's variable for this key when it sets one — in force only while nothing is saved.</param>
public sealed record SettingDto(
    string Key,
    string Label,
    bool IsSecret,
    bool IsBoolean,
    bool IsMultiline,
    string Source,
    string? EnvVar,
    string? Value,
    bool HasValue,
    string? Default,
    string? HelpTopic,
    IReadOnlyList<SettingChoice>? Choices = null);

/// <param name="InUse">What still lives in it, in words ("12 files"), when that stops it being removed.</param>
/// <param name="LastTest">The last test run on it, which the badge on its card reads; null when it has never been tested.</param>
public sealed record SetupDto(
    string Id,
    string Name,
    bool Enabled,
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
    List<SettingDto>? Template = null,
    /// <summary>A warning the group's state earns, shown above its fields: today, the AI switch on with a privacy policy that does not say so.</summary>
    string? Notice = null);
