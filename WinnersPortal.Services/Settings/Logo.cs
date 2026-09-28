using System.Text;

namespace WinnersPortal.Services.Settings;

/// <summary>
/// One uploaded brand image. There are two: the logo, which the rail and the
/// sign-in frame wear, and the tab icon, which is separate because the shapes
/// are different jobs — a wordmark reads at 32px beside the portal name and
/// becomes a smear in a 16px tab, so a portal with a wide logo needs a square
/// mark of its own. The icon falls back to the logo when it is empty, so a
/// portal that never thinks about it still gets one.
///
/// Both are kept as a data URL in a system setting rather than in object
/// storage, so a portal that never configured storage can still wear its own
/// mark, and the public endpoint that serves them needs nothing but the
/// settings cache.
/// </summary>
public sealed record BrandImage(string Name, string DataKey, string StampKey)
{
    /// <summary>The logo. Outranks <c>branding.logoUrl</c>: the URL is for a logo hosted elsewhere.</summary>
    public static readonly BrandImage Logo = new("logo", "system.logoData", "system.logoStamp");

    /// <summary>The tab icon. Outranks the logo, which outranks the Logo URL.</summary>
    public static readonly BrandImage Icon = new("icon", "system.iconData", "system.iconStamp");

    public static readonly IReadOnlyList<BrandImage> All = [Logo, Icon];

    /// <summary>
    /// Where this image is served. Every upload is a new URL, so the old one
    /// may be cached forever and the new one is never stale.
    /// </summary>
    public string PublicPath(string stamp) => $"/api/public/{Name}?v={Uri.EscapeDataString(stamp)}";
}

/// <summary>
/// The rules both brand images share: small by rule — a logo is a mark, not
/// a poster — and sniffed rather than trusted, because a content type is
/// only what the browser claims.
/// </summary>
public static class Logo
{
    public const int MaxBytes = 512 * 1024;

    public const string TypesSentence = "PNG, SVG, JPEG, WebP or GIF";

    private static readonly HashSet<string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/svg+xml", "image/webp", "image/gif",
    };

    /// <summary>Null when the upload may proceed, else what the screen says. Checks the claim; <see cref="Sniff"/> checks the bytes.</summary>
    public static string? Problem(string? contentType, long length)
    {
        if (length <= 0) return "The file is empty.";
        if (length > MaxBytes) return $"Keep it under {MaxBytes / 1024} KB — it is a mark, not a poster.";
        if (contentType is null || !Types.Contains(contentType.Split(';')[0].Trim()))
            return $"Use a {TypesSentence}.";
        return null;
    }

    /// <summary>The image type the bytes actually are, or null when they are none of the served kinds.</summary>
    public static string? Sniff(ReadOnlySpan<byte> bytes)
    {
        // Byte by byte: the signature opens with 0x89, which no UTF-8 literal can spell.
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == (byte)'P' && bytes[2] == (byte)'N' && bytes[3] == (byte)'G')
            return "image/png";
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return "image/jpeg";
        if (bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8)) return "image/gif";
        if (bytes.Length >= 12 && bytes.StartsWith("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8)) return "image/webp";

        // SVG is text: markup that opens an <svg> element somewhere in its
        // first kilobyte, after any byte-order mark, whitespace or prolog.
        var head = Encoding.UTF8.GetString(bytes[..Math.Min(bytes.Length, 1024)]).TrimStart('﻿', ' ', '\t', '\r', '\n');
        if (head.StartsWith('<') && head.Contains("<svg", StringComparison.OrdinalIgnoreCase)) return "image/svg+xml";
        return null;
    }

    public static string Encode(string contentType, byte[] bytes) =>
        $"data:{contentType};base64,{Convert.ToBase64String(bytes)}";

    /// <summary>The stored form back into bytes; false for anything that is not a data URL of a served type.</summary>
    public static bool TryParse(string? dataUrl, out string contentType, out byte[] bytes)
    {
        contentType = "";
        bytes = [];
        if (dataUrl is null || !dataUrl.StartsWith("data:", StringComparison.Ordinal)) return false;
        var comma = dataUrl.IndexOf(',');
        if (comma < 0) return false;
        var meta = dataUrl[5..comma];
        if (!meta.EndsWith(";base64", StringComparison.Ordinal)) return false;
        var type = meta[..^";base64".Length];
        if (!Types.Contains(type)) return false;
        try
        {
            bytes = Convert.FromBase64String(dataUrl[(comma + 1)..]);
        }
        catch (FormatException)
        {
            return false;
        }
        if (bytes.Length == 0) return false;
        contentType = type;
        return true;
    }

    /// <summary>A new stamp for a served URL — see <see cref="BrandImage.PublicPath"/>.</summary>
    public static string NewStamp() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
}
