using WinnersPortal.Services.Opportunities;
using Xunit;

namespace WinnersPortal.Api.Tests;

/// <summary>
/// The excerpt on an opportunity card: a brief's first words as plain text. What
/// must not regress is that a heading is dropped rather than glued to the
/// sentence after it — "What we need We run a fabric…" was the card once.
/// </summary>
public class ExcerptTests
{
    [Fact]
    public void A_heading_is_a_label_for_what_follows_not_its_first_words() =>
        Assert.Equal(
            "We run a fabric wholesale business.",
            Excerpt.Of("## What we need\n\nWe run a fabric wholesale business.\n"));

    [Fact]
    public void Marks_go_and_words_stay_including_a_links_text_and_a_hash_in_prose() =>
        Assert.Equal(
            "Use the API docs and the v2 endpoint, not issue #12.",
            Excerpt.Of("Use the [API docs](https://example.com/docs) and the `v2` endpoint, **not** issue #12."));

    [Fact]
    public void List_and_quote_markers_read_as_plain_sentences() =>
        Assert.Equal(
            "Stock levels. Invoices. As the clerks asked.",
            Excerpt.Of("- Stock levels.\n2. Invoices.\n> As the clerks asked."));

    [Fact]
    public void Two_hundred_characters_then_an_ellipsis()
    {
        var s = Excerpt.Of(string.Join(' ', Enumerable.Repeat("word", 80)));
        Assert.EndsWith("…", s);
        Assert.True(s.Length <= 201, s.Length.ToString());
    }

    [Fact]
    public void Plain_text_comes_back_as_it_is() =>
        Assert.Equal("Plain words.", Excerpt.Of("Plain words.\n"));
}
