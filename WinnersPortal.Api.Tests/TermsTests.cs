using Microsoft.AspNetCore.Http;
using WinnersPortal.Services.Settings;
using Xunit;

namespace WinnersPortal.Api.Tests;

/// <summary>
/// The pure halves of the terms of service: who owes an acceptance, what a
/// version is, and which requests wait while one is owed. What must not
/// regress is the shape of the gate — a gate that blocked reads would hide
/// the terms from the person being asked to accept them, and one that
/// blocked /api/auth would lock them in with no way to accept or leave.
/// </summary>
public class TermsTests
{
    [Theory]
    [InlineData(null, 1, true, true)]   // an account older than the terms
    [InlineData(1, 2, true, true)]      // the operator raised the version
    [InlineData(2, 2, true, false)]     // current
    [InlineData(3, 2, true, false)]     // lowering undoes nothing
    [InlineData(null, 5, false, false)] // no terms, nothing owed
    public void An_acceptance_is_owed_below_the_current_version_and_only_while_terms_exist(
        int? accepted, int current, bool exist, bool pending) =>
        Assert.Equal(pending, Terms.Pending(accepted, current, exist));

    [Theory]
    [InlineData("1", true, 1)]
    [InlineData(" 7 ", true, 7)]
    [InlineData("0", false, 1)]
    [InlineData("-2", false, 1)]
    [InlineData("2a", false, 1)]
    [InlineData("", false, 1)]
    [InlineData(null, false, 1)]
    public void A_version_is_a_whole_number_of_one_or_more(string? configured, bool valid, int parsed)
    {
        Assert.Equal(valid, Terms.IsValidVersion(configured));
        Assert.Equal(parsed, Terms.Version(configured));
    }

    [Fact]
    public void Terms_exist_once_there_is_text_to_agree_to()
    {
        Assert.False(Terms.Exist(null));
        Assert.False(Terms.Exist("  \n"));
        Assert.True(Terms.Exist("# Terms"));
    }

    [Theory]
    [InlineData("GET", "/api/opportunities")]           // reading never waits
    [InlineData("GET", "/api/public/terms")]       // least of all the terms themselves
    [InlineData("POST", "/api/auth/accept-terms")] // the way out
    [InlineData("POST", "/api/auth/logout")]       // the other way out
    [InlineData("PUT", "/api/auth/theme")]
    [InlineData("POST", "/")]                      // Next pages are not the API's to gate
    public void The_gate_lets_reads_and_the_session_through(string method, string path) =>
        Assert.True(Terms.GateAllows(new PathString(path), method));

    [Theory]
    [InlineData("POST", "/api/opportunities")]
    [InlineData("POST", "/api/opportunities/some-slug/applications")]
    [InlineData("PUT", "/api/settings")] // the administrator accepts like everyone else
    [InlineData("DELETE", "/api/opportunities/some-slug")]
    public void The_gate_holds_every_other_write(string method, string path) =>
        Assert.False(Terms.GateAllows(new PathString(path), method));
}
