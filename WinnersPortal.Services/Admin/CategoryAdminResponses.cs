namespace WinnersPortal.Services.Admin;

/// <summary>
/// The kinds of work as Admin → Categories edits them: the whole list,
/// hidden ones included, with what uses each, and the version a save must
/// name so that two administrators cannot overwrite each other unseen.
/// </summary>
public sealed record CategoriesAdminResponse
{
    public required string Version { get; init; }
    public required List<CategoryAdminRow> Categories { get; init; }
    public required CategoryLimits Limits { get; init; }
}

public sealed record CategoryAdminRow
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public required bool Hidden { get; init; }
    public required IReadOnlyList<string> Skills { get; init; }
    public required List<SubcategoryAdminRow> Subcategories { get; init; }
    public required CategoryUses Uses { get; init; }

    /// <summary>"Something else": relabelled, never removed, hidden, moved or given subcategories.</summary>
    public required bool Builtin { get; init; }

    public DateTimeOffset? UpdatedAtUtc { get; init; }
    public string? UpdatedBy { get; init; }
}

public sealed record SubcategoryAdminRow
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public required bool Hidden { get; init; }
    public required IReadOnlyList<string> Skills { get; init; }

    /// <summary>Opportunities filed under it, drafts included.</summary>
    public required int Opportunities { get; init; }
}

/// <summary>What stores a key, so the screen can say why one cannot be removed.</summary>
public sealed record CategoryUses(int Opportunities, int Profiles, int Projects)
{
    public bool InUse() => Opportunities + Profiles + Projects > 0;
}

/// <summary>The caps the screen enforces as you type; the save enforces them again.</summary>
public sealed record CategoryLimits(
    int MaxCategories, int MaxSubcategories, int MaxSkills, int MaxKey, int MaxLabel, int MaxSkillName);

/// <summary>The whole list as the screen sends it back, in its new order.</summary>
public sealed record CategoriesSaveRequest
{
    public string? Version { get; init; }
    public List<CategoryInput?>? Categories { get; init; }
}

public sealed record CategoryInput
{
    public string? Key { get; init; }
    public string? Label { get; init; }
    public bool Hidden { get; init; }
    public List<string?>? Skills { get; init; }
    public List<SubcategoryInput?>? Subcategories { get; init; }
}

public sealed record SubcategoryInput
{
    public string? Key { get; init; }
    public string? Label { get; init; }
    public bool Hidden { get; init; }
    public List<string?>? Skills { get; init; }
}
