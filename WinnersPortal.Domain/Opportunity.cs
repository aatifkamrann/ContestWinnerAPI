namespace WinnersPortal.Domain;

public enum OpportunityStatus
{
    /// <summary>Visible only to its client; freely editable.</summary>
    Draft = 0,

    /// <summary>Published: listed in the feed, open for entry until the deadline.</summary>
    Open = 1,

    /// <summary>Deadline passed; the client is reading code. (Set by phase three's freeze.)</summary>
    Reviewing = 2,

    /// <summary>Winner announced and recorded.</summary>
    Awarded = 3,

    /// <summary>Cancelled before a winner; entries are void.</summary>
    Cancelled = 4,
}

/// <summary>
/// How entrants hand their work in. A per-opportunity choice the client makes
/// while drafting and cannot change after publishing, because entrants
/// commit to it: a repository is the right shape for code, a file upload
/// for a logo, a document, a deck — and an opportunity may ask for both.
/// </summary>
[Flags]
public enum OpportunityDelivery
{
    /// <summary>One private GitHub repository per entrant; milestones claimed by tag or pull request.</summary>
    Repository = 1,

    /// <summary>Files uploaded to the opportunity page; milestones claimed by tagging an upload.</summary>
    Upload = 2,

    Both = Repository | Upload,
}

/// <summary>
/// How an opportunity pays, chosen while drafting and frozen at publish.
/// </summary>
public enum OpportunityKind
{
    /// <summary>Several entrants build in parallel; the client announces one winner and pays the award after review.</summary>
    Competitive = 0,

    /// <summary>
    /// The client hires one applicant, who works through the milestones in
    /// order; each milestone is approved and paid its own amount before the
    /// next one opens, and the last payment completes the award.
    /// </summary>
    Milestones = 1,
}

/// <summary>
/// A fixed-award opportunity: one brief, one award, work delivered as a
/// private repo per entrant or as uploaded files (<see cref="Delivery"/>).
/// Competitive: open entry, and the client reviews before paying one
/// winner. Paid by milestone (<see cref="Kind"/>): one hired freelancer,
/// paid milestone by milestone.
/// </summary>
public sealed class Opportunity
{
    // Column limits: the one place a length is stated; the rules and the
    // DbContext both read it here.
    public const int MaxCategoryKey = 40;
    public const int MaxRubricTitle = 60;
    public const int MaxRubricDetail = 240;

    public Guid Id { get; set; }

    /// <summary>URL identity, generated from the title; unique.</summary>
    public required string Slug { get; set; }

    public required string Title { get; set; }
    public string BriefMarkdown { get; set; } = "";

    // ---- what kind of work, and who it is for ----------------------------
    // Classification for the browse page and the entry door. The category is
    // a key from OpportunityCategories.All, required to publish, so "by category"
    // on the browse page is never a filter over a list that half the
    // opportunities skipped. Required skills and the minimum merit score are the
    // door: an entrant short of either is refused with the reason, before
    // they stake a week on a brief they were never going to be picked for.

    /// <summary>A category key from <c>OpportunityCategories.All</c>; null only on a draft.</summary>
    public string? Category { get; set; }

    /// <summary>A subcategory key under <see cref="Category"/>, or null.</summary>
    public string? Subcategory { get; set; }

    /// <summary>
    /// The merit score an entrant needs, 0–100; zero is no minimum and the
    /// default. Checked at the door, so it is a term of the deal like the
    /// award, and frozen with it.
    /// </summary>
    public int MinMeritScore { get; set; }

    /// <summary>Skills an entrant's profile must list to enter. Ordered by the client; frozen at publish.</summary>
    public List<OpportunitySkill> Skills { get; set; } = [];

    // ---- the terms beside the brief ---------------------------------------
    // What the work must be built with, and how it is scored: two lists the
    // page shows on tabs of their own, in the client's order, frozen at
    // publish with the brief they qualify. Both may be empty — a logo
    // opportunity has no language, and a client may score by eye.

    /// <summary>The technical requirements: "Language — Python 3.11+", one row each.</summary>
    public List<OpportunityRequirement> Requirements { get; set; } = [];

    /// <summary>The scoring rubric: points per criterion, totalling whatever the client made them total.</summary>
    public List<OpportunityCriterion> Criteria { get; set; } = [];

    /// <summary>
    /// The fixed award, in <see cref="Currency"/>. Validated against
    /// opportunity.minAwardUsd at publish. Paid by milestone, it is the
    /// total the milestones' amounts must add up to.
    /// </summary>
    public decimal AwardAmount { get; set; }

    /// <summary>Competitive or paid by milestone. Set while drafting; frozen at publish.</summary>
    public OpportunityKind Kind { get; set; } = OpportunityKind.Competitive;

    public string Currency { get; set; } = "USD";

    public OpportunityStatus Status { get; set; } = OpportunityStatus.Draft;

    /// <summary>Repository, upload, or both. Set while drafting; frozen at publish with the rest of the terms.</summary>
    public OpportunityDelivery Delivery { get; set; } = OpportunityDelivery.Repository;

    /// <summary>
    /// Entries must run with Docker Compose: every milestone claim is built
    /// from the repository's root compose file on the build host, and the
    /// board says whether it built. Only with a repository delivery; frozen
    /// at publish with the rest of the terms.
    /// </summary>
    public bool RequiresCompose { get; set; }

