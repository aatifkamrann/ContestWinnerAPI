using System.Security.Cryptography;
using WinnersPortal.Services.Settings;

namespace WinnersPortal.Services.Auth;

/// <summary>
/// What the token service is bound with: who a token says issued it and is
/// for, the key it is signed with, and how long each kind lasts.
/// </summary>
public sealed record JwtConfig(string Issuer, string Audience, byte[] Key, TimeSpan AccessLifetime, TimeSpan RefreshLifetime)
{
    /// <summary>The shipped values around a key — what an install that never touched the JWT settings runs with.</summary>
    public static JwtConfig WithKey(byte[] key) => new(
        JwtSettings.DefaultIssuer,
        JwtSettings.DefaultAudience,
        key,
        TimeSpan.FromMinutes(JwtSettings.DefaultAccessMinutes),
        TimeSpan.FromDays(JwtSettings.DefaultRefreshDays));
}

/// <summary>
/// The JWT settings group, and how the token service is bound from it. The
/// values are read once, at startup: the bearer handler's validation
/// parameters are fixed for the life of the process, so a saved change waits
/// for the next restart, and <see cref="RestartPendingAsync"/> says when one
/// is waiting. A value that would not bind is refused when it is saved
/// (<see cref="Problem"/>), not discovered at that restart.
/// </summary>
public static class JwtSettings
{
    public const string Group = "jwt";
    public const string IssuerKey = "jwt.issuer";
    public const string AudienceKey = "jwt.audience";
    public const string SigningKeyKey = "jwt.signingKey";
    public const string AccessMinutesKey = "jwt.accessTokenMinutes";
    public const string RefreshDaysKey = "jwt.refreshTokenDays";

    public const string DefaultIssuer = "WinnersPortal";
    public const string DefaultAudience = "WinnersPortal";
    public const int MaxNameLength = 200;

    /// <summary>
    /// An hour. A bearer client renews its access token through the refresh
    /// endpoint, so the lifetime is how long a copied token keeps working —
    /// not how often anybody signs in.
    /// </summary>
    public const int DefaultAccessMinutes = 60;
    public const int MinAccessMinutes = 5;
    public const int MaxAccessMinutes = 1440;

    /// <summary>
    /// A month. A client that has not been back in a month signs in again;
    /// one that comes back weekly never does, because every use issues a
    /// fresh token with a fresh month.
    /// </summary>
    public const int DefaultRefreshDays = 30;
    public const int MinRefreshDays = 1;
    public const int MaxRefreshDays = 365;

    /// <summary>HMAC-SHA256 wants a key at least as long as its output.</summary>
    public const int MinKeyBytes = 32;

    /// <summary>
    /// Where the key lived before it was a setting, beside the data-protection
    /// key ring. Read once, on the first start without a stored key, so an
    /// install that had one keeps everybody signed in; never written again.
    /// </summary>
    public const string KeyFileName = "jwt-signing.key";

    /// <summary>Why a value cannot be saved under a JWT key, or null when it can — or when the key is not one of these.</summary>
    public static string? Problem(string key, string? value) => key switch
    {
        IssuerKey when (value?.Trim().Length ?? 0) > MaxNameLength =>
            $"The issuer can be at most {MaxNameLength} characters.",
        AudienceKey when (value?.Trim().Length ?? 0) > MaxNameLength =>
            $"The audience can be at most {MaxNameLength} characters.",
        AccessMinutesKey when !string.IsNullOrEmpty(value) && Whole(value, MinAccessMinutes, MaxAccessMinutes) is null =>
            $"The access token lifetime must be a whole number of minutes from {MinAccessMinutes} to {MaxAccessMinutes} (a day).",
        RefreshDaysKey when !string.IsNullOrEmpty(value) && Whole(value, MinRefreshDays, MaxRefreshDays) is null =>
            $"The refresh token lifetime must be a whole number of days from {MinRefreshDays} to {MaxRefreshDays}.",
        // A token must be signed with something: the key is replaced, never removed.
        SigningKeyKey when string.IsNullOrEmpty(value) =>
            "The signing key cannot be removed — generate a new one to replace it.",
        SigningKeyKey when DecodeKey(value) is null =>
            $"The signing key must be base64 of at least {MinKeyBytes} bytes — use Generate for one.",
        _ => null,
    };

