using WinnersPortal.Domain;
using System.Security.Cryptography;
using WinnersPortal.Services.Settings;

namespace WinnersPortal.Services.Notifications;

/// <summary>
/// VAPID (RFC 8292) is how a browser's push service knows who is sending:
/// a P-256 key pair whose public half every subscription is bound to. The
/// pure parts live here — generation, the base64url the standard speaks,
/// and the checks a browser's subscription passes at the door — so they
/// can be tested without a push service in the room.
/// </summary>
public static class Vapid
{
    /// <summary>A fresh pair: the public key as the 65-byte uncompressed
    /// point, the private key as the 32-byte scalar, both base64url.</summary>
    public static (string PublicKey, string PrivateKey) Generate()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var p = ecdsa.ExportParameters(includePrivateParameters: true);
        var point = new byte[65];
        point[0] = 0x04;
        LeftPad(p.Q.X!, point, 1);
        LeftPad(p.Q.Y!, point, 33);
        var scalar = new byte[32];
        LeftPad(p.D!, scalar, 0);
        return (Base64Url(point), Base64Url(scalar));
    }

    // The exported coordinates are 32 bytes for P-256; a shorter array would
    // be one with leading zeros dropped, which the fixed layout restores.
    private static void LeftPad(byte[] src, byte[] dst, int offset) =>
        Array.Copy(src, 0, dst, offset + 32 - src.Length, src.Length);

    public static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Null when the text is not base64url at all.</summary>
    public static byte[]? FromBase64Url(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var s = text.Replace('-', '+').Replace('_', '/');
        s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
        try
        {
            return Convert.FromBase64String(s);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    // What a browser hands over when it subscribes. Checked at the door so
    // a malformed subscription fails the person who can fix it (by
    // subscribing again) rather than the worker, hours later.

    /// <summary>The browser's key: a 65-byte uncompressed P-256 point.</summary>
    public static bool IsValidP256dh(string? key) =>
        FromBase64Url(key) is { Length: 65 } bytes && bytes[0] == 0x04;

    /// <summary>The browser's auth secret: 16 bytes.</summary>
    public static bool IsValidAuth(string? secret) => FromBase64Url(secret) is { Length: 16 };

    public const int MaxEndpointLength = PushDevice.MaxEndpointLength;

    /// <summary>Push services speak https and nothing else.</summary>
    public static bool IsValidEndpoint(string? endpoint) =>
        endpoint is not null
        && endpoint.Length <= MaxEndpointLength
        && Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps;
}

/// <summary>
/// The portal's own pair, generated the first time anything needs it and
/// kept as two system settings (the private half encrypted, like every
/// secret). Never typed and never shown: a pair an operator could edit is a
/// pair an operator could break, and a changed pair silently orphans every
/// subscription made under the old one.
/// </summary>
public sealed class PushKeys(SettingsService settings)
{
    public const string PublicKeySetting = "system.pushPublicKey";
    public const string PrivateKeySetting = "system.pushPrivateKey";

    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<(string PublicKey, string PrivateKey)> GetAsync(CancellationToken ct)
    {
        if (await StoredAsync(ct) is { } stored) return stored;
        await _gate.WaitAsync(ct);
        try
        {
            // Two first callers at once generate one pair, not two.
            if (await StoredAsync(ct) is { } raced) return raced;
            var pair = Vapid.Generate();
            await settings.SetManyAsync(new Dictionary<string, string?>
            {
                [PublicKeySetting] = pair.PublicKey,
                [PrivateKeySetting] = pair.PrivateKey,
            }, changedBy: "push", allowSystem: true, ct: ct);
            return pair;
        }
        finally
        {
            _gate.Release();
        }
    }

    // Either half missing means both are regenerated: a public key whose
    // private half was lost with the data-protection keys is no key at all.
    private async Task<(string, string)?> StoredAsync(CancellationToken ct)
    {
        var pub = await settings.GetAsync(PublicKeySetting, ct);
        var priv = await settings.GetAsync(PrivateKeySetting, ct);
        return string.IsNullOrEmpty(pub) || string.IsNullOrEmpty(priv) ? null : (pub, priv);
    }
}
