using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using WinnersPortal.Domain;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Services.Auth;
using WinnersPortal.Services.Common;
using WinnersPortal.Services.Opportunities;

namespace WinnersPortal.Services.Admin;

/// <summary>
/// Admin → Categories: the kinds of work, read with what uses each, and
/// saved whole. A key is fixed once created — opportunities and profiles
/// store it with nothing tying it back — so rows are matched by key, a
/// missing key is a removal, and a removal of anything still in use is
/// refused with the advice to hide it instead.
/// </summary>
public sealed partial class CategoryAdminService(AppDbContext db, CategoryCatalog catalog, PublicReads publicReads)
{
    public const int MaxCategories = 30;
    public const int MaxSubcategories = 20;
    public const int MaxSkills = 30;

    public static readonly CategoryLimits Limits = new(
        MaxCategories, MaxSubcategories, MaxSkills, Category.MaxKey, Category.MaxLabel, OpportunityFit.MaxSkillName);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex KeyShape();

    public async Task<Outcome<CategoriesAdminResponse>> ListAsync(CancellationToken ct)
    {
        var stored = await CategoryCatalog.ReadAsync(db, ct);
        var stamps = await db.Categories.AsNoTracking()
            .Select(c => new { c.Key, c.UpdatedAtUtc, c.UpdatedBy })
            .ToDictionaryAsync(c => c.Key, StringComparer.Ordinal, ct);
        var uses = Uses.From(db.UseDapper ? await UsageSqlAsync(db.Sql, ct) : await UsageLinqAsync(db, ct));
        return Outcome.Ok(new CategoriesAdminResponse
        {
            Version = Version(stored),
            Limits = Limits,
            Categories = [.. stored.All.Select(c => new CategoryAdminRow
            {
                Key = c.Key,
                Label = c.Label,
                Hidden = c.Hidden,
                Skills = c.Skills,
                Builtin = c.Key == Taxonomy.OtherKey,
                Uses = uses.Of(c.Key),
                UpdatedAtUtc = stamps.GetValueOrDefault(c.Key)?.UpdatedAtUtc,
                UpdatedBy = stamps.GetValueOrDefault(c.Key)?.UpdatedBy,
                Subcategories = [.. c.Subcategories.Select(s => new SubcategoryAdminRow
                {
                    Key = s.Key,
                    Label = s.Label,
                    Hidden = s.Hidden,
                    Skills = s.Skills,
                    Opportunities = uses.Of(c.Key, s.Key),
                })],
            })],
        });
    }

    public async Task<Outcome<CategoriesAdminResponse>> SaveAsync(
        CategoriesSaveRequest request, ClaimsPrincipal principal, CancellationToken ct)
    {
        if (Clean(request, out var list) is { } problem) return Outcome.Invalid(problem);

        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            var rows = await db.Categories.Include(c => c.Subcategories).ToListAsync(ct);
            if (Version(await CategoryCatalog.ReadAsync(db, ct)) != request.Version)
                return Outcome.Conflict(
                    "Someone else changed the categories since this screen read them. Reload to see their changes, then make yours again.");

            var uses = Uses.From(db.UseDapper ? await UsageSqlAsync(db.Sql, ct) : await UsageLinqAsync(db, ct));
            if (RemovalProblem(rows, list, uses) is { } inUse) return Outcome.Conflict(inUse);

            Apply(rows, list, DateTimeOffset.UtcNow, Principal.Email(principal));
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }

