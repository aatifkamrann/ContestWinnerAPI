using Microsoft.EntityFrameworkCore;
using WinnersPortal.Domain;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Services.Admin;
using WinnersPortal.Services.Opportunities;
using Xunit;

namespace WinnersPortal.Api.Tests;

/// <summary>Admin → Categories: what a save accepts, what it refuses, and what it does to the rows.</summary>
public class CategoryAdminTests
{
    private static CategoryInput Cat(string key, string label, bool hidden = false, string[]? skills = null, params SubcategoryInput[] subs) =>
        new() { Key = key, Label = label, Hidden = hidden, Skills = [.. skills ?? []], Subcategories = [.. subs] };

    private static SubcategoryInput Sub(string key, string label, bool hidden = false, string[]? skills = null) =>
        new() { Key = key, Label = label, Hidden = hidden, Skills = [.. skills ?? []] };

    private static CategoryInput Other() => Cat("other", "Something else");

    private static string? Check(params CategoryInput[] categories) =>
        CategoryAdminService.Clean(new CategoriesSaveRequest { Version = "v", Categories = [.. categories] }, out _);

    // ------------------------------------------------------------- shape

    [Fact]
    public void The_shipped_list_as_the_screen_sends_it_is_accepted_unchanged()
    {
        var sent = DefaultCategories.All.Select(c => Cat(c.Key, c.Label, c.Hidden, [.. c.Skills],
            [.. c.Subcategories.Select(s => Sub(s.Key, s.Label, s.Hidden, [.. s.Skills]))])).ToArray();
        Assert.Null(CategoryAdminService.Clean(new CategoriesSaveRequest { Categories = [.. sent] }, out var list));
        Assert.Equal(CategoryAdminService.Version(Taxonomy.Default), CategoryAdminService.Version(new Taxonomy(list)));
    }

    [Theory]
    [InlineData("Web", "web")]
    [InlineData("  data-labelling ", "data-labelling")]
    public void A_key_is_stored_trimmed_and_lower_case(string sent, string stored)
    {
        Assert.Null(CategoryAdminService.Clean(
            new CategoriesSaveRequest { Categories = [Cat(sent, "Data"), Other()] }, out var list));
        Assert.Equal(stored, list[0].Key);
    }

    [Theory]
    [InlineData("data labelling")]
    [InlineData("-data")]
    [InlineData("données")]
    [InlineData("a234567890123456789012345678901234567890x")]
    public void A_key_is_letters_digits_and_hyphens_and_short(string key) =>
        Assert.Equal("Data: a key is lower-case letters, digits and hyphens, at most 40 characters.", Check(Cat(key, "Data"), Other()));

    [Fact]
    public void Names_and_keys_are_unique_among_their_siblings()
    {
        Assert.Equal("Two categories share the key web.", Check(Cat("web", "Web"), Cat("web", "Webs"), Other()));
        Assert.Equal("Two categories are both called web.", Check(Cat("web", "Web"), Cat("www", "web"), Other()));
        Assert.Equal("Web: two subcategories share the key ui.",
            Check(Cat("web", "Web", subs: [Sub("ui", "UI"), Sub("ui", "Interfaces")]), Other()));
        // The same subcategory key under two categories is fine: it is unique under its parent.
        Assert.Null(Check(Cat("web", "Web", subs: [Sub("ui", "UI")]), Cat("mobile", "Mobile", subs: [Sub("ui", "UI")]), Other()));
    }

    [Fact]
    public void Something_else_stays_last_offered_and_without_subcategories()
    {
        Assert.StartsWith("Something else (key other) stays", Check(Cat("web", "Web")));
        Assert.Equal("Something else stays last.", Check(Other(), Cat("web", "Web")));
        Assert.StartsWith("Something else cannot be hidden", Check(Cat("web", "Web"), Cat("other", "Something else", hidden: true)));
        Assert.Equal("Something else takes no subcategories.", Check(Cat("other", "Something else", subs: [Sub("x", "X")])));
        // Its name is the administrator's to change.
        Assert.Null(Check(Cat("web", "Web"), Cat("other", "Anything else")));
    }

    [Fact]
    public void Skills_are_trimmed_a_repeat_dropped_and_the_list_capped()
    {
        Assert.Null(CategoryAdminService.Clean(new CategoriesSaveRequest
        {
            Categories = [Cat("web", "Web", skills: [" React ", "react", "", "Vue"]), Other()],
        }, out var list));
        Assert.Equal(["React", "Vue"], list[0].Skills);

        var many = Enumerable.Range(0, CategoryAdminService.MaxSkills + 1).Select(i => $"Skill {i}").ToArray();
        Assert.Equal($"Web: at most {CategoryAdminService.MaxSkills} skills.", Check(Cat("web", "Web", skills: many), Other()));
    }

