namespace WinnersPortal.Domain;

/// <summary>
/// One kind of work on the list an administrator keeps (Admin → Categories).
/// The key is what opportunities and profiles store, with nothing tying it
/// back here, so it is fixed once created: the label is what changes, and a
/// kind still in use is hidden rather than deleted. The portal reads the whole
/// list into memory at start and after every save; nothing queries it per
/// request.
/// </summary>
public sealed class Category
{
    // Column limits: the one place a length is stated; the rules and the
    // DbContext both read it here.
    public const int MaxKey = Opportunity.MaxCategoryKey;
    public const int MaxLabel = 80;
    public const int MaxUpdatedBy = 320;

    public required string Key { get; set; }
    public required string Label { get; set; }

    /// <summary>Where it sits in every picker; "Something else" is always last.</summary>
    public int SortOrder { get; set; }

    /// <summary>Kept for what already chose it, offered to nothing new.</summary>
    public bool Hidden { get; set; }

    /// <summary>Skill names offered to a member who works in this kind; they decide nothing.</summary>
    public List<string> Skills { get; set; } = [];

    public DateTimeOffset UpdatedAtUtc { get; set; }

    /// <summary>The administrator who last saved the list with a change to this row.</summary>
    public string? UpdatedBy { get; set; }

    public List<Subcategory> Subcategories { get; set; } = [];
}

/// <summary>A narrower kind under one <see cref="Category"/>; its key is unique only under that parent.</summary>
public sealed class Subcategory
{
    public required string CategoryKey { get; set; }
    public required string Key { get; set; }
    public required string Label { get; set; }
    public int SortOrder { get; set; }
    public bool Hidden { get; set; }

    /// <summary>What the opportunity form offers first once a client picks this one.</summary>
    public List<string> Skills { get; set; } = [];
}
