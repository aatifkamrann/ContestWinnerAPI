using WinnersPortal.Services.Auth;
using Xunit;

namespace WinnersPortal.Api.Tests;

public class UserThemeTests
{
    [Theory]
    [InlineData(null, true, null)] // clear = fall back to the portal default
    [InlineData("", true, null)]
    [InlineData("  ", true, null)]
    [InlineData("#2563eb", true, "#2563EB")] // canonicalised to uppercase
    [InlineData("#F43F5E", true, "#F43F5E")]
    [InlineData(" #14b8a6 ", true, "#14B8A6")]
    [InlineData("2563EB", false, null)] // missing hash
    [InlineData("#25 63EB", false, null)]
    [InlineData("#2563EBFF", false, null)] // no alpha channel
    [InlineData("#25E", false, null)] // no shorthand
    [InlineData("red", false, null)]
    [InlineData("#2563GG", false, null)]
    public void Normalize(string? input, bool ok, string? expected)
    {
        var (actualOk, normalized) = UserTheme.Normalize(input);
        Assert.Equal(ok, actualOk);
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void Status_colours_keep_only_the_changed_ones_in_picker_order()
    {
        var (ok, json) = UserTheme.NormalizeStatus(new Dictionary<string, string?>
        {
            ["cancelled"] = "#e11d48",
            ["draft"] = " #64748b ",
            ["open"] = null, // cleared = stock
        });
        Assert.True(ok);
        Assert.Equal("{\"draft\":\"#64748B\",\"cancelled\":\"#E11D48\"}", json);
        var back = UserTheme.ParseStatus(json);
        Assert.NotNull(back);
        Assert.Equal(["draft", "cancelled"], back.Keys);
        Assert.Equal("#E11D48", back["cancelled"]);
    }

    [Fact]
    public void Status_colours_all_cleared_store_as_the_stock_set()
    {
        var (ok, json) = UserTheme.NormalizeStatus(new Dictionary<string, string?> { ["open"] = "", ["awarded"] = null });
        Assert.True(ok);
        Assert.Null(json);
        Assert.Null(UserTheme.ParseStatus(null));
        Assert.Null(UserTheme.ParseStatus("not json"));
    }

    [Theory]
    [InlineData("paid", "#2563EB")] // a handover state, not an opportunity status
    [InlineData("Open", "#2563EB")] // the API's status names are lower-case
    [InlineData("open", "blue")]
    public void Status_colours_reject_a_bad_entry_rather_than_drop_it(string key, string value)
    {
        var (ok, json) = UserTheme.NormalizeStatus(new Dictionary<string, string?> { [key] = value, ["draft"] = "#64748B" });
        Assert.False(ok);
        Assert.Null(json);
    }
}