        // Every reader here, every other process over Redis, and the public
        // lists that carry labels: all at once, now the rows are committed.
        await catalog.ChangedAsync(ct);
        publicReads.Clear();
        return await ListAsync(ct);
    }

    // ------------------------------------------------------------ the rules

    /// <summary>
    /// The list as sent, cleaned, or the first thing wrong with it. Skill
    /// names are trimmed and a repeat is dropped, as on a profile; anything a
    /// person typed that cannot be kept as typed is refused with its name.
    /// </summary>
    public static string? Clean(CategoriesSaveRequest request, out List<OpportunityCategory> list)
    {
        list = [];
        var sent = request.Categories?.Where(c => c is not null).Select(c => c!).ToList() ?? [];
        if (sent.Count == 0) return "The list cannot be empty — Something else, at least, stays.";
        if (sent.Count > MaxCategories) return $"At most {MaxCategories} categories: each one is in every AI reading of a brief or a profile.";

        var keys = new HashSet<string>(StringComparer.Ordinal);
        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in sent)
        {
            var label = c.Label?.Trim() ?? "";
            if (Thing("A category", c.Key, label, out var key) is { } wrong) return wrong;
            if (!keys.Add(key)) return $"Two categories share the key {key}.";
            if (!labels.Add(label)) return $"Two categories are both called {label}.";
            if (Skills(label, c.Skills, out var skills) is { } skillProblem) return skillProblem;

            var subsSent = c.Subcategories?.Where(s => s is not null).Select(s => s!).ToList() ?? [];
            if (subsSent.Count > MaxSubcategories) return $"{label}: at most {MaxSubcategories} subcategories.";
            var subKeys = new HashSet<string>(StringComparer.Ordinal);
            var subLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var subs = new List<OpportunitySubcategory>();
            foreach (var s in subsSent)
            {
                var subLabel = s.Label?.Trim() ?? "";
                if (Thing($"A subcategory of {label}", s.Key, subLabel, out var subKey) is { } subWrong) return subWrong;
                if (!subKeys.Add(subKey)) return $"{label}: two subcategories share the key {subKey}.";
                if (!subLabels.Add(subLabel)) return $"{label}: two subcategories are both called {subLabel}.";
                if (Skills($"{label} › {subLabel}", s.Skills, out var subSkills) is { } subSkillProblem) return subSkillProblem;
                subs.Add(new OpportunitySubcategory(subKey, subLabel, subSkills, s.Hidden));
            }
            list.Add(new OpportunityCategory(key, label, subs, skills, c.Hidden));
        }

        var other = list.FindIndex(c => c.Key == Taxonomy.OtherKey);
        if (other < 0) return "Something else (key other) stays on the list: it is where a brief that fits nothing goes.";
        if (other != list.Count - 1) return $"{list[other].Label} stays last.";
        if (list[other].Hidden) return $"{list[other].Label} cannot be hidden: a brief that fits nothing needs somewhere to go.";
        if (list[other].Subcategories.Count > 0) return $"{list[other].Label} takes no subcategories.";
        return null;
    }

    private static string? Thing(string what, string? rawKey, string label, out string key)
    {
        key = OpportunityCategories.CleanKey(rawKey) ?? "";
        if (label.Length == 0) return $"{what} needs a name.";
        if (label.Length > Category.MaxLabel) return $"{label[..20]}…: a name stays under {Category.MaxLabel} characters.";
        if (key.Length == 0) return $"{label} needs a key.";
        if (key.Length > Category.MaxKey || !KeyShape().IsMatch(key))
            return $"{label}: a key is lower-case letters, digits and hyphens, at most {Category.MaxKey} characters.";
        return null;
    }

    private static string? Skills(string owner, List<string?>? sent, out List<string> skills)
    {
        skills = [];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in sent ?? [])
        {
            var name = raw?.Trim() ?? "";
            if (name.Length == 0 || !seen.Add(name)) continue;
            if (name.Length > OpportunityFit.MaxSkillName)
                return $"{owner}: a skill name stays under {OpportunityFit.MaxSkillName} characters.";
            skills.Add(name);
        }
        return skills.Count > MaxSkills ? $"{owner}: at most {MaxSkills} skills." : null;
    }

    /// <summary>
    /// Null when nothing removed is in use; else one sentence naming each
    /// that is. A key is all the stored rows have, so a removed one in use
    /// would leave them pointing at nothing.
    /// </summary>
    internal static string? RemovalProblem(IEnumerable<Category> stored, IReadOnlyList<OpportunityCategory> list, Uses uses)
    {
        var kept = list.ToDictionary(c => c.Key, StringComparer.Ordinal);
        var refused = new List<string>();
        foreach (var row in stored)
        {
            if (!kept.TryGetValue(row.Key, out var now))
            {
                var u = uses.Of(row.Key);
                if (u.InUse()) refused.Add($"{row.Label} ({Describe(u)})");
                continue;
            }
            foreach (var sub in row.Subcategories)
                if (now.Subcategories.All(s => s.Key != sub.Key) && uses.Of(row.Key, sub.Key) is > 0 and var n)
                    refused.Add($"{row.Label} › {sub.Label} ({Count(n, "opportunity", "opportunities")})");
        }
        return refused.Count == 0 ? null
            : $"Still in use, so hide rather than remove: {string.Join("; ", refused)}.";
    }

    private static string Describe(CategoryUses u) => string.Join(", ", new[]
    {
        u.Opportunities > 0 ? Count(u.Opportunities, "opportunity", "opportunities") : null,
        u.Profiles > 0 ? Count(u.Profiles, "profile", "profiles") : null,
        u.Projects > 0 ? Count(u.Projects, "past project", "past projects") : null,
    }.Where(s => s is not null));

    private static string Count(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";

    /// <summary>The rows brought in line with the list: changed ones stamped, new ones added, removed ones gone.</summary>
    internal void Apply(List<Category> rows, IReadOnlyList<OpportunityCategory> list, DateTimeOffset now, string? by)
    {
        var byKey = rows.ToDictionary(r => r.Key, StringComparer.Ordinal);
        var keep = new HashSet<string>(list.Select(c => c.Key), StringComparer.Ordinal);
        foreach (var gone in rows.Where(r => !keep.Contains(r.Key)))
            db.Categories.Remove(gone);

        for (var i = 0; i < list.Count; i++)
        {
            var c = list[i];
            if (!byKey.TryGetValue(c.Key, out var row))
            {
                var fresh = CategoryCatalog.Rows([c], now, by).Single();
                fresh.SortOrder = i;
                db.Categories.Add(fresh);
                continue;
            }

            var changed = row.Label != c.Label || row.SortOrder != i || row.Hidden != c.Hidden
                || !row.Skills.SequenceEqual(c.Skills, StringComparer.Ordinal);
            row.Label = c.Label;
            row.SortOrder = i;
            row.Hidden = c.Hidden;
            if (!row.Skills.SequenceEqual(c.Skills, StringComparer.Ordinal)) row.Skills = [.. c.Skills];

            var subsByKey = row.Subcategories.ToDictionary(s => s.Key, StringComparer.Ordinal);
            var keepSubs = new HashSet<string>(c.Subcategories.Select(s => s.Key), StringComparer.Ordinal);
            foreach (var gone in row.Subcategories.Where(s => !keepSubs.Contains(s.Key)).ToList())
            {
                row.Subcategories.Remove(gone);
                db.Subcategories.Remove(gone);
                changed = true;
            }
            for (var j = 0; j < c.Subcategories.Count; j++)
            {
                var s = c.Subcategories[j];
                if (!subsByKey.TryGetValue(s.Key, out var sub))
                {
                    row.Subcategories.Add(new Subcategory
                    {
                        CategoryKey = c.Key, Key = s.Key, Label = s.Label, SortOrder = j, Hidden = s.Hidden, Skills = [.. s.Skills],
                    });
                    changed = true;
                    continue;
                }
                if (sub.Label == s.Label && sub.SortOrder == j && sub.Hidden == s.Hidden
                    && sub.Skills.SequenceEqual(s.Skills, StringComparer.Ordinal)) continue;
                sub.Label = s.Label;
                sub.SortOrder = j;
                sub.Hidden = s.Hidden;
                if (!sub.Skills.SequenceEqual(s.Skills, StringComparer.Ordinal)) sub.Skills = [.. s.Skills];
                changed = true;
            }

            if (changed)
            {
                row.UpdatedAtUtc = now;
                row.UpdatedBy = by;
            }
        }
    }

    /// <summary>
    /// A short fingerprint of the stored list, order included. A save names
    /// the one it was made from, so the second of two administrators editing
    /// at once is told rather than silently overwriting the first.
    /// </summary>
    public static string Version(Taxonomy stored) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(stored.All))))[..16].ToLowerInvariant();

    // ------------------------------------------------------------ the uses

    internal sealed record OpportunityUse(string Category, string? Subcategory, int Count);

    internal sealed record KeyUse(string Category, int Count);

    internal sealed record UsageReads(
        List<OpportunityUse> Opportunities, List<KeyUse> Primary, List<KeyUse> Secondary, List<KeyUse> Projects);

    /// <summary>The four counts, keyed for the screen and the removal check.</summary>
    internal sealed class Uses
    {
        private readonly Dictionary<string, CategoryUses> _categories = new(StringComparer.Ordinal);
        private readonly Dictionary<(string, string), int> _subcategories = [];

        public static Uses From(UsageReads reads)
        {
            var uses = new Uses();
            CategoryUses Get(string key) => uses._categories.GetValueOrDefault(key) ?? new CategoryUses(0, 0, 0);
            foreach (var o in reads.Opportunities)
            {
                uses._categories[o.Category] = Get(o.Category) with { Opportunities = Get(o.Category).Opportunities + o.Count };
                if (o.Subcategory is { } sub)
                    uses._subcategories[(o.Category, sub)] = uses._subcategories.GetValueOrDefault((o.Category, sub)) + o.Count;
            }
            foreach (var p in reads.Primary.Concat(reads.Secondary))
                uses._categories[p.Category] = Get(p.Category) with { Profiles = Get(p.Category).Profiles + p.Count };
            foreach (var p in reads.Projects)
                uses._categories[p.Category] = Get(p.Category) with { Projects = Get(p.Category).Projects + p.Count };
            return uses;
        }

        public CategoryUses Of(string category) => _categories.GetValueOrDefault(category) ?? new CategoryUses(0, 0, 0);

        public int Of(string category, string subcategory) => _subcategories.GetValueOrDefault((category, subcategory));
    }

    private static Task<UsageReads> UsageSqlAsync(Sql sql, CancellationToken ct) =>
        sql.MultipleAsync(Procedures.CategoryUsage, null, async grid => new UsageReads(
            (await grid.ReadAsync<OpportunityUse>()).ToList(),
            (await grid.ReadAsync<KeyUse>()).ToList(),
            (await grid.ReadAsync<KeyUse>()).ToList(),
            (await grid.ReadAsync<KeyUse>()).ToList()), ct);

    /// <summary>The LINQ beside <c>Category_Usage</c>: what Postgres runs. A profile holds a key at most once, so primary and secondary add up.</summary>
    internal static async Task<UsageReads> UsageLinqAsync(AppDbContext db, CancellationToken ct) => new(
        await OpportunityUsesLinqAsync(db, ct),
        await PrimaryUsesLinqAsync(db, ct),
        await SecondaryUsesLinqAsync(db, ct),
        await ProjectUsesLinqAsync(db, ct));

    internal static Task<List<OpportunityUse>> OpportunityUsesLinqAsync(AppDbContext db, CancellationToken ct) =>
        db.Opportunities.Where(o => o.Category != null)
            .GroupBy(o => new { o.Category, o.Subcategory })
            .Select(g => new OpportunityUse(g.Key.Category!, g.Key.Subcategory, g.Count()))
            .ToListAsync(ct);

    internal static Task<List<KeyUse>> PrimaryUsesLinqAsync(AppDbContext db, CancellationToken ct) =>
        db.Profiles.Where(p => p.PrimaryCategory != null)
            .GroupBy(p => p.PrimaryCategory)
            .Select(g => new KeyUse(g.Key!, g.Count()))
            .ToListAsync(ct);

    /// <summary>
    /// The secondary lists come back whole and are counted here: EF Core
    /// cannot flatten the profile's text[] column in SQL. Three short keys
    /// a profile at most, read only for this screen.
    /// </summary>
    internal static async Task<List<KeyUse>> SecondaryUsesLinqAsync(AppDbContext db, CancellationToken ct) =>
        [.. (await db.Profiles.Select(p => p.SecondaryCategories).ToListAsync(ct))
            .SelectMany(keys => keys)
            .GroupBy(k => k, StringComparer.Ordinal)
            .Select(g => new KeyUse(g.Key, g.Count()))];

    internal static Task<List<KeyUse>> ProjectUsesLinqAsync(AppDbContext db, CancellationToken ct) =>
        db.ProfileProjects.Where(pp => pp.Category != null && db.Profiles.Any(p => p.UserId == pp.UserId))
            .GroupBy(pp => pp.Category)
            .Select(g => new KeyUse(g.Key!, g.Count()))
            .ToListAsync(ct);
}