    /// <summary>
    /// The competition start date — a day, held as 00:00 UTC on it.
    /// Information, not a gate: entry opens the moment an opportunity is
    /// published whatever this says, so a client can take entrants, settle
    /// the field before the last joining date and have the work start here.
    /// What it decides is the opportunity duration — start to deadline — and
    /// where a blank deadline counts from. Null, today, or a day gone by at
    /// publish all mean the moment it is published. Before the deadline;
    /// frozen at publish.
    /// </summary>
    public DateTimeOffset? StartsAtUtc { get; set; }

    /// <summary>The build ends and the board freezes here. Required to publish; immutable after.</summary>
    public DateTimeOffset? DeadlineUtc { get; set; }

    /// <summary>
    /// The last joining date: no new entry after this. Null means entry runs
    /// to <see cref="DeadlineUtc"/>, which is what every opportunity did before
    /// the two dates were separated — so null is not "unset", it is the
    /// default the whole portal was built on. Never later than the deadline.
    /// </summary>
    public DateTimeOffset? EntryCloseUtc { get; set; }

    public Guid ClientId { get; set; }
    public User? Client { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? PublishedAtUtc { get; set; }

    // ---- cancellation — the one exit that is not a winner ---------------
    // Both stay public: the reason is sent to every entrant and shown on the
    // page, and the cancellation itself joins the client's track record.

    public DateTimeOffset? CancelledAtUtc { get; set; }

    /// <summary>Why the client called it off. Required to cancel; never edited after.</summary>
    public string? CancelReason { get; set; }

    // ---- search metadata for the public page (phase four's SEO drafts) --
    // Presentation, not opportunity terms: drafted by AI when asked, always
    // edited and saved by the client, rendered into the page head.

    /// <summary>Overrides the title in search results; null falls back to <see cref="Title"/>.</summary>
    public string? MetaTitle { get; set; }

    /// <summary>The meta description search engines show; null falls back to a generated line.</summary>
    public string? MetaDescription { get; set; }

    // ---- feed counters — maintained on write, shown on every card -------
    // The blueprint's performance rule: counting entrants per card is the
    // classic marketplace killer. These are recomputed from truth (never
    // incremented) by Recount at every write site, so a race of two joins
    // cannot drift them and a missed write heals on the next one.

    /// <summary>Entries with <see cref="EntryStatus.Active"/>. Written on enter and withdraw.</summary>
    public int ActiveEntryCount { get; set; }

    /// <summary>Written where milestones are — the draft save. Frozen with them at publish.</summary>
    public int MilestoneCount { get; set; }

    public List<Milestone> Milestones { get; set; } = [];
    public List<Entry> Entries { get; set; } = [];

    // The full-text search column (a generated tsvector over Title and
    // BriefMarkdown, GIN-indexed) is a shadow property configured in
    // AppDbContext: the domain does not carry a provider type for a column
    // nothing in it ever reads or sets.
}

/// <summary>
/// One skill an opportunity asks of its entrants. Matched against a profile's
/// skill names by <see cref="Key"/> — the name folded the way the profile's
/// own duplicate rule folds it — so "React native" on a profile satisfies
/// "React Native" on an opportunity.
/// </summary>
public sealed class OpportunitySkill
{
    // Column limits: the one place a length is stated; the rules and the
    // DbContext both read it here.
    public const int MaxName = 60;

    public Guid Id { get; set; }
    public Guid OpportunityId { get; set; }

    /// <summary>Position in the list, 0-based — the client's own ordering.</summary>
    public int Order { get; set; }

    /// <summary>As the client typed it, tidied.</summary>
    public required string Name { get; set; }

    /// <summary>Lower-case, single-spaced — the form a profile skill is compared in.</summary>
    public required string Key { get; set; }
}

/// <summary>
/// One technical requirement of an opportunity: the name of the constraint and
/// what it asks for. Shown as a table on the opportunity page; matched against
/// nothing — it is the brief's small print, not the entry door.
/// </summary>
public sealed class OpportunityRequirement
{
    public Guid Id { get; set; }
    public Guid OpportunityId { get; set; }

    /// <summary>Position in the table, 0-based — the client's own ordering.</summary>
    public int Order { get; set; }

    /// <summary>The constraint: "Language", "Frameworks", "Serving".</summary>
    public required string Title { get; set; }

    /// <summary>What it asks for: "Python 3.11+".</summary>
    public required string Detail { get; set; }
}

/// <summary>
/// One line of an opportunity's scoring rubric. The points are the client's
/// own scale — the page adds them up and calls the sum the rubric's total —
/// and the description says how the line is judged. Shown, not computed:
/// the client still chooses the winner, and this is what they promised to
/// choose by.
/// </summary>
public sealed class OpportunityCriterion
{
    public Guid Id { get; set; }
    public Guid OpportunityId { get; set; }

    /// <summary>Position in the rubric, 0-based — the client's own ordering.</summary>
    public int Order { get; set; }

    /// <summary>1–100.</summary>
    public int Points { get; set; }

    /// <summary>"Functionality", "Craft &amp; quality".</summary>
    public required string Title { get; set; }

    /// <summary>"All requirements met and working end-to-end."</summary>
    public string? Description { get; set; }
}
