using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using WinnersPortal.Domain;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Services.Settings;

namespace WinnersPortal.Services.Opportunities;

/// <summary>
/// Keeps <see cref="OpportunityCategories.Current"/> in step with the
/// <c>Categories</c> table: seeds an empty table with the shipped list on
/// first start, reads the whole list at start and after every save, and tells
/// the other API processes to read it again over Redis. Held whole in memory
/// because it is short, read on almost every page, and changed by hand.
/// </summary>
public sealed class CategoryCatalog
{
    public const string ChangedChannel = "wp:categories:changed";

    private readonly IServiceScopeFactory _scopes;
    private readonly RedisConnection _redis;
    private readonly ILogger<CategoryCatalog> _log;

    /// <summary>This process on the channel, so its own news is not read twice.</summary>
    internal string Instance { get; } = Guid.NewGuid().ToString("N");

    public CategoryCatalog(IServiceScopeFactory scopes, RedisConnection redis, ILogger<CategoryCatalog> log)
    {
        _scopes = scopes;
        _redis = redis;
        _log = log;
        // Optional by contract, guarded as the settings' subscription is.
        try
        {
            _redis.Muxer?.GetSubscriber()
                .Subscribe(RedisChannel.Literal(ChangedChannel), (_, sender) => Heard(sender));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Redis subscribe failed; this process will not see category changes saved by others until it restarts.");
        }
    }

    /// <summary>
    /// Puts the shipped list into an empty table. Every start asks, so an
    /// install that predates the table, a fresh one and one just moved into
    /// a new database all end up with a list; one that has a list, however
    /// edited, is left alone. Two processes starting together may both try:
    /// the second one's insert fails on the key, and the first one's rows stand.
    /// </summary>
    public async Task EnsureSeededAsync(AppDbContext db, CancellationToken ct)
    {
        if (await db.Categories.AnyAsync(ct)) return;
        db.Categories.AddRange(Rows(DefaultCategories.All, DateTimeOffset.UtcNow, by: null));
        try
        {
            await db.SaveChangesAsync(ct);
            _log.LogInformation("Categories: the table was empty, so it now holds the {Count} the portal ships with.", DefaultCategories.All.Count);
        }
        catch (DbUpdateException ex)
        {
            db.ChangeTracker.Clear();
            _log.LogInformation(ex, "Categories: another process seeded the list first.");
        }
    }

    /// <summary>Reads the list and makes it the one every reader sees.</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        OpportunityCategories.Use(await ReadAsync(db, ct));
    }

    /// <summary>After a save: read it here, then tell the other processes to.</summary>
    public async Task ChangedAsync(CancellationToken ct)
    {
        await LoadAsync(ct);
        try
        {
            _redis.Muxer?.GetSubscriber()
                .Publish(RedisChannel.Literal(ChangedChannel), Instance, CommandFlags.FireAndForget);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "The categories were saved, but the Redis notice for the other processes failed; they see the change at their next start.");
        }
    }

    /// <summary>Another process saved the list; this one's own notice, coming back, is already done.</summary>
    internal void Heard(string? sender)
    {
        if (sender == Instance) return;
        _ = Task.Run(async () =>
        {
            try { await LoadAsync(CancellationToken.None); }
            catch (Exception ex) { _log.LogWarning(ex, "Another process changed the categories, and reading them here failed; this process keeps the list it had."); }
        });
    }

    /// <summary>The stored list as a taxonomy, in picker order.</summary>
    public static async Task<Taxonomy> ReadAsync(AppDbContext db, CancellationToken ct)
    {
        if (db.UseDapper)
        {
            return await db.Sql.MultipleAsync(Procedures.CategoryList, null, async grid =>
            {
                var categories = (await grid.ReadAsync<CategoryRead>()).ToList();
                var subs = (await grid.ReadAsync<SubcategoryRead>()).ToLookup(s => s.CategoryKey, StringComparer.Ordinal);
                return new Taxonomy(categories.Select(c => new OpportunityCategory(
                    c.Key, c.Label,
                    [.. subs[c.Key].Select(s => new OpportunitySubcategory(s.Key, s.Label, Sql.JsonList<string>(s.Skills), s.Hidden))],
                    Sql.JsonList<string>(c.Skills), c.Hidden)));
            }, ct);
        }
        return await ListLinqAsync(db, ct);
    }

    /// <summary>The LINQ beside <c>Category_List</c>: what Postgres runs.</summary>
    internal static async Task<Taxonomy> ListLinqAsync(AppDbContext db, CancellationToken ct)
    {
        var rows = await db.Categories.AsNoTracking()
            .Include(c => c.Subcategories)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Key)
            .ToListAsync(ct);
        return new Taxonomy(rows.Select(c => new OpportunityCategory(
            c.Key, c.Label,
            [.. c.Subcategories.OrderBy(s => s.SortOrder).ThenBy(s => s.Key, StringComparer.Ordinal)
                .Select(s => new OpportunitySubcategory(s.Key, s.Label, s.Skills, s.Hidden))],
            c.Skills, c.Hidden)));
    }

    /// <summary>A list as the rows that store it, numbered in its order.</summary>
    public static IEnumerable<Category> Rows(IEnumerable<OpportunityCategory> list, DateTimeOffset now, string? by) =>
        list.Select((c, i) => new Category
        {
            Key = c.Key,
            Label = c.Label,
            SortOrder = i,
            Hidden = c.Hidden,
            Skills = [.. c.Skills],
            UpdatedAtUtc = now,
            UpdatedBy = by,
            Subcategories = [.. c.Subcategories.Select((s, j) => new Subcategory
            {
                CategoryKey = c.Key,
                Key = s.Key,
                Label = s.Label,
                SortOrder = j,
                Hidden = s.Hidden,
                Skills = [.. s.Skills],
            })],
        });

    private sealed record CategoryRead(string Key, string Label, int SortOrder, bool Hidden, string? Skills);

    private sealed record SubcategoryRead(string CategoryKey, string Key, string Label, int SortOrder, bool Hidden, string? Skills);
}
