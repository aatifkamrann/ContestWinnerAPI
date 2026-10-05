using WinnersPortal.Services.Auth;
using WinnersPortal.Domain;
using Xunit;

namespace WinnersPortal.Api.Tests;

/// <summary>
/// The pure rules behind the Users screen. Two of them are the portal's
/// safety rails — never lock yourself out, never leave the portal without
/// an administrator who can sign in — and the third decides whether
/// "delete" removes a row or erases a person from records other members
/// still rely on.
/// </summary>
public class AccountRulesTests
{
    private static User Someone() => new()
    {
        Id = Guid.NewGuid(), Email = "a@b.c", DisplayName = "A", PasswordHash = "", Role = Roles.Client,
    };

    [Fact]
    public void A_lock_or_an_erasure_closes_the_door()
    {
        Assert.True(AccountRules.CanSignIn(Someone()));
        Assert.False(AccountRules.CanSignIn(new User
        {
            Id = Guid.NewGuid(), Email = "a@b.c", DisplayName = "A", PasswordHash = "", Role = Roles.Client,
            LockedAtUtc = DateTimeOffset.UtcNow,
        }));
        Assert.False(AccountRules.CanSignIn(new User
        {
            Id = Guid.NewGuid(), Email = "a@b.c", DisplayName = "A", PasswordHash = "", Role = Roles.Client,
            ErasedAtUtc = DateTimeOffset.UtcNow,
        }));
    }

    [Fact]
    public void The_erased_address_is_unique_valid_and_undeliverable()
    {
        var a = AccountRules.ErasedEmail(Guid.NewGuid());
        var b = AccountRules.ErasedEmail(Guid.NewGuid());

        Assert.NotEqual(a, b);
        Assert.Contains('@', a);
        Assert.Equal(a, a.ToLowerInvariant()); // the column is stored lower-cased
        Assert.EndsWith(".invalid", a);        // RFC 2606: no resolver will ever answer for it
        Assert.True(a.Length <= 320);
    }

    [Fact]
    public void A_lock_reason_is_trimmed_optional_and_refused_when_too_long()
    {
        Assert.Equal((true, (string?)null), AccountRules.CleanLockReason(null));
        Assert.Equal((true, (string?)null), AccountRules.CleanLockReason("   "));
        Assert.Equal((true, (string?)"Disputed entry"), AccountRules.CleanLockReason("  Disputed entry  "));
        Assert.Equal((false, (string?)null), AccountRules.CleanLockReason(new string('x', AccountRules.MaxLockReason + 1)));
        Assert.True(AccountRules.CleanLockReason(new string('x', AccountRules.MaxLockReason)).Ok);
    }

    [Fact]
    public void You_cannot_lock_or_delete_yourself()
    {
        Assert.NotNull(AccountRules.RemovalProblem("lock", targetIsSelf: true, targetIsAdmin: true, otherActiveAdmins: 3));
        Assert.Contains("lock", AccountRules.RemovalProblem("lock", true, true, 3));
        Assert.Contains("delete", AccountRules.RemovalProblem("delete", true, true, 3));
    }

    [Fact]
    public void The_last_administrator_who_can_sign_in_stays()
    {
        Assert.NotNull(AccountRules.RemovalProblem("delete", targetIsSelf: false, targetIsAdmin: true, otherActiveAdmins: 0));
        Assert.Null(AccountRules.RemovalProblem("delete", targetIsSelf: false, targetIsAdmin: true, otherActiveAdmins: 1));
        // A client or freelancer is never the last door.
        Assert.Null(AccountRules.RemovalProblem("delete", targetIsSelf: false, targetIsAdmin: false, otherActiveAdmins: 0));
    }

    [Fact]
    public void Only_an_account_nothing_points_at_leaves_no_trace()
    {
        Assert.True(AccountRules.LeavesNoTrace(0, 0, 0));
        Assert.False(AccountRules.LeavesNoTrace(1, 0, 0));
        Assert.False(AccountRules.LeavesNoTrace(0, 1, 0));
        Assert.False(AccountRules.LeavesNoTrace(0, 0, 1));
    }

    [Theory]
    [InlineData("  Nadia Rahman  ", "Nadia Rahman")]
    [InlineData("Nadia\t\tRahman", "Nadia Rahman")]
    [InlineData("Nadia\nRahman", "Nadia Rahman")]
    [InlineData("Nadia\u0000 Rahman", "Nadia Rahman")]
    public void A_name_is_stored_as_one_line_however_it_was_pasted(string raw, string stored)
    {
        var (name, problem) = AccountRules.CleanDisplayName(raw);
        Assert.Null(problem);
        Assert.Equal(stored, name);
    }

    [Theory]
    [InlineData("Zoë Ó Súilleabháin")]
    [InlineData("田中 太郎")]
    [InlineData("Jean-Luc O'Brien")]
    [InlineData("bell hooks")]
    public void A_name_is_not_a_character_class(string raw)
    {
        var (name, problem) = AccountRules.CleanDisplayName(raw);
        Assert.Null(problem);
        Assert.Equal(raw, name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("A")]
    public void A_name_that_is_not_a_name_is_refused(string? raw)
    {
        Assert.NotNull(AccountRules.CleanDisplayName(raw).Problem);
    }

    [Fact]
    public void A_name_is_refused_rather_than_cut_short()
    {
        Assert.Null(AccountRules.CleanDisplayName(new string('a', AccountRules.MaxDisplayName)).Problem);
        Assert.NotNull(AccountRules.CleanDisplayName(new string('a', AccountRules.MaxDisplayName + 1)).Problem);
    }

    [Theory]
    [InlineData("Deleted member")]
    [InlineData("deleted MEMBER")]
    public void Nobody_gets_to_call_themselves_a_closed_account(string raw)
    {
        // The one name the portal writes itself. A member wearing it would
        // make every erased row on an entrant list ambiguous.
        Assert.NotNull(AccountRules.CleanDisplayName(raw).Problem);
    }
}
