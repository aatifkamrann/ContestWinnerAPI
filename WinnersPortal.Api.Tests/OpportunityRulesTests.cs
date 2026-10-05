using WinnersPortal.Services.Opportunities;
using Xunit;

namespace WinnersPortal.Api.Tests;

/// <summary>The pure rules of phase two: slugs, cursors, and the GitHub username gate.</summary>
public class OpportunityRulesTests
{
    [Theory]
    [InlineData("Inventory & billing dashboard!", "inventory-billing-dashboard")]
    [InlineData("  --Weird   spacing--  ", "weird-spacing")]
    [InlineData("پورٹل", "opportunity")] // non-ASCII collapses to the fallback, never to an empty slug
    [InlineData("UPPER case 123", "upper-case-123")]
    public void Slugs_are_lowercase_ascii_and_never_empty(string title, string expected) =>
        Assert.Equal(expected, Slugs.From(title));

    [Fact]
    public void Cursor_round_trips_and_rejects_garbage()
    {
        var cursor = new Cursor(DateTimeOffset.Parse("2026-08-28T10:00:00Z"), Guid.NewGuid());
        Assert.Equal(cursor, Cursor.Decode(cursor.Encode()));

        // A bad cursor restarts the list rather than erroring a public page.
        Assert.Null(Cursor.Decode(null));
        Assert.Null(Cursor.Decode(""));
        Assert.Null(Cursor.Decode("definitely-not-a-cursor"));
    }

    [Fact]
    public void A_cursor_carries_the_award_when_the_list_is_ordered_by_it()
    {
        var at = DateTimeOffset.Parse("2026-08-28T10:00:00Z");
        var cursor = new Cursor(at, Guid.NewGuid(), 1250.50m);
        Assert.Equal(cursor, Cursor.Decode(cursor.Encode()));

        // The two-part cursor of the other orders still reads, with no award on it.
        var plain = new Cursor(at, Guid.NewGuid());
        Assert.Equal(plain, Cursor.Decode(plain.Encode()));
        Assert.Null(Cursor.Decode(plain.Encode())!.Value.Amount);
    }

    [Theory]
    [InlineData("inventory", "%inventory%")]
    [InlineData("  Inventory dashboard ", "%Inventory dashboard%")]
    [InlineData("50% off_peak\\", "%50\\% off\\_peak\\\\%")] // the wildcards mean themselves
    public void A_title_search_is_matched_as_typed_with_the_wildcards_escaped(string q, string expected) =>
        Assert.Equal(expected, Search.TitlePattern(q));

    [Fact]
    public void The_figures_over_the_list_count_what_is_open_and_what_closes_within_the_window()
    {
        var now = DateTimeOffset.Parse("2026-09-12T10:00:00Z");
        var f = OpportunityStats.Of(new (decimal Award, string Currency, DateTimeOffset? Closes)[]
        {
            (1500m, "USD", now.AddHours(20)),
            (800m, "USD", now.AddHours(71)),
            (2000m, "USD", now.AddDays(10)),
            (300m, "USD", null),
        }, now);

        Assert.Equal(4, f.Open);
        Assert.Equal(4600m, f.Rewards);
        Assert.Equal("USD", f.Currency);
        Assert.Equal(2, f.EndingSoon);
    }

    [Fact]
    public void The_figures_are_nought_over_an_empty_portal()
    {
        var f = OpportunityStats.Of([], DateTimeOffset.UtcNow);
        Assert.Equal(new OpportunityStats.Figures(0, 0m, "USD", 0), f);
    }

    [Theory]
    [InlineData("bilal-builds", true)]
    [InlineData("a", true)]
    [InlineData("octo-cat-99", true)]
    [InlineData("-leading", false)]
    [InlineData("trailing-", false)]
    [InlineData("double--hyphen", false)]
    [InlineData("has space", false)]
    [InlineData("way-too-long-for-github-way-too-long-for-github", false)] // > 39 chars
    [InlineData("", false)]
    public void Github_usernames_follow_githubs_own_rules(string candidate, bool valid) =>
        Assert.Equal(valid, EntryService.GithubUsername().IsMatch(candidate));
}
