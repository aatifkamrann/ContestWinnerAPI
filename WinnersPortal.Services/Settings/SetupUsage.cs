using WinnersPortal.Services.Storage;
using Microsoft.EntityFrameworkCore;
using WinnersPortal.Infrastructure.Data;

namespace WinnersPortal.Services.Settings;

/// <summary>
/// What still lives in a setup, for the two kinds where something does: a
/// store holds files, an organization holds repositories. Making another
/// setup active is always allowed — new work goes there and what is here
/// stays reachable — but removing it would strand that work, so the
/// settings screen shows the count and the save refuses the removal.
/// Email, phone messages and AI providers keep nothing, and are never in use.
/// </summary>
public static partial class SetupUsage
{
    /// <summary>Setup id → what it holds, in words ("12 files"); a setup holding nothing is absent.</summary>
    public static async Task<Dictionary<string, string>> InUseAsync(
        AppDbContext db, SetupKind kind, IReadOnlyList<SetupValues> setups, CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (kind == Setups.Storage)
        {
            var rows = db.UseDapper ? await FileCountsSqlAsync(db.Sql, ct) : await FileCountsLinqAsync(db, ct);
            foreach (var (id, files) in FilesBySetup(rows))
                result[id] = files == 1 ? "1 file" : $"{files} files";
        }
        else if (kind == Setups.GitHub)
        {
            var repos = db.UseDapper
                ? await ReposSqlAsync(db.Sql, ct)
                : await db.Entries.Where(e => e.RepoFullName != null).Select(e => e.RepoFullName!).ToListAsync(ct);
            foreach (var (id, text) in ReposBySetup(setups, repos))
                result[id] = text;
        }
        return result;
    }

    public sealed record SetupCount(string? Setup, int Count);

    internal static async Task<List<(string? Setup, int Count)>> FileCountsLinqAsync(AppDbContext db, CancellationToken ct)
    {
        var rows = new List<(string? Setup, int Count)>();
        foreach (var query in FileCounts(db))
            rows.AddRange((await query.ToListAsync(ct)).Select(r => (r.Setup, r.Count)));
        return rows;
    }

    /// <summary>The three kinds of row that name a store — brief attachments, entry uploads, packaged ZIPs — counted per store.</summary>
    internal static IQueryable<SetupCount>[] FileCounts(AppDbContext db) =>
    [
        db.Attachments.GroupBy(a => a.StorageSetup).Select(g => new SetupCount(g.Key, g.Count())),
        db.Submissions.GroupBy(s => s.StorageSetup).Select(g => new SetupCount(g.Key, g.Count())),
        db.Entries.Where(e => e.ZipStorageKey != null).GroupBy(e => e.ZipStorageSetup).Select(g => new SetupCount(g.Key, g.Count())),
    ];

    /// <summary>Pure: file counts per setup, a row with no setup counting for the main one.</summary>
    public static Dictionary<string, int> FilesBySetup(IEnumerable<(string? Setup, int Count)> rows)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (setup, count) in rows)
        {
            var id = Storage.StorageService.SetupOf(setup);
            counts[id] = counts.GetValueOrDefault(id) + count;
        }
        return counts.Where(c => c.Value > 0).ToDictionary(c => c.Key, c => c.Value, StringComparer.Ordinal);
    }

    /// <summary>Pure: for each setup, the repositories still owned by its organization, in words.</summary>
    public static Dictionary<string, string> ReposBySetup(IEnumerable<SetupValues> setups, IEnumerable<string> repoFullNames)
    {
        var byOwner = repoFullNames
            .Select(n => n.Split('/', 2)[0])
            .GroupBy(o => o, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var setup in setups)
        {
            var org = setup.Get("github.organization")?.Trim();
            if (string.IsNullOrEmpty(org) || !byOwner.TryGetValue(org, out var count)) continue;
            result[setup.Id] = count == 1 ? $"1 repository in {org}" : $"{count} repositories in {org}";
        }
        return result;
    }

    /// <summary>
    /// Why a save that removes setups may not, or null when it may: the
    /// first removed setup that still holds work, named with what it holds.
    /// </summary>
    public static async Task<string?> RemovalProblemAsync(
        AppDbContext db, SettingsService settings, IReadOnlyDictionary<string, string?> updates, CancellationToken ct)
    {
        foreach (var kind in Setups.Kinds.Where(k => k == Setups.Storage || k == Setups.GitHub))
        {
            if (!updates.TryGetValue(kind.ListKey, out var listed)) continue;
            var after = string.IsNullOrEmpty(listed) ? Setups.Initial : Setups.Read(listed).List;
            if (after is null) continue; // not a list at all; the save itself says so
            var before = await settings.SetupsAsync(kind, ct);
            var removed = before.Where(b => after.All(a => a.Id != b.Id)).ToList();
            if (removed.Count == 0) continue;

            var inUse = await InUseAsync(db, kind, before, ct);
            foreach (var gone in removed)
            {
                if (inUse.TryGetValue(gone.Id, out var what))
                    return $"“{gone.Name}” still holds {what}, so it cannot be removed. "
                        + "Leave it inactive instead: nothing new goes there, and what is there stays reachable.";
            }
        }
        return null;
    }
}
