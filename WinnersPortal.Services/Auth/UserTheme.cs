using System.Text.Json;
using System.Text.RegularExpressions;

namespace WinnersPortal.Services.Auth;

/// <summary>
/// The user's colours, as implemented in BookingApp and extended here: the
/// accent, and one badge colour per opportunity status. Colour only — light/dark
/// mode is a per-device preference and never leaves the client. Persisted on
/// the user row so the look follows them across devices.
/// </summary>
public static partial class UserTheme
{
    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    public static partial Regex HexColor();

    /// <summary>The statuses a badge can wear, in the order the picker lists them.</summary>
    public static readonly string[] StatusKeys = ["draft", "open", "reviewing", "awarded", "cancelled"];

    /// <summary>
    /// Null (clear) stays null; a valid #RRGGBB is canonicalised to uppercase;
    /// anything else is rejected.
    /// </summary>
    public static (bool Ok, string? Normalized) Normalize(string? color)
    {
        if (string.IsNullOrWhiteSpace(color)) return (true, null);
        var trimmed = color.Trim();
        return HexColor().IsMatch(trimmed) ? (true, trimmed.ToUpperInvariant()) : (false, null);
    }

    /// <summary>
    /// Badge colours arrive as {status: colour}. An unknown status or a bad
    /// colour rejects the whole request rather than being dropped — a typo
    /// must not silently lose a choice. Null, empty, or every value cleared
    /// means the stock set and stores as null. Kept as compact JSON in the
    /// picker's order, so two equal choices compare equal as strings.
    /// </summary>
    public static (bool Ok, string? Json) NormalizeStatus(IReadOnlyDictionary<string, string?>? colours)
    {
        if (colours is null || colours.Count == 0) return (true, null);
        var kept = new SortedDictionary<string, string>(
            Comparer<string>.Create((a, b) => Array.IndexOf(StatusKeys, a).CompareTo(Array.IndexOf(StatusKeys, b))));
        foreach (var (key, value) in colours)
        {
            if (Array.IndexOf(StatusKeys, key) < 0) return (false, null);
            var (ok, hex) = Normalize(value);
            if (!ok) return (false, null);
            if (hex is not null) kept[key] = hex;
        }
        return kept.Count == 0 ? (true, null) : (true, JsonSerializer.Serialize(kept));
    }

    /// <summary>The stored JSON back as the object the client sent, or null for the stock set.</summary>
    public static Dictionary<string, string>? ParseStatus(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
