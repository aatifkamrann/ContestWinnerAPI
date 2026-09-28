namespace WinnersPortal.Services.Opportunities;

/// <summary>
/// The feed's search reads the title and the brief through the tsvector,
/// which knows English words and nothing shorter. The title is also
/// matched as typed, so half a word finds it — and the characters LIKE
/// reads as wildcards are escaped so they mean themselves: percent and
/// underscore everywhere, and the bracket SQL Server adds to the set.
/// Escaped, the bracket is a literal on both, so one pattern serves both.
/// </summary>
public static class Search
{
    public const string Escape = "\\";

    /// <summary>The LIKE pattern for "the title contains what was typed".</summary>
    public static string TitlePattern(string q)
    {
        var escaped = q.Trim()
            .Replace(Escape, Escape + Escape)
            .Replace("%", Escape + "%")
            .Replace("_", Escape + "_")
            .Replace("[", Escape + "[");
        return "%" + escaped + "%";
    }
}
