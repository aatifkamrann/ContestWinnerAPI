namespace WinnersPortal.Services.Ai;

/// <summary>
/// The spam and duplicate scan — the one AI feature that runs entirely on
/// this server: file-tree overlap plus simple heuristics, no model provider,
/// no quota. Its output is a set of flags for the client to look at; nothing
/// here rejects an entry, ever.
/// </summary>
public static class SpamRules
{
    /// <summary>Tree overlap at or above this reads as copied work, not coincidence.</summary>
    public const double DuplicateThreshold = 0.85;

    /// <summary>
    /// Below this many meaningful files, high overlap proves nothing — every
    /// entry starts from the same seeded repository.
    /// </summary>
    public const int MinFilesForDuplicate = 5;

    /// <summary>One opportunity entry as the scan sees it. Null paths = no repository visible.</summary>
    public sealed record Entry(Guid EntryId, string Entrant, int PushCount, IReadOnlyList<string>? TreePaths);

    public sealed record Flag(Guid EntryId, string Entrant, IReadOnlyList<string> Reasons);

    /// <summary>Scaffolding every repo shares; ignored when judging effort and overlap.</summary>
    public static bool IsMeaningful(string path)
    {
        var name = path.Replace('\\', '/');
        var leaf = name[(name.LastIndexOf('/') + 1)..].ToLowerInvariant();
        return leaf is not ("" or ".gitignore" or ".gitattributes")
            && !leaf.StartsWith("readme")
            && !leaf.StartsWith("license")
            && !leaf.StartsWith("licence");
    }

    /// <summary>Plain Jaccard over two path sets — local similarity, no model needed.</summary>
    public static double Jaccard(IReadOnlyCollection<string> a, IReadOnlyCollection<string> b)
    {
        if (a.Count == 0 && b.Count == 0) return 0;
        var setA = new HashSet<string>(a, StringComparer.Ordinal);
        var intersection = b.Count(setA.Contains);
        var union = setA.Count + b.Distinct().Count() - intersection;
        return union == 0 ? 0 : (double)intersection / union;
    }

    public static List<Flag> Scan(IReadOnlyList<Entry> entries)
    {
        var meaningful = entries.ToDictionary(
            e => e.EntryId,
            e => e.TreePaths?.Where(IsMeaningful).Distinct(StringComparer.Ordinal).ToList());

        var flags = new List<Flag>();
        foreach (var e in entries)
        {
            var reasons = new List<string>();
            if (e.PushCount == 0)
                reasons.Add("No pushes — the repository is as the portal seeded it.");
            else if (meaningful[e.EntryId] is { Count: 0 })
                reasons.Add("Only a README and scaffolding — no work files in the repository.");

            var mine = meaningful[e.EntryId];
            if (mine is { Count: >= MinFilesForDuplicate })
            {
                foreach (var other in entries)
                {
                    if (other.EntryId == e.EntryId) continue;
                    var theirs = meaningful[other.EntryId];
                    if (theirs is not { Count: >= MinFilesForDuplicate }) continue;
                    var overlap = Jaccard(mine, theirs);
                    if (overlap >= DuplicateThreshold)
                        reasons.Add($"File layout is {overlap:P0} identical to {other.Entrant}'s entry — worth comparing by hand.");
                }
            }

            if (reasons.Count > 0)
                flags.Add(new Flag(e.EntryId, e.Entrant, reasons));
        }
        return flags;
    }
}