    /// <summary>The key a base64 value stands for, or null when it is not base64 or too short to sign with.</summary>
    public static byte[]? DecodeKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            var key = Convert.FromBase64String(value.Trim());
            return key.Length >= MinKeyBytes ? key : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>A new key, as the settings keep it.</summary>
    public static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(MinKeyBytes));

    /// <summary>
    /// The key an install without a stored one starts from: the file an
    /// earlier release wrote, when it is there and readable, else a new one.
    /// </summary>
    public static (string Key, bool FromFile) FirstKey(string keysDir)
    {
        var file = Path.Combine(keysDir, KeyFileName);
        if (File.Exists(file) && DecodeKey(File.ReadAllText(file)) is { } key)
            return (Convert.ToBase64String(key), true);
        return (NewKey(), false);
    }

    /// <summary>
    /// The values to bind the token service with. Anything unset — or out of
    /// shape, which only an environment variable or a hand-edited row can
    /// be — is the default. The key is the one exception: an install with no
    /// usable key stores one first (<see cref="FirstKey"/>), so the settings
    /// screen shows it set and the next restart signs with the same key.
    /// </summary>
    public static async Task<JwtConfig> LoadAsync(SettingsService settings, string keysDir, ILogger log, CancellationToken ct)
    {
        foreach (var key in new[] { IssuerKey, AudienceKey, AccessMinutesKey, RefreshDaysKey })
        {
            var raw = await settings.GetAsync(key, ct);
            if (Problem(key, raw) is { } problem)
                log.LogWarning("Ignoring {Key}, which the token service cannot use: {Problem} Its default applies.", key, problem);
        }

        var saved = await ResolveAsync(settings, ct);
        if (saved.Key is { } usable) return saved.Bind(usable);

        if (SettingsService.IsLocked(SigningKeyKey))
            throw new InvalidOperationException(
                $"{SettingsRegistry.EnvVarName(SigningKeyKey)} must be base64 of at least {MinKeyBytes} bytes.");
        if (await settings.GetAsync(SigningKeyKey, ct) is not null)
            log.LogError(
                "The stored JWT signing key is not base64 of {Bytes} bytes or more; a new key replaces it, and everybody signs in again.",
                MinKeyBytes);

        var (first, fromFile) = FirstKey(keysDir);
        await settings.SetManyAsync(
            new Dictionary<string, string?> { [SigningKeyKey] = first },
            changedBy: "startup",
            ct: ct);
        if (fromFile)
            log.LogInformation("The JWT signing key in {File} is now kept in the settings; the file is no longer read.", KeyFileName);
        else
            log.LogInformation("Made a JWT signing key and stored it in the settings.");
        return saved.Bind(Convert.FromBase64String(first));
    }

    /// <summary>
    /// Whether what is saved differs from what the running token service was
    /// bound with, so that a restart would change how tokens are signed,
    /// checked or timed.
    /// </summary>
    public static async Task<bool> RestartPendingAsync(SettingsService settings, JwtConfig bound, CancellationToken ct)
    {
        var saved = await ResolveAsync(settings, ct);
        return saved.Issuer != bound.Issuer
            || saved.Audience != bound.Audience
            || saved.AccessLifetime != bound.AccessLifetime
            || saved.RefreshLifetime != bound.RefreshLifetime
            || saved.Key is null
            || !CryptographicOperations.FixedTimeEquals(saved.Key, bound.Key);
    }

    /// <summary>The saved values as they would bind, with a null key when there is no usable one.</summary>
    private static async Task<ResolvedJwt> ResolveAsync(SettingsService settings, CancellationToken ct)
    {
        string Name(string? raw, string fallback) =>
            string.IsNullOrWhiteSpace(raw) || raw.Trim().Length > MaxNameLength ? fallback : raw.Trim();

        return new ResolvedJwt(
            Name(await settings.GetAsync(IssuerKey, ct), DefaultIssuer),
            Name(await settings.GetAsync(AudienceKey, ct), DefaultAudience),
            DecodeKey(await settings.GetAsync(SigningKeyKey, ct)),
            TimeSpan.FromMinutes(Whole(await settings.GetAsync(AccessMinutesKey, ct), MinAccessMinutes, MaxAccessMinutes) ?? DefaultAccessMinutes),
            TimeSpan.FromDays(Whole(await settings.GetAsync(RefreshDaysKey, ct), MinRefreshDays, MaxRefreshDays) ?? DefaultRefreshDays));
    }

    private sealed record ResolvedJwt(string Issuer, string Audience, byte[]? Key, TimeSpan AccessLifetime, TimeSpan RefreshLifetime)
    {
        public JwtConfig Bind(byte[] key) => new(Issuer, Audience, key, AccessLifetime, RefreshLifetime);
    }

    private static int? Whole(string? value, int min, int max) =>
        int.TryParse(value?.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n)
        && n >= min && n <= max
            ? n
            : null;
}