    [Fact]
    public void The_list_is_capped_so_every_AI_reading_stays_a_sensible_size()
    {
        var many = Enumerable.Range(0, CategoryAdminService.MaxCategories).Select(i => Cat($"k{i}", $"Kind {i}")).Append(Other()).ToArray();
        Assert.StartsWith($"At most {CategoryAdminService.MaxCategories} categories", Check(many));
    }

    // ------------------------------------------------------------ removal

    private static readonly List<Category> Stored =
    [
        new() { Key = "web", Label = "Web", Subcategories = [new() { CategoryKey = "web", Key = "flash", Label = "Flash" }] },
        new() { Key = "games", Label = "Games" },
        new() { Key = "other", Label = "Something else" },
    ];

    private static CategoryAdminService.Uses Uses(int webOpportunities = 0, int flash = 0, int gamesProfiles = 0) =>
        CategoryAdminService.Uses.From(new CategoryAdminService.UsageReads(
            [new("web", null, webOpportunities), new("web", "flash", flash)],
            [new("games", gamesProfiles)], [], []));

    private static List<OpportunityCategory> Keeping(params string[] keys) =>
        [.. keys.Select(k => new OpportunityCategory(k, k, [], []))];

    [Fact]
    public void A_kind_nothing_uses_can_be_removed()
    {
        Assert.Null(CategoryAdminService.RemovalProblem(Stored, Keeping("web", "other"), Uses()));
    }

    [Fact]
    public void A_kind_in_use_cannot_be_removed_and_the_refusal_says_what_uses_it()
    {
        Assert.Equal("Still in use, so hide rather than remove: Games (2 profiles).",
            CategoryAdminService.RemovalProblem(Stored, Keeping("web", "other"), Uses(gamesProfiles: 2)));
        // A subcategory dropped from a kept category counts too.
        Assert.Equal("Still in use, so hide rather than remove: Web › Flash (1 opportunity).",
            CategoryAdminService.RemovalProblem(Stored, Keeping("web", "games", "other"), Uses(webOpportunities: 1, flash: 1)));
    }

    // ------------------------------------------------------------- apply

    private static AppDbContext Model() => new(AppDbContextOptions.Build(
        DatabaseProvider.Postgres, "Host=nowhere;Database=none;Username=x;Password=x"));

    [Fact]
    public void A_save_renames_reorders_hides_adds_and_removes_by_key_and_stamps_only_what_changed()
    {
        using var db = Model();
        var earlier = DateTimeOffset.UtcNow.AddDays(-1);
        var rows = new List<Category>
        {
            new() { Key = "web", Label = "Web", SortOrder = 0, Skills = ["React"], UpdatedAtUtc = earlier,
                Subcategories = [new() { CategoryKey = "web", Key = "ui", Label = "UI", SortOrder = 0 }] },
            new() { Key = "games", Label = "Games", SortOrder = 1, UpdatedAtUtc = earlier },
            new() { Key = "qa", Label = "QA", SortOrder = 2, UpdatedAtUtc = earlier },
            new() { Key = "other", Label = "Something else", SortOrder = 3, UpdatedAtUtc = earlier },
        };
        db.Categories.AttachRange(rows);
        var now = DateTimeOffset.UtcNow;
        var service = new CategoryAdminService(db, null!, null!);

        service.Apply(rows,
        [
            new("games", "Games", [], [], Hidden: true),
            new("web", "Web development", [new("ui", "UI", []), new("api", "APIs", ["REST"])], ["React"]),
            new("labelling", "Data labelling", [new("images", "Images", [])], ["Label Studio"]),
            new("other", "Something else", [], []),
        ], now, "admin@example.test");

        var games = rows.Single(r => r.Key == "games");
        Assert.True(games.Hidden);
        Assert.Equal(0, games.SortOrder);
        Assert.Equal(now, games.UpdatedAtUtc);

        var web = rows.Single(r => r.Key == "web");
        Assert.Equal("Web development", web.Label);
        Assert.Equal(["ui", "api"], web.Subcategories.OrderBy(s => s.SortOrder).Select(s => s.Key));
        Assert.Equal("admin@example.test", web.UpdatedBy);

        Assert.Equal(EntityState.Deleted, db.Entry(rows.Single(r => r.Key == "qa")).State);
        var added = db.ChangeTracker.Entries<Category>().Single(e => e.State == EntityState.Added).Entity;
        Assert.Equal(("labelling", 2), (added.Key, added.SortOrder));
        Assert.Equal("images", Assert.Single(added.Subcategories).Key);

        // Moved from fourth to fourth, unchanged: no stamp.
        Assert.Equal(earlier, rows.Single(r => r.Key == "other").UpdatedAtUtc);
    }
}
