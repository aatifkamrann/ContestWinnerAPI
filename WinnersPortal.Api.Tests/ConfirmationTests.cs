using Microsoft.AspNetCore.Http;
using WinnersPortal.Services.Auth;
using Xunit;

namespace WinnersPortal.Api.Tests;

/// <summary>
/// The pure halves of contact confirmation: what a code is, what a presented
/// one amounts to against the row, how a phone number is read, and which
/// requests an unconfirmed account may still make. What must not regress is
/// the shape of the guard — a six-digit code is only as safe as its attempt
/// cap and lifetime, and a gate that blocked /api/auth would lock the person
/// in with no way to enter the code or leave.
/// </summary>
public class ConfirmationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-10T12:00:00Z");

    [Fact]
    public void A_code_is_six_digits_and_the_hash_is_never_the_code()
    {
        for (var i = 0; i < 50; i++)
        {
            var code = Confirmation.NewCode();
            Assert.Matches("^[0-9]{6}$", code);
            Assert.Matches("^[0-9a-f]{64}$", Confirmation.Hash(code));
            Assert.Equal(Confirmation.Hash(code), Confirmation.Hash(code));
        }
    }

    [Fact]
    public void Either_channels_code_confirms_and_the_row_learns_which()
    {
        var email = Confirmation.Hash("111111");
        var phone = Confirmation.Hash("222222");

        Assert.Equal(Confirmation.Match.Email, Confirmation.Verify(email, phone, Now, 0, "111111", Now.AddMinutes(1)));
        Assert.Equal(Confirmation.Match.Phone, Confirmation.Verify(email, phone, Now, 0, "222222", Now.AddMinutes(1)));
        Assert.Equal(Confirmation.Match.None, Confirmation.Verify(email, phone, Now, 0, "333333", Now.AddMinutes(1)));
        // No text went out: the phone code cannot be "the one they typed".
        Assert.Equal(Confirmation.Match.None, Confirmation.Verify(email, null, Now, 0, "222222", Now.AddMinutes(1)));
    }

    [Theory]
    [InlineData("111 111")]
    [InlineData("111-111")]
    [InlineData(" 111111 ")]
    public void A_code_typed_with_punctuation_is_the_code_it_means(string typed) =>
        Assert.Equal(Confirmation.Match.Email,
            Confirmation.Verify(Confirmation.Hash("111111"), null, Now, 0, typed, Now));

    [Theory]
    [InlineData("11111")]
    [InlineData("1111111")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_but_six_digits_is_not_a_guess(string? typed) =>
        Assert.Equal(Confirmation.Match.None,
            Confirmation.Verify(Confirmation.Hash("111111"), null, Now, 0, typed, Now));

    [Fact]
    public void A_code_dies_at_its_lifetime_and_after_its_last_try()
    {
        var hash = Confirmation.Hash("111111");
        var lastMoment = Now + Confirmation.Lifetime;
        Assert.Equal(Confirmation.Match.Email, Confirmation.Verify(hash, null, Now, 0, "111111", lastMoment));
        Assert.Equal(Confirmation.Match.Expired, Confirmation.Verify(hash, null, Now, 0, "111111", lastMoment.AddSeconds(1)));

        // The right code, presented once the cap is spent, is refused too:
        // the cap would mean nothing if guess number six could still land.
        Assert.Equal(Confirmation.Match.Email, Confirmation.Verify(hash, null, Now, Confirmation.MaxAttempts - 1, "111111", Now));
        Assert.Equal(Confirmation.Match.Exhausted, Confirmation.Verify(hash, null, Now, Confirmation.MaxAttempts, "111111", Now));
    }

    [Fact]
    public void No_standing_code_matches_nothing()
    {
        Assert.Equal(Confirmation.Match.None, Confirmation.Verify(null, null, Now, 0, "111111", Now));
        Assert.Equal(Confirmation.Match.None, Confirmation.Verify(null, null, null, 0, "111111", Now));
    }

    [Fact]
    public void A_six_digit_code_is_guarded_by_its_cap_not_its_length()
    {
        // Five tries against a million codes: a guesser's odds are one in
        // two hundred thousand per pair, and the pair dies in fifteen
        // minutes. Widen either and the arithmetic changes — say so here.
        Assert.Equal(6, Confirmation.CodeLength);
        Assert.True(Confirmation.MaxAttempts <= 5);
        Assert.True(Confirmation.Lifetime <= TimeSpan.FromMinutes(15));
    }

    [Fact]
    public void A_resend_waits_a_minute_and_says_how_long()
    {
        Assert.False(Confirmation.TooSoonToResend(null, Now));
        Assert.True(Confirmation.TooSoonToResend(Now, Now.AddSeconds(30)));
        Assert.Equal(30, Confirmation.RetryAfterSeconds(Now, Now.AddSeconds(30)));
        Assert.False(Confirmation.TooSoonToResend(Now, Now + Confirmation.ResendInterval));
        Assert.Equal(0, Confirmation.RetryAfterSeconds(Now, Now + Confirmation.ResendInterval));
    }

    // ---------------------------------------------------------------- phone

    [Theory]
    [InlineData("+92 300 1234567", "+923001234567")]
    [InlineData("+44 (0)7700-900123", "+4407700900123")] // the bracketed trunk zero is theirs to type; kept as typed
    [InlineData("0092 300 1234567", "+923001234567")]    // the dialling prefix for plus
    [InlineData("+1.555.000.1111", "+15550001111")]
    [InlineData("  +8801712345678  ", "+8801712345678")]
    public void A_phone_is_stored_in_one_form_however_it_was_typed(string typed, string stored)
    {
        var (phone, problem) = Confirmation.CleanPhone(typed);
        Assert.Null(problem);
        Assert.Equal(stored, phone);
        Assert.True(phone!.Length <= Confirmation.MaxPhoneLength);
    }

    [Theory]
    [InlineData("03001234567")]      // a national number is a different phone in every country
    [InlineData("3001234567")]
    [InlineData("+92 300 12x4567")]  // not a digit
    [InlineData("+1234567")]         // too short to be anyone
    [InlineData("+1234567890123456")] // past E.164's fifteen
    [InlineData("+0300123456")]      // no country code starts with zero
    [InlineData("+")]
    public void A_number_without_a_country_code_or_with_letters_is_refused_not_guessed(string typed)
    {
        var (phone, problem) = Confirmation.CleanPhone(typed);
        Assert.Null(phone);
        Assert.Equal(Confirmation.PhoneProblem, problem);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_phone_is_not_a_problem(string? typed)
    {
        var (phone, problem) = Confirmation.CleanPhone(typed);
        Assert.Null(phone);
        Assert.Null(problem);
    }

    [Fact]
    public void Masks_leave_enough_to_recognise_and_not_enough_to_copy()
    {
        Assert.Equal("b•••@example.com", Confirmation.MaskEmail("bilal@example.com"));
        Assert.Equal("+92 ••• 4567", Confirmation.MaskPhone("+923001234567"));
        Assert.DoesNotContain("3001", Confirmation.MaskPhone("+923001234567"));
    }

    [Fact]
    public void The_text_carries_the_code_the_portal_and_the_lifetime_and_stays_one_segment()
    {
        var text = Confirmation.Text("Astrik Winners Portal", "123456");
        Assert.StartsWith("123456", text);
        Assert.Contains("Astrik Winners Portal", text);
        Assert.Contains("15 minutes", text);
        Assert.True(text.Length <= 160, "one SMS segment — the operator may pay per segment");
    }

    // ----------------------------------------------------------------- gate

    [Theory]
    [InlineData("/api/auth/confirm")]      // the way in
    [InlineData("/api/auth/resend-code")]
    [InlineData("/api/auth/me")]           // who am I, which the shell asks first
    [InlineData("/api/auth/logout")]       // the way out
    [InlineData("/api/public/branding")]   // what any stranger may read
    [InlineData("/api/help/register.code")]
    [InlineData("/api/health")]
    [InlineData("/register")]              // Next pages are not the API's to gate
    public void The_gate_lets_the_session_and_the_public_through(string path) =>
        Assert.True(Confirmation.GateAllows(new PathString(path)));

    [Theory]
    [InlineData("/api/opportunities")]          // reads wait too: an unconfirmed account is not yet a member
    [InlineData("/api/opportunities/some-slug")]
    [InlineData("/api/profile")]
    [InlineData("/api/dashboard")]
    [InlineData("/api/github/connect")]
    [InlineData("/api/live/opportunities")]
    [InlineData("/api/settings")]
    public void Everything_else_waits(string path) =>
        Assert.False(Confirmation.GateAllows(new PathString(path)));

    [Fact]
    public void The_gate_names_a_phone_only_where_the_portal_can_text()
    {
        Assert.Contains("phone number", Confirmation.GateMessage(canText: true));
        Assert.DoesNotContain("phone", Confirmation.GateMessage(canText: false));
        // Either way it says what is owed, not merely that something is.
        Assert.Contains("email address", Confirmation.GateMessage(canText: false));
    }

    [Fact]
    public void A_codes_expiry_is_its_issue_time_plus_the_lifetime()
    {
        var issued = new DateTimeOffset(2026, 9, 11, 9, 0, 0, TimeSpan.Zero);
        Assert.Equal(issued + Confirmation.Lifetime, Confirmation.ExpiresUtc(issued));
        // Fifteen minutes is what the form counts down and what the email
        // and the text both promise; they read it from here.
        Assert.Equal(15, (int)Confirmation.Lifetime.TotalMinutes);
        // No code standing, nothing to count down.
        Assert.Null(Confirmation.ExpiresUtc(null));
    }
}
