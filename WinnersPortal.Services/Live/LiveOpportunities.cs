using Microsoft.EntityFrameworkCore;
using WinnersPortal.Domain;
using WinnersPortal.Infrastructure.Data;

namespace WinnersPortal.Services.Live;

/// <summary>The one read the live board's hub makes: whether an opportunity may be watched.</summary>
public sealed class LiveOpportunities(AppDbContext db)
{
    /// <summary>
    /// The opportunity exists and its board is public. The same non-answer for
    /// "does not exist" and "not yours to watch" — a probe learns nothing a
    /// 404 on the page would not already say.
    /// </summary>
    public async Task<bool> WatchableAsync(string slug, CancellationToken ct)
    {
        var wanted = slug.Trim().ToLowerInvariant();
        var status = db.UseDapper
            ? (OpportunityStatus?)await db.Sql.SingleOrDefaultAsync<int?>(Procedures.OpportunityStatus, new { slug = wanted }, ct)
            : await db.Opportunities
                .Where(c => c.Slug == wanted)
                .Select(c => (OpportunityStatus?)c.Status)
                .SingleOrDefaultAsync(ct);
        return status is not null && LiveRules.CanWatch(status.Value);
    }
}
