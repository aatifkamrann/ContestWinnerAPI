using System.Text;
using System.Text.RegularExpressions;

namespace WinnersPortal.Services.Opportunities;

/// <summary>
/// The first words of a brief as plain text, for the opportunity card. Briefs
/// are Markdown and nearly always open with a heading; the heading is a
/// label for what follows, not part of it, so it is dropped rather than
/// glued to the first sentence ("What we need We run a…"). List and quote
/// markers go, emphasis and code marks go, a link keeps its text and loses
/// its address. Two hundred characters, then an ellipsis.
/// </summary>
public static class Excerpt
{
    private static readonly Regex Marker = new(@"^(?:[-*+]|\d+[.)]|>)\s+", RegexOptions.Compiled);
    private static readonly Regex Link = new(@"!?\[([^\]]*)\]\([^)]*\)", RegexOptions.Compiled);

    public static string Of(string markdown)
    {
        var sb = new StringBuilder(Math.Min(markdown.Length, 240));
        foreach (var raw in markdown.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('#')) continue;
            line = Link.Replace(Marker.Replace(line, ""), "$1");
            foreach (var ch in line)
                if (ch is not ('*' or '`' or '_' or '>' or '[' or ']')) sb.Append(ch);
            sb.Append(' ');
            if (sb.Length >= 220) break;
        }
        var s = string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return s.Length > 200 ? s[..200].TrimEnd() + "…" : s;
    }
}
