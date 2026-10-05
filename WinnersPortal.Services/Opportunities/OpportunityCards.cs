using WinnersPortal.Services.Profiles;
using Microsoft.EntityFrameworkCore;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Domain;

namespace WinnersPortal.Services.Opportunities;

/// <summary>
/// One opportunity as a card — the row the browse feed reads and the shape it
/// sends, kept in one place so the single card on a new member’s welcome
/// screen and the twenty on the feed are the same card: same facts, same
/// trust line, same fit. A field added to one cannot be missing from the
/// other, because there is only one. The feed's LINQ is here too, beside
/// the projection; its T-SQL is in <c>OpportunityCards.SqlServer.cs</c>.
/// </summary>
public static partial class OpportunityCards
{
    /// <summary>What a card is projected from — the row, before any words are put on it.</summary>
    public sealed class Row
    {
        public Guid Id { get; set; }
        public string Slug { get; set; } = "";
        public string Title { get; set; } = "";
        public decimal AwardAmount { get; set; }
        public string Currency { get; set; } = "";
        public string? Category { get; set; }
        public string? Subcategory { get; set; }
        public int MinMeritScore { get; set; }
        public List<string> Skills { get; set; } = [];
        public string Brief240 { get; set; } = "";
        public DateTimeOffset? DeadlineUtc { get; set; }
        public DateTimeOffset? StartsAtUtc { get; set; }
        public DateTimeOffset? EntryCloseUtc { get; set; }
        public DateTimeOffset? PublishedAtUtc { get; set; }
        public OpportunityStatus Status { get; set; }
        public OpportunityDelivery Delivery { get; set; }
        public OpportunityKind Kind { get; set; }
        public bool RequiresCompose { get; set; }
        public Guid ClientId { get; set; }
        public string ClientName { get; set; } = "";
        public DateTimeOffset? ClientAvatarAt { get; set; }
        public int EntrantCount { get; set; }
        public int MilestoneCount { get; set; }
        public int ClientAwardsPaid { get; set; }
        public int ClientRatingCount { get; set; }
        public double? ClientRatingAvg { get; set; }
        /// <summary>Applications submitted, whatever became of them — filled by CountApplicationsAsync, not the projection.</summary>
        public int ApplicationCount { get; set; }
    }

    /// <summary>
    /// The projection, on whatever filter and order the caller has already
    /// put on the opportunities. One join the query already paid for: the
    /// counters are maintained on write (see Recount), where they used to
    /// be five correlated subqueries per card.
    /// </summary>
    public static IQueryable<Row> Rows(IQueryable<Opportunity> opportunities) => opportunities.Select(c => new Row
    {
        Id = c.Id,
        Slug = c.Slug,
        Title = c.Title,
        AwardAmount = c.AwardAmount,
        Currency = c.Currency,
        Category = c.Category,
        Subcategory = c.Subcategory,
        MinMeritScore = c.MinMeritScore,
        Skills = c.Skills.OrderBy(s => s.Order).Select(s => s.Name).ToList(),
        // Postgres substr() is forgiving past the end, so this is safe for short briefs.
        Brief240 = c.BriefMarkdown.Substring(0, 240),
        DeadlineUtc = c.DeadlineUtc,
        StartsAtUtc = c.StartsAtUtc,
        EntryCloseUtc = c.EntryCloseUtc,
        PublishedAtUtc = c.PublishedAtUtc,
        Status = c.Status,
        Delivery = c.Delivery,
        Kind = c.Kind,
        RequiresCompose = c.RequiresCompose,
        ClientId = c.ClientId,
        ClientName = c.Client!.DisplayName,
        ClientAvatarAt = c.Client.AvatarUpdatedAtUtc,
        EntrantCount = c.ActiveEntryCount,
        MilestoneCount = c.MilestoneCount,
        // The trust line: before anyone opens the brief, the card says how
        // this client's past promises went.
        ClientAwardsPaid = c.Client.AwardsPaidCount,
        ClientRatingCount = c.Client.RatingCount,
        ClientRatingAvg = c.Client.RatingCount == 0
            ? (double?)null
            : (double)c.Client.RatingSum / c.Client.RatingCount,
    });

