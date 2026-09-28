using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WinnersPortal.Api.Common;
using WinnersPortal.Services.Opportunities;
using WinnersPortal.Services.Profiles;

namespace WinnersPortal.Api.Opportunities;

/// <summary>The HTTP edge of <see cref="OpportunityService"/>: binds the request, answers with its outcome.</summary>
[ApiController]
public sealed class OpportunityController(OpportunityService opportunities) : ControllerBase
{
    // ------------------------------------------------------ public feed
    [HttpGet("api/opportunities")]
    public async Task<IResult> GetOpportunities(string? status, string? q, string? category, string? subcategory, string? sort, bool? fit, string? cursor, int? limit, CancellationToken ct) =>
        (await opportunities.FeedAsync(status, q, category, subcategory, sort, fit, cursor, limit, User, ct)).ToResult();

    // ------------------------------------------------------ the figures
    // What the Opportunities page says over the list: how many opportunities
    // are open for entry, what they add up to, and how many close within
    // the window that turns a card's pulse orange. Public, as the feed is.
    [HttpGet("api/opportunities/stats")]
    public async Task<IResult> GetStats(CancellationToken ct) =>
        (await opportunities.StatsAsync(ct)).ToResult();

    // The taxonomy the form fills from and the Opportunities page filters by.
    // What decides which kind a brief is lives in a model now, not in
    // a word list on this server, and the skills each kind carries are
    // not that word list coming back: they decide nothing, and are the
    // names offered to a member who has already said which kind of work
    // is theirs. Nobody’s data, so anonymous, and cacheable for as long
    // as the build.
    [HttpGet("api/opportunities/categories")]
    public IResult GetCategories() =>
        Results.Ok(new OpportunityCategoriesResponse
    {
        Categories = OpportunityCategories.All.Select(c => new CategoryOption
        {
            Key = c.Key,
            Label = c.Label,
            Subcategories = c.Subcategories.Select(s => new SubcategoryOption(s.Key, s.Label, s.Skills)),
            Skills = c.Skills,
        }),
        MaxSkills = OpportunityFit.MaxSkills,
        MaxSkillName = OpportunityFit.MaxSkillName,
        MaxMerit = Merit.Max,
        MaxSecondaryCategories = ProfileRules.MaxSecondaryCategories,
    });

    // -------------------------------------------- the client's own list
    [HttpGet("api/opportunities/mine")]
    [Authorize(Policy = "client")]
    public async Task<IResult> GetMine(CancellationToken ct) =>
        (await opportunities.MineAsync(User, ct)).ToResult();

    // ------------------------------------------------------ detail page
    [HttpGet("api/opportunities/{slug}")]
    public async Task<IResult> GetOpportunitiesBySlug(string slug, CancellationToken ct) =>
        (await opportunities.DetailAsync(slug, User, ct)).ToResult();

    // ------------------------------------------------------------ draft
    [HttpPost("api/opportunities")]
    [Authorize(Policy = "client")]
    public async Task<IResult> PostOpportunities(OpportunityUpsertRequest request, CancellationToken ct) =>
        (await opportunities.CreateAsync(request, Publishing(HttpContext), User, ct)).ToResult();

    [HttpPut("api/opportunities/{id:guid}")]
    [Authorize(Policy = "client")]
    public async Task<IResult> PutOpportunities(Guid id, OpportunityUpsertRequest request, CancellationToken ct) =>
        (await opportunities.UpdateAsync(id, request, Publishing(HttpContext), User, ct)).ToResult();

    // ---------------------------------------------------------- publish
    [HttpPost("api/opportunities/{id:guid}/publish")]
    [Authorize(Policy = "client")]
    public async Task<IResult> PostPublish(Guid id, CancellationToken ct) =>
        (await opportunities.PublishAsync(id, User, ct)).ToResult();

    /// <summary>
    /// Whether this save is the one the editor's Publish button makes on
    /// its way to publishing — said in the query, read only to name the
    /// activity row. A save is a draft either way.
    /// </summary>
    private static bool Publishing(HttpContext http) =>
        string.Equals(http.Request.Query["intent"], "publish", StringComparison.OrdinalIgnoreCase);
}

/// <summary>The taxonomy and the limits the opportunity form and the Opportunities filters read.</summary>
public sealed record OpportunityCategoriesResponse
{
    public required IEnumerable<CategoryOption> Categories { get; init; }
    public required int MaxSkills { get; init; }
    public required int MaxSkillName { get; init; }
    public required int MaxMerit { get; init; }
    public required int MaxSecondaryCategories { get; init; }
}

/// <summary>One kind of work, its narrower kinds, and the skills offered with it.</summary>
public sealed record CategoryOption
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public required IEnumerable<SubcategoryOption> Subcategories { get; init; }
    public required IReadOnlyList<string> Skills { get; init; }
}

/// <summary>A narrower kind of work inside a category, and the skills the opportunity form offers with it.</summary>
public sealed record SubcategoryOption(string Key, string Label, IReadOnlyList<string> Skills);
