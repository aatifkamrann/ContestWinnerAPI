using System.Globalization;
using System.Text;

namespace WinnersPortal.Services.Opportunities;

/// <summary>
/// Keyset-pagination cursor over (timestamp desc, id desc) — the blueprint's
/// rule that every list pages by keyset, never by offset, so page 40 costs the
/// same as page 1. Ordered by award, the cursor carries the last row's award
/// too, since that is then the first key.
/// </summary>
public readonly record struct Cursor(DateTimeOffset At, Guid Id, decimal? Amount = null)
{
    public string Encode()
    {
        var raw = Amount is { } amount
            ? $"{At.UtcTicks}:{Id:N}:{amount.ToString(CultureInfo.InvariantCulture)}"
            : $"{At.UtcTicks}:{Id:N}";
        return Convert.ToBase64String(Encoding.ASCII.GetBytes(raw))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static Cursor? Decode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            var b64 = value.Replace('-', '+').Replace('_', '/');
            var raw = Encoding.ASCII.GetString(Convert.FromBase64String(b64.PadRight((b64.Length + 3) / 4 * 4, '=')));
            var parts = raw.Split(':');
            if (parts.Length is not (2 or 3)) return null;
            return new Cursor(
                new DateTimeOffset(long.Parse(parts[0]), TimeSpan.Zero),
                Guid.ParseExact(parts[1], "N"),
                parts.Length == 3 ? decimal.Parse(parts[2], CultureInfo.InvariantCulture) : null);
        }
        catch
        {
            return null; // a bad cursor just restarts the list; it is not an error worth a 400
        }
    }
}
