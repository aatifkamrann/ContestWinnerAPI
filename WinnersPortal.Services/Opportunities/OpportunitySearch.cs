using Microsoft.EntityFrameworkCore;
using NpgsqlTypes;
using WinnersPortal.Domain;
using WinnersPortal.Infrastructure.Data;

namespace WinnersPortal.Services.Opportunities;

/// <summary>
/// The feed's text search, in the dialect of the database behind it. On
/// Postgres the generated tsvector reads the title and the brief as English
/// words; on SQL Server a full-text index over the same two columns does,
/// through FREETEXT. On both, the title is also matched as typed, so half a
/// word — "inven" — finds "Inventory dashboard" the way a member expects,
/// and on SQL Server it also covers the seconds a fresh opportunity waits for
/// the full-text index to catch up. Kept apart from the service so the two
/// shapes can be rendered and pinned by the tests.
/// </summary>
public static class OpportunitySearch
{
    public static IQueryable<Opportunity> Apply(IQueryable<Opportunity> query, AppDbContext db, string q)
    {
        var pattern = Search.TitlePattern(q);
        var words = q.Trim();
        return db.IsSqlServer
            ? query.Where(c =>
                EF.Functions.FreeText(c.Title, words)
                || EF.Functions.FreeText(c.BriefMarkdown, words)
                || EF.Functions.Like(c.Title, pattern, Search.Escape))
            : query.Where(c =>
                EF.Property<NpgsqlTsVector>(c, "SearchVector").Matches(EF.Functions.PlainToTsQuery("english", q))
                || EF.Functions.ILike(c.Title, pattern, Search.Escape));
    }
}
