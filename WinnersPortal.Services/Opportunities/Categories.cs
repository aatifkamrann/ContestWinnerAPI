using WinnersPortal.Domain;

namespace WinnersPortal.Services.Opportunities;

/// <summary>
/// One kind of work an opportunity can be — a key that is stored and filtered on,
/// and a label people read. What decides which kind a brief is, and which
/// kinds a freelancer works in, is no longer a word list on this server: a
/// model reads the brief and the skills against this taxonomy.
///
/// <see cref="OpportunityCategory.Skills"/> is not that word list coming back. It
/// decides nothing and matches nothing — it is the vocabulary a member is
/// offered once they have said, themselves, which kind of work they do, so
/// that one skill is spelt one way across the portal and a profile does not
/// start from an empty box. What a kind of work is built with is a fact about
/// the trade, and a model call to be told that Android is written in Kotlin
/// would be a call spent on nothing. A subcategory's
/// <see cref="OpportunitySubcategory.Skills"/> are the same kind of list, narrower:
/// what the opportunity form offers first once a client has picked one.
///
/// <see cref="OpportunityCategory.Hidden"/> retires a kind without losing it:
/// nothing new may choose it, and everything that already did keeps its
/// label, its board and its fit.
/// </summary>
public sealed record OpportunitySubcategory(string Key, string Label, IReadOnlyList<string> Skills, bool Hidden = false);

public sealed record OpportunityCategory(
    string Key,
    string Label,
    IReadOnlyList<OpportunitySubcategory> Subcategories,
    IReadOnlyList<string> Skills,
    bool Hidden = false);

/// <summary>
/// One whole list of categories, fixed once built. The portal holds one at a
/// time (<see cref="OpportunityCategories.Current"/>) and swaps it for a new
/// one when an administrator saves the list, so a reader never sees half an
/// edit.
/// </summary>
public sealed class Taxonomy
{
    /// <summary>"Something else": the escape hatch every list keeps, last, and nobody's speciality.</summary>
    public const string OtherKey = "other";

    /// <summary>The list the portal ships with.</summary>
    public static Taxonomy Default { get; } = new(DefaultCategories.All);

    private readonly Dictionary<string, OpportunityCategory> _byKey;

