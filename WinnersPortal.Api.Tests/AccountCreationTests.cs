using WinnersPortal.Services.Email;
using WinnersPortal.Services.Auth;
using WinnersPortal.Domain;
using Xunit;

namespace WinnersPortal.Api.Tests;

/// <summary>
/// The account an administrator creates for somebody else. The rules pinned
/// here are the ones that decide who can sign in and as what: which types may
/// be made at all, how long the one link into a new account lasts, and that
/// the throttle guarding the public forgot form never mistakes that link for
/// a reset and leaves the invited person outside.
/// </summary>
public class AccountCreationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-04T12:00:00Z");

    [Theory]
    [InlineData(Roles.Freelancer)]
    [InlineData(Roles.Client)]
    [InlineData(Roles.Admin)]
    public void The_three_types_an_administrator_may_create(string role) =>
        Assert.Null(AccountRules.RoleProblem(role));

    [Fact]
    public void Administrator_is_creatable_here_and_only_here()
    {
        // Registration refuses it (AuthController), which is why this list has
        // to carry it: after the setup wizard there is no other door.
        Assert.Contains(Roles.Admin, AccountRules.CreatableRoles);
        Assert.Equal(3, AccountRules.CreatableRoles.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("owner")]
    [InlineData("superuser")]
    // The endpoint lowercases before asking, so the rule itself is exact —
    // a caller that forgets is refused rather than quietly accommodated.
    [InlineData("Admin")]
    public void Anything_else_is_refused_by_name(string? role)
    {
        var problem = AccountRules.RoleProblem(role);
        Assert.NotNull(problem);
        Assert.Contains("freelancer", problem);
        Assert.Contains("administrator", problem);
    }

    [Fact]
    public void An_invitation_outlasts_a_reset_because_nobody_asked_for_it()
    {
        Assert.True(PasswordReset.InvitationLifetime > PasswordReset.Lifetime);
        // Long enough to survive a weekend plus a working day.
        Assert.True(PasswordReset.InvitationLifetime >= TimeSpan.FromDays(3));
    }

    [Fact]
    public void The_forgot_form_still_throttles_a_reset_sent_seconds_ago()
    {
        // Unchanged behaviour: this is what stops the open form being a way
        // to flood somebody else's inbox one click at a time.
        var justIssued = Now + PasswordReset.Lifetime;
        Assert.True(PasswordReset.TooSoonToReissue(justIssued, Now));
        Assert.True(PasswordReset.TooSoonToReissue(justIssued, Now.AddSeconds(30)));

        // And lets one through once the interval has passed, or when there
        // is no standing token at all.
        Assert.False(PasswordReset.TooSoonToReissue(justIssued, Now + PasswordReset.ReissueInterval));
        Assert.False(PasswordReset.TooSoonToReissue(null, Now));
    }

    [Fact]
    public void A_standing_invitation_does_not_lock_the_invited_person_out_of_the_forgot_form()
    {
        // The throttle reads the issue time back off the expiry. An
        // invitation expires a week out, which under a lifetime-of-one-hour
        // reading looks like a reset issued days in the future — and would
        // have silently refused this person a link for the whole week.
        var invited = Now + PasswordReset.InvitationLifetime;
        Assert.False(PasswordReset.TooSoonToReissue(invited, Now));
        Assert.False(PasswordReset.TooSoonToReissue(invited, Now.AddDays(3)));

        // Once it is nearly spent it is still not a recent reset.
        Assert.False(PasswordReset.TooSoonToReissue(invited, invited - TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void The_invitation_says_who_made_the_account_and_that_it_is_shut_until_used()
    {
        var mail = Emails.AccountInvitation("tok123", Roles.Freelancer, 7);

        Assert.Equal("/reset-password?token=tok123", mail.ActionPath);
        Assert.Contains("administrator created an account for you", mail.TextBody);
        Assert.Contains("7 days", mail.TextBody);
        // A reader who was not expecting this needs to know that ignoring it
        // is safe, and why.
        Assert.Contains("cannot be signed into", mail.TextBody);
        // …and must not be told they asked for a reset, which is what a
        // phishing mail says and what the reset template says.
        Assert.DoesNotContain("hopefully you", mail.TextBody);
        Assert.DoesNotContain("your current password", mail.TextBody);
    }

    [Fact]
    public void Each_type_is_described_by_what_it_lets_the_person_do()
    {
        var freelancer = Emails.AccountInvitation("t", Roles.Freelancer, 7).TextBody;
        var client = Emails.AccountInvitation("t", Roles.Client, 7).TextBody;
        var admin = Emails.AccountInvitation("t", Roles.Admin, 7).TextBody;

        Assert.Contains("private repository", freelancer);
        Assert.Contains("fixed award", client);
        // The one type whose reach the reader should be told about before
        // they choose a password for it.
        Assert.Contains("every opportunity, account and setting", admin);
        Assert.Contains("nowhere else", admin);

        Assert.NotEqual(freelancer, client);
        Assert.NotEqual(client, admin);
    }
}
