using WinnersPortal.Services.Opportunities;
using WinnersPortal.Services.Profiles;
using Xunit;

namespace WinnersPortal.Api.Tests;

/// <summary>
/// The list an administrator keeps, as rows and back, and what a retired
/// kind does to a profile. These swap the process-wide list, so they run on
/// their own, after everything else, and put the shipped one back.
/// </summary>
[CollectionDefinition(nameof(SwapsTheTaxonomy), DisableParallelization = true)]
public sealed class SwapsTheTaxonomy;

[Collection(nameof(SwapsTheTaxonomy))]
public class CategoryCatalogTests : IDisposable
{
    public void Dispose() => OpportunityCategories.Use(Taxonomy.Default);

    private static Taxonomy GamesHidden() => new(DefaultCategories.All.Select(c => c.Key == "games" ? c with { Hidden = true } : c));

    [Fact]
    public void The_shipped_list_becomes_rows_in_its_own_order()
    {
        var now = DateTimeOffset.UtcNow;
        var rows = CategoryCatalog.Rows(DefaultCategories.All, now, by: null).ToList();

        Assert.Equal(DefaultCategories.All.Select(c => c.Key), rows.Select(r => r.Key));
        Assert.Equal(Enumerable.Range(0, rows.Count), rows.Select(r => r.SortOrder));
        Assert.Equal("other", rows[^1].Key);
        var web = rows.Single(r => r.Key == "web");
        Assert.Equal("Web development", web.Label);
        Assert.Equal(DefaultCategories.All[0].Subcategories.Select(s => s.Key), web.Subcategories.Select(s => s.Key));
        Assert.All(web.Subcategories, s => Assert.Equal("web", s.CategoryKey));
        Assert.Equal(Enumerable.Range(0, web.Subcategories.Count), web.Subcategories.Select(s => s.SortOrder));
        Assert.All(rows, r => Assert.Equal(now, r.UpdatedAtUtc));
    }

    [Fact]
    public void A_swapped_list_is_what_every_reader_sees_at_once()
    {
        OpportunityCategories.Use(GamesHidden());

        Assert.True(OpportunityCategories.Find("games")!.Hidden);
        Assert.DoesNotContain("games", OpportunityCategories.Keys);
        Assert.DoesNotContain(OpportunityCategories.Offered, c => c.Key == "games");
        Assert.Contains(OpportunityCategories.All, c => c.Key == "games");
    }

    [Fact]
    public void A_profile_keeps_a_retired_kind_it_had_and_cannot_take_a_new_one()
    {
        OpportunityCategories.Use(GamesHidden());

        // Newly chosen: dropped, the way an unknown key is.
        var (primary, secondary) = ProfileRules.CleanCategories("games", ["web", "games"]);
        Assert.Null(primary);
        Assert.Equal(["web"], secondary);

        // Already held: it stays.
        (primary, secondary) = ProfileRules.CleanCategories("web", ["games", "design"], kept: ["web", "games"]);
        Assert.Equal("web", primary);
        Assert.Equal(["games", "design"], secondary);

        Assert.Null(ProfileRules.CleanCategory("games"));
        Assert.Equal("games", ProfileRules.CleanCategory("GAMES", kept: ["games"]));
    }

    [Fact]
    public void A_draft_and_a_publish_read_the_swapped_list()
    {
        OpportunityCategories.Use(GamesHidden());

        Assert.NotNull(OpportunityCategories.Problem("games", null));
        Assert.Null(OpportunityCategories.Problem("games", "unity", keptCategory: "games"));
        Assert.NotNull(OpportunityCategories.PublishProblem("games"));
    }
}