    public Taxonomy(IEnumerable<OpportunityCategory> categories)
    {
        All = [.. categories];
        _byKey = All.ToDictionary(c => c.Key, StringComparer.Ordinal);
        Offered = [.. All.Where(c => !c.Hidden)];
        Keys = Offered.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Every category, hidden ones included, in the order they are offered.</summary>
    public IReadOnlyList<OpportunityCategory> All { get; }

    /// <summary>The categories something new may choose: all but the hidden.</summary>
    public IReadOnlyList<OpportunityCategory> Offered { get; }

    /// <summary>
    /// Every key the model may answer with, in the order they are offered.
    /// A reading that names anything else is rejected rather than mapped:
    /// a category that is not in this list is not a category.
    /// </summary>
    public IReadOnlySet<string> Keys { get; }

    /// <summary>A category by key, hidden or not: labels and fit still need a retired one.</summary>
    public OpportunityCategory? Find(string? key) =>
        key is null ? null : _byKey.GetValueOrDefault(key.Trim().ToLowerInvariant());

    public static OpportunitySubcategory? FindSub(OpportunityCategory category, string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var k = key.Trim().ToLowerInvariant();
        return category.Subcategories.FirstOrDefault(s => s.Key == k);
    }

    /// <summary>Hidden itself, or under a hidden category.</summary>
    public static bool IsHidden(OpportunityCategory category, OpportunitySubcategory? sub) =>
        category.Hidden || sub?.Hidden == true;

    /// <summary>Only the kinds an opportunity can actually be judged against — "something else" is nobody’s speciality.</summary>
    public static bool Judgeable(OpportunityCategory category) => category.Key != OtherKey;

    /// <summary>
    /// Null when the pair is acceptable on a draft, else the message the form
    /// shows. A draft may carry no category at all; publish is where one
    /// becomes required (<see cref="PublishProblem"/>). A hidden category or
    /// subcategory is accepted only where the draft already held it
    /// (<paramref name="keptCategory"/>, <paramref name="keptSubcategory"/>):
    /// retiring one must not make somebody's draft unsaveable, and must not
    /// let anything new choose it either.
    /// </summary>
    public string? Problem(string? category, string? subcategory,
        string? keptCategory = null, string? keptSubcategory = null)
    {
        if (string.IsNullOrWhiteSpace(category))
            return string.IsNullOrWhiteSpace(subcategory) ? null : "Pick a category before a subcategory.";
        var c = Find(category);
        if (c is null) return "Pick a category from the list.";
        if (c.Hidden && c.Key != OpportunityCategories.CleanKey(keptCategory))
            return $"{c.Label} is no longer offered — pick another category.";
        if (string.IsNullOrWhiteSpace(subcategory)) return null;
        var s = FindSub(c, subcategory);
        if (s is null) return $"That subcategory is not under {c.Label} — pick one from its list.";
        if (s.Hidden && (c.Key != OpportunityCategories.CleanKey(keptCategory)
                         || s.Key != OpportunityCategories.CleanKey(keptSubcategory)))
            return $"{s.Label} is no longer offered — pick another subcategory, or none.";
        return null;
    }

    /// <summary>
    /// What publishing adds: a category is required, and neither it nor the
    /// subcategory may be one that is no longer offered — entrants browse by
    /// what is on the list.
    /// </summary>
    public string? PublishProblem(string? category, string? subcategory = null)
    {
        var c = Find(category);
        if (c is null) return "Pick a category before publishing — it is how entrants browse for this opportunity.";
        if (c.Hidden) return $"{c.Label} is no longer offered — pick another category before publishing.";
        if (string.IsNullOrWhiteSpace(subcategory)) return null;
        var s = FindSub(c, subcategory);
        if (s is null) return $"That subcategory is not under {c.Label} — pick one from its list.";
        return s.Hidden ? $"{s.Label} is no longer offered — pick another subcategory, or none, before publishing." : null;
    }
}

/// <summary>
/// The taxonomy the browse page filters by and the opportunity form fills from,
/// read by everything that knows a kind of work: the form, the filter, the
/// suggestion, the fit, the boards. The list itself is rows an administrator
/// edits (Admin → Categories); <c>CategoryCatalog</c> loads them at start and
/// after every save and hands the result to <see cref="Use"/>. Until then — and
/// in tests, which never call it — it is <see cref="Taxonomy.Default"/>.
///
/// A key is fixed once created: it is stored on opportunities and profiles
/// with nothing tying it back here, so a label is what changes and a kind in
/// use is hidden rather than deleted.
/// </summary>
public static class OpportunityCategories
{
    public const int MaxKey = Opportunity.MaxCategoryKey;

    public const string OtherKey = Taxonomy.OtherKey;

    private static volatile Taxonomy _current = Taxonomy.Default;

    /// <summary>The list as it stands; take it once when one piece of work needs several reads to agree.</summary>
    public static Taxonomy Current => _current;

    /// <summary>Swaps the whole list at once. Only the catalog calls this.</summary>
    internal static void Use(Taxonomy taxonomy) => _current = taxonomy;

    /// <inheritdoc cref="Taxonomy.All"/>
    public static IReadOnlyList<OpportunityCategory> All => _current.All;

    /// <inheritdoc cref="Taxonomy.Offered"/>
    public static IReadOnlyList<OpportunityCategory> Offered => _current.Offered;

    /// <inheritdoc cref="Taxonomy.Keys"/>
    public static IReadOnlySet<string> Keys => _current.Keys;

    public static OpportunityCategory? Find(string? key) => _current.Find(key);

    public static OpportunitySubcategory? FindSub(OpportunityCategory category, string? key) =>
        Taxonomy.FindSub(category, key);

    /// <inheritdoc cref="Taxonomy.Problem"/>
    public static string? Problem(string? category, string? subcategory,
        string? keptCategory = null, string? keptSubcategory = null) =>
        _current.Problem(category, subcategory, keptCategory, keptSubcategory);

    /// <inheritdoc cref="Taxonomy.PublishProblem"/>
    public static string? PublishProblem(string? category, string? subcategory = null) =>
        _current.PublishProblem(category, subcategory);

    /// <summary>The stored form of a key: trimmed, lower-case, null for blank.</summary>
    public static string? CleanKey(string? key) =>
        string.IsNullOrWhiteSpace(key) ? null : key.Trim().ToLowerInvariant();

    /// <inheritdoc cref="Taxonomy.Judgeable"/>
    public static bool Judgeable(OpportunityCategory category) => Taxonomy.Judgeable(category);
}
