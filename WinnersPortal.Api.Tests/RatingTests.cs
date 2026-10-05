using WinnersPortal.Services.Opportunities;
using WinnersPortal.Services.Email;
using Xunit;

namespace WinnersPortal.Api.Tests;

/// <summary>
/// The pure halves of ratings and the payment track record. The track record
/// is the counterweight to "no deposit, open entry", so its arithmetic — the
/// median especially — must not quietly become something else.
/// </summary>
public class RatingTests
{
    // ------------------------------------------------------- rating rules

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public void Stars_in_range_are_fine(int stars) =>
        Assert.Null(RatingRules.Problem(stars, null));

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(-1)]
    public void Stars_out_of_range_are_refused(int stars) =>
        Assert.NotNull(RatingRules.Problem(stars, null));

    [Fact]
    public void Comment_at_the_limit_passes_and_one_over_fails()
    {
        Assert.Null(RatingRules.Problem(4, new string('x', RatingRules.MaxComment)));
        Assert.NotNull(RatingRules.Problem(4, new string('x', RatingRules.MaxComment + 1)));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("  paid same day  ", "paid same day")]
    public void Comments_are_trimmed_and_blank_becomes_null(string? raw, string? stored) =>
        Assert.Equal(stored, RatingRules.CleanComment(raw));

    // -------------------------------------------------- track record math

    [Fact]
    public void Median_of_nothing_is_null() =>
        Assert.Null(TrackRecordMath.Median([]));

    [Fact]
    public void Median_odd_count_is_the_middle_value() =>
        // One 90-day dispute must not drown the same-day payments — that is
        // the whole reason this is a median and not a mean.
        Assert.Equal(0, TrackRecordMath.Median([0, 0, 90]));

    [Fact]
    public void Median_even_count_averages_the_middle_pair() =>
        Assert.Equal(2.5, TrackRecordMath.Median([1, 2, 3, 90]));

    [Fact]
    public void Days_to_pay_floors_to_whole_days()
    {
        var announced = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(0, TrackRecordMath.DaysToPay(announced, announced.AddHours(23)));
        Assert.Equal(1, TrackRecordMath.DaysToPay(announced, announced.AddHours(25)));
    }

    [Fact]
    public void Days_to_pay_never_goes_negative_on_clock_skew() =>
        Assert.Equal(0, TrackRecordMath.DaysToPay(
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(-5)));

    // ------------------------------------------------------------- emails

    [Fact]
    public void Rating_email_says_who_scored_what_and_that_it_is_public()
    {
        var email = Emails.RatingReceived("Invoice Builder", "invoice-builder", 4, "Bilal");
        Assert.Contains("Bilal", email.Subject);
        Assert.Contains("4 stars", email.Subject);
        Assert.Contains("★★★★☆", email.TextBody);
        Assert.Contains("public", email.TextBody);
        Assert.Equal("/opportunities/invoice-builder", email.ActionPath);
    }

    [Fact]
    public void One_star_rating_email_is_singular() =>
        Assert.Contains("1 star", Emails.RatingReceived("T", "t", 1, "A").Subject);

    [Fact]
    public void Paid_email_invites_the_winner_to_rate()
    {
        // The rating prompt rides the payment confirmation — the moment
        // ratings open is the moment the winner hears they can leave one.
        var email = Emails.AwardPaid("T", "t", 500, "USD", handoverNote: null);
        Assert.Contains("rate working with this client", email.TextBody);
    }
}