    /// <summary>
    /// What the feed was asked for, decided once by the service: which
    /// opportunities, in which order, from where. <see cref="Cursor"/> is null
    /// when there is none or it belongs to another order; <see cref="Take"/>
    /// is one more than the page, so the feed knows whether there is more.
    /// </summary>
    public sealed record FeedQuery(
        bool OpenOnly, string? Q, string? CategoryKey, string? SubcategoryKey, bool Ending, bool ByAward,
        Cursor? Cursor, int Take, DateTimeOffset Now);

    /// <summary>The feed's rows on Postgres: the filters, the order and the keyset, then the projection.</summary>
    internal static Task<List<Row>> FeedLinqAsync(AppDbContext db, FeedQuery f, CancellationToken ct)
    {
        var now = f.Now;
        var query = db.Opportunities.AsNoTracking()
            .Where(c => c.Status != OpportunityStatus.Draft && c.Status != OpportunityStatus.Cancelled);
        // "Open for entry" has to mean enterable, or the filter is a lie:
        // an opportunity whose last joining date has gone is still open and
        // still building, and belongs under All opportunities with its own
        // badge. Null EntryCloseUtc falls back to the deadline, which is
        // what closed entry before the two dates were separated.
        if (f.OpenOnly)
            query = query.Where(c => c.Status == OpportunityStatus.Open
                && (c.EntryCloseUtc ?? c.DeadlineUtc) > now);

        // Words through the database's own text search, and the title as
        // typed — the two shapes are OpportunitySearch's.
        if (f.Q is { } q) query = OpportunitySearch.Apply(query, db, q);

        // By kind of work. An unknown key matches nothing rather than
        // everything: a filter that silently widened would mislead.
        if (f.CategoryKey is { } catKey)
        {
            query = query.Where(c => c.Category == catKey);
            if (f.SubcategoryKey is { } subKey)
                query = query.Where(c => c.Subcategory == subKey);
        }

        if (f.Ending)
            query = query.Where(c => c.DeadlineUtc > now);

        // Keyset on whichever order is in force — the cursor carries the
        // timestamp of the last row, and here is which timestamp it was.
        // By award it carries the award too.
        if (f.Cursor is { } cur)
        {
            var amount = cur.Amount ?? 0m;
            query = f.ByAward
                ? query.Where(c =>
                    c.AwardAmount < amount ||
                    (c.AwardAmount == amount && (c.PublishedAtUtc < cur.At ||
                        (c.PublishedAtUtc == cur.At && c.Id.CompareTo(cur.Id) < 0))))
                : f.Ending
                    ? query.Where(c =>
                        c.DeadlineUtc > cur.At ||
                        (c.DeadlineUtc == cur.At && c.Id.CompareTo(cur.Id) > 0))
                    : query.Where(c =>
                        c.PublishedAtUtc < cur.At ||
                        (c.PublishedAtUtc == cur.At && c.Id.CompareTo(cur.Id) < 0));
        }

        var ordered = f.ByAward
            ? query.OrderByDescending(c => c.AwardAmount).ThenByDescending(c => c.PublishedAtUtc).ThenByDescending(c => c.Id)
            : f.Ending
                ? query.OrderBy(c => c.DeadlineUtc).ThenBy(c => c.Id)
                : query.OrderByDescending(c => c.PublishedAtUtc).ThenByDescending(c => c.Id);

        return Rows(ordered.Take(f.Take)).ToListAsync(ct);
    }

