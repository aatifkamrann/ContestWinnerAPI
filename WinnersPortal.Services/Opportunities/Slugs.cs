using System.Text;
using Microsoft.EntityFrameworkCore;
using WinnersPortal.Infrastructure.Data;

namespace WinnersPortal.Services.Opportunities;

public static class Slugs
{
    /// <summary>Lower-cased ASCII letters, digits and single hyphens; never empty.</summary>
    public static string From(string title)
    {
        var sb = new StringBuilder(title.Length);
        var lastWasHyphen = true; // suppresses a leading hyphen
        foreach (var ch in title.Trim().ToLowerInvariant())
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                sb.Append(ch);
                lastWasHyphen = false;
            }
            else if (!lastWasHyphen)
            {
                sb.Append('-');
                lastWasHyphen = true;
            }
        }
        while (sb.Length > 0 && sb[^1] == '-') sb.Length--;
        if (sb.Length > 120) sb.Length = 120;
        return sb.Length == 0 ? "opportunity" : sb.ToString();
    }

    /// <summary>Appends -2, -3… until the slug is free (excluding the opportunity itself when renaming).</summary>
    public static async Task<string> UniqueAsync(AppDbContext db, string title, Guid? exceptId, CancellationToken ct)
    {
        var baseSlug = From(title);
        var slug = baseSlug;
        for (var n = 2; await TakenAsync(db, slug, exceptId, ct); n++)
            slug = $"{baseSlug}-{n}";
        return slug;
    }

    /// <summary>Whether another opportunity already has the slug; the one being renamed does not count.</summary>
    private static async Task<bool> TakenAsync(AppDbContext db, string slug, Guid? exceptId, CancellationToken ct) =>
        db.UseDapper
            ? await db.Sql.ScalarAsync<int>(Procedures.OpportunitySlugTaken, new { slug, exceptId }, ct) == 1
            : await db.Opportunities.AnyAsync(c => c.Slug == slug && c.Id != exceptId, ct);
}
