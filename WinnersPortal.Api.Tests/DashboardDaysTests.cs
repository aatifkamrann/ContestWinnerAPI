using WinnersPortal.Services.Dashboard;
using Xunit;

namespace WinnersPortal.Api.Tests;

/// <summary>
/// The dashboard's chart days (Dashboard/DashboardController.cs). A point on a
/// chart opens a report narrowed to its day, and a report's day is the
/// viewer's own, so the charts count days in the viewer's zone — a claim at
/// one in the morning in Karachi belongs to that morning's point, not to the
/// UTC evening before it.
/// </summary>
public class DashboardDaysTests
{
    private static readonly TimeZoneInfo Karachi = DashboardService.ZoneOf("Asia/Karachi");

    [Fact]
    public void A_stamp_counts_on_the_day_it_was_where_the_viewer_is()
    {
        var from = new DateOnly(2026, 9, 11);
        // 20:00 UTC on the 12th is 01:00 on the 13th in Karachi (UTC+5).
        var stamps = new[] { new DateTimeOffset(2026, 9, 12, 20, 0, 0, TimeSpan.Zero) };

        Assert.Equal([0, 1, 0], DashboardService.Bucket(stamps, from, 3, TimeZoneInfo.Utc));
        Assert.Equal([0, 0, 1], DashboardService.Bucket(stamps, from, 3, Karachi));
    }

    [Fact]
    public void The_window_opens_at_midnight_in_the_viewers_zone_written_as_utc()
    {
        var day = new DateOnly(2026, 9, 12);

        Assert.Equal(new DateTimeOffset(2026, 9, 11, 19, 0, 0, TimeSpan.Zero), DashboardService.StartOf(day, Karachi));
        Assert.Equal(new DateTimeOffset(2026, 9, 12, 0, 0, 0, TimeSpan.Zero), DashboardService.StartOf(day, TimeZoneInfo.Utc));
        // The database takes an instant only at offset zero; +05:00 is refused outright.
        Assert.Equal(TimeSpan.Zero, DashboardService.StartOf(day, Karachi).Offset);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Not/AZone")]
    [InlineData("Asia/KarachiAsia/KarachiAsia/KarachiAsia/KarachiAsia/KarachiAsia/Karachi")]
    public void A_zone_the_server_cannot_find_counts_in_utc(string? name)
    {
        Assert.Equal(TimeZoneInfo.Utc, DashboardService.ZoneOf(name));
    }

    [Fact]
    public void A_zone_the_browser_names_is_found()
    {
        Assert.Equal(TimeSpan.FromHours(5), Karachi.BaseUtcOffset);
    }
}