    /// <summary>The opportunities open for entry right now, as cards — the welcome screen's read.</summary>
    internal static Task<List<Row>> OpenLinqAsync(AppDbContext db, DateTimeOffset now, CancellationToken ct) =>
        Rows(db.Opportunities.AsNoTracking()
                .Where(c => c.Status == OpportunityStatus.Open && (c.EntryCloseUtc ?? c.DeadlineUtc) > now))
            .ToListAsync(ct);

    /// <summary>
    /// How many have applied to each card's opportunity, read in one grouped
    /// query for the whole page of rows rather than a subquery per card —
    /// the one figure on a card that is not kept as a counter on the opportunity.
    /// </summary>
    public static async Task CountApplicationsAsync(AppDbContext db, IReadOnlyCollection<Row> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return;
        var ids = rows.Select(r => r.Id).ToList();
        var counts = db.UseDapper
            ? await CountApplicationsSqlAsync(db.Sql, ids, ct)
            : await db.Applications.AsNoTracking()
                .Where(a => ids.Contains(a.OpportunityId))
                .GroupBy(a => a.OpportunityId)
                .Select(g => new { OpportunityId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.OpportunityId, x => x.Count, ct);
        foreach (var r in rows) r.ApplicationCount = counts.GetValueOrDefault(r.Id);
    }

    /// <summary>
    /// The wire shape. The viewer is the freelancer whose fit colours the
    /// card, or null — a visitor, a client, or a feed nobody switched
    /// Recommended on for — and then no card carries a verdict.
    /// </summary>
    public static OpportunityCard Dto(Row c, Viewer? viewer, DateTimeOffset now) => new OpportunityCard
    {
        Slug = c.Slug,
        Title = c.Title,
        Excerpt = Excerpt.Of(c.Brief240),
        // What kind of work, as keys the filter takes back and as the
        // words the card shows.
        Category = c.Category,
        CategoryLabel = OpportunityCategories.Find(c.Category)?.Label,
        Subcategory = c.Subcategory,
        SubcategoryLabel = OpportunityCategories.Find(c.Category) is { } cat
            ? OpportunityCategories.FindSub(cat, c.Subcategory)?.Label
            : null,
        MinMeritScore = c.MinMeritScore,
        Skills = c.Skills,
        Fit = viewer is null
            ? null
            : FitReader.Dto(viewer.Judge(
                c.MinMeritScore, c.Skills, c.Category, Schedule.StartsAt(c.StartsAtUtc, c.PublishedAtUtc), c.DeadlineUtc)),
        AwardAmount = c.AwardAmount,
        Currency = c.Currency,
        DeadlineUtc = c.DeadlineUtc,
        // Null means entry runs to the deadline. The card resolves it
        // rather than sending both, so no reader has to know the fallback
        // rule to say "Entry closed".
        EntryCloseUtc = Schedule.EntryClosesAt(c.EntryCloseUtc, c.DeadlineUtc),
        EntryOpen = Schedule.EntryOpen(c.Status, c.EntryCloseUtc, c.DeadlineUtc, now),
        // The start day where the client set one — information, never a gate.
        StartsAtUtc = c.StartsAtUtc,
        PublishedAtUtc = c.PublishedAtUtc,
        Status = OpportunityNames.StatusName(c.Status),
        // How work is handed in — the card says it up front, because an
        // entrant with no GitHub account needs to know before opening a
        // brief they cannot enter.
        Delivery = Delivery.Name(c.Delivery),
        Kind = MilestonePay.KindName(c.Kind),
        RequiresCompose = c.RequiresCompose,
        ClientName = c.ClientName,
        ClientAvatarUrl = Profiles.AvatarRules.Url(c.ClientId, c.ClientAvatarAt),
        EntrantCount = c.EntrantCount,
        ApplicationCount = c.ApplicationCount,
        MilestoneCount = c.MilestoneCount,
        ClientAwardsPaid = c.ClientAwardsPaid,
        ClientRatingAvg = c.ClientRatingAvg,
        ClientRatingCount = c.ClientRatingCount,
    };
}
