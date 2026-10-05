using Microsoft.Extensions.Logging;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Services.Settings;
using Xunit;

namespace WinnersPortal.Api.Tests;

/// <summary>
/// The slow-query log: what counts as slow, what a line says, what the
/// operations screen's tally keeps, and the setting that sets the
/// threshold — refused on save when it is not a number, so the screen
/// never shows one value while another is in force.
/// </summary>
public class SlowQueryTests
{
    private sealed class Capture : ILogger<SlowQueryInterceptor>
    {
        public List<(LogLevel Level, string Text)> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Lines.Add((logLevel, formatter(state, exception)));
    }

    private static (SlowQueryInterceptor Interceptor, Capture Log) Make(int ms, string? during = "GET /api/opportunities")
    {
        var log = new Capture();
        return (new SlowQueryInterceptor(new SlowQueryStats(ms), log, () => during), log);
    }

    [Theory]
    [InlineData(" 250 ", 250)]
    [InlineData("100", 100)]
    [InlineData("0", 0)]
    public void The_setting_takes_whole_milliseconds_and_zero(string raw, int expected) =>
        Assert.Equal(expected, SlowQueryStats.ParseMs(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("-1")]
    [InlineData("half a second")]
    [InlineData("0.5")]
    public void Anything_else_is_no_threshold(string? raw) => Assert.Null(SlowQueryStats.ParseMs(raw));

    [Fact]
    public void The_setting_lives_under_limits_and_defaults_to_half_a_second()
    {
        var def = SettingsRegistry.Find(SlowQueryStats.SettingKey)!;
        Assert.Equal("limits", def.Group);
        Assert.Equal(SlowQueryStats.DefaultMs.ToString(), def.Default);
        Assert.Equal(SlowQueryStats.DefaultMs, new SlowQueryStats().ThresholdMs);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("fast")]
    [InlineData("0.5")]
    public void A_threshold_that_is_not_a_number_is_refused_on_save(string value)
    {
        var problem = SettingsService.ValueProblem(SettingsRegistry.Find(SlowQueryStats.SettingKey)!, value);
        Assert.NotNull(problem);
        Assert.Contains("0 turns it off", problem);
    }

    [Theory]
    [InlineData("100")]
    [InlineData("0")]
    [InlineData("")]
    [InlineData(null)]
    public void A_number_or_blank_for_the_default_is_accepted(string? value) =>
        Assert.Null(SettingsService.ValueProblem(SettingsRegistry.Find(SlowQueryStats.SettingKey)!, value));

    [Fact]
    public void A_command_under_the_threshold_is_not_logged()
    {
        var (i, log) = Make(500);
        Assert.False(i.Note(TimeSpan.FromMilliseconds(499), 3, "Query", "SELECT 1"));
        Assert.Empty(log.Lines);
    }

    [Fact]
    public void A_slow_command_is_a_warning_naming_time_rows_source_request_and_sql()
    {
        var (i, log) = Make(500);
        Assert.True(i.Note(TimeSpan.FromMilliseconds(812), 5000, "Query",
            "SELECT c.\"Id\" FROM \"Opportunities\" AS c WHERE c.\"Title\" = @p0"));

        var (level, text) = Assert.Single(log.Lines);
        Assert.Equal(LogLevel.Warning, level);
        Assert.Contains("812 ms", text);
        Assert.Contains("5000 rows", text);
        Assert.Contains("Query", text);
        Assert.Contains("during GET /api/opportunities", text);
        Assert.Contains("threshold 500 ms", text);
        // The placeholder, never the value it was bound to.
        Assert.Contains("@p0", text);
    }

    [Fact]
    public void Work_outside_a_request_says_so_and_a_scalar_has_no_row_count()
    {
        var (i, log) = Make(100, during: null);
        i.Note(TimeSpan.FromSeconds(2), null, "ExecuteSqlRaw", "UPDATE x SET y = 1");
        var (_, text) = Assert.Single(log.Lines);
        Assert.Contains("during background work", text);
        Assert.Contains("n/a rows", text);
    }

    [Fact]
    public void Very_long_sql_is_cut()
    {
        var (i, log) = Make(1);
        i.Note(TimeSpan.FromSeconds(1), 1, "Query", "SELECT " + new string('x', 10_000));
        var (_, text) = Assert.Single(log.Lines);
        Assert.True(text.Length < SlowQueryInterceptor.MaxSqlLength + 300);
        Assert.EndsWith(" …", text);
    }

    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly DateTimeOffset T0 = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_slow_command_is_tallied_as_well_as_logged()
    {
        var stats = new SlowQueryStats(100);
        var i = new SlowQueryInterceptor(stats, new Capture(), () => "GET /api/opportunities");
        i.Note(TimeSpan.FromMilliseconds(99), 1, "Query", "SELECT 1");
        i.Note(TimeSpan.FromMilliseconds(250), 4, "Query", "SELECT 2");

        var s = Assert.Single(stats.Snapshot(10));
        Assert.Equal("SELECT 2", s.Sql);
        Assert.Equal("GET /api/opportunities", s.LastDuring);
        Assert.Equal(4, s.LastRows);
    }

    [Fact]
    public void The_same_sql_is_one_row_with_its_count_total_slowest_and_last_run()
    {
        var clock = new Clock(T0);
        var stats = new SlowQueryStats(100, clock);
        stats.Record("SELECT a", "Query", 300, 10, "GET /api/opportunities");
        clock.Now = T0.AddMinutes(5);
        stats.Record("SELECT a", "Query", 900, 2, "background work");
        clock.Now = T0.AddMinutes(9);
        stats.Record("SELECT a", "Query", 150, null, "GET /api/me");

        var s = Assert.Single(stats.Snapshot(10));
        Assert.Equal(3, s.Count);
        Assert.Equal(1350, s.TotalMs);
        Assert.Equal(900, s.MaxMs);
        Assert.Equal(150, s.LastMs);
        Assert.Null(s.LastRows);
        Assert.Equal("GET /api/me", s.LastDuring);
        Assert.Equal(T0, s.FirstAtUtc);
        Assert.Equal(T0.AddMinutes(9), s.LastAtUtc);
    }

    [Fact]
    public void The_costliest_in_all_come_first_so_often_a_little_slow_outranks_once_very_slow()
    {
        var stats = new SlowQueryStats(100);
        stats.Record("once", "Query", 3000, 1, "background work");
        for (var n = 0; n < 20; n++) stats.Record("often", "Query", 200, 1, "GET /api/opportunities");
        stats.Record("rare", "Query", 120, 1, "GET /api/me");

        Assert.Equal(["often", "once", "rare"], stats.Snapshot(10).Select(s => s.Sql));
        Assert.Equal(["often"], stats.Snapshot(1).Select(s => s.Sql));
    }

    [Fact]
    public void A_full_tally_makes_room_by_dropping_the_shape_seen_longest_ago()
    {
        var clock = new Clock(T0);
        var stats = new SlowQueryStats(100, clock);
        for (var n = 0; n < SlowQueryStats.MaxShapes; n++)
        {
            clock.Now = T0.AddSeconds(n);
            stats.Record($"SELECT {n}", "Query", 5000, 1, "background work");
        }
        clock.Now = T0.AddSeconds(1000);
        stats.Record("SELECT 0", "Query", 5000, 1, "background work"); // seen again, so no longer the oldest
        stats.Record("SELECT new", "Query", 100, 1, "background work");

        var shapes = stats.Snapshot(1000).Select(s => s.Sql).ToList();
        Assert.Equal(SlowQueryStats.MaxShapes, shapes.Count);
        Assert.Contains("SELECT new", shapes);
        Assert.Contains("SELECT 0", shapes);
        Assert.DoesNotContain("SELECT 1", shapes);
    }

    [Fact]
    public void Clear_starts_the_tally_again_from_now()
    {
        var clock = new Clock(T0);
        var stats = new SlowQueryStats(100, clock);
        stats.Record("SELECT a", "Query", 300, 1, "background work");
        Assert.Equal(T0, stats.SinceUtc);

        clock.Now = T0.AddHours(2);
        stats.Clear();
        Assert.Empty(stats.Snapshot(10));
        Assert.Equal(T0.AddHours(2), stats.SinceUtc);
    }

    [Theory]
    [InlineData(Microsoft.EntityFrameworkCore.Diagnostics.CommandSource.LinqQuery, "Query")]
    [InlineData(Microsoft.EntityFrameworkCore.Diagnostics.CommandSource.Unknown, "Query")]
    // Postgres reads an insert's new ids back through a reader: still a save.
    [InlineData(Microsoft.EntityFrameworkCore.Diagnostics.CommandSource.SaveChanges, "SaveChanges")]
    [InlineData(Microsoft.EntityFrameworkCore.Diagnostics.CommandSource.FromSqlQuery, "FromSqlQuery")]
    [InlineData(Microsoft.EntityFrameworkCore.Diagnostics.CommandSource.Migrations, "Migrations")]
    public void A_reader_is_named_for_what_issued_it(Microsoft.EntityFrameworkCore.Diagnostics.CommandSource source, string expected) =>
        Assert.Equal(expected, SlowQueryInterceptor.ReaderSource(source));

    [Fact]
    public void Zero_logs_and_tallies_nothing_however_slow()
    {
        var stats = new SlowQueryStats(0);
        var log = new Capture();
        var i = new SlowQueryInterceptor(stats, log, () => null);
        Assert.False(i.Note(TimeSpan.FromSeconds(30), 1, "Query", "SELECT 1"));
        Assert.Empty(log.Lines);
        Assert.Empty(stats.Snapshot(10));
    }

    [Fact]
    public void A_new_threshold_is_in_force_at_once_and_starts_the_tally_again()
    {
        var clock = new Clock(T0);
        var stats = new SlowQueryStats(500, clock);
        var i = new SlowQueryInterceptor(stats, new Capture(), () => null);
        Assert.True(i.Note(TimeSpan.FromMilliseconds(600), 1, "Query", "SELECT a"));
        Assert.False(i.Note(TimeSpan.FromMilliseconds(150), 1, "Query", "SELECT b"));

        clock.Now = T0.AddHours(1);
        Assert.True(stats.SetThreshold(100));
        Assert.Empty(stats.Snapshot(10));
        Assert.Equal(T0.AddHours(1), stats.SinceUtc);
        Assert.True(i.Note(TimeSpan.FromMilliseconds(150), 1, "Query", "SELECT b"));
        Assert.Equal(["SELECT b"], stats.Snapshot(10).Select(s => s.Sql));
    }

    [Fact]
    public void The_same_threshold_again_keeps_the_tally()
    {
        var stats = new SlowQueryStats(100);
        stats.Record("SELECT a", "Query", 300, 1, "background work");
        Assert.False(stats.SetThreshold(100));
        Assert.Single(stats.Snapshot(10));
    }

    [Fact]
    public void The_background_switch_sits_right_after_the_threshold_and_counts_background_work_by_default()
    {
        var limits = SettingsRegistry.All.Where(d => d.Group == "limits").Select(d => d.Key).ToList();
        Assert.Equal(limits.IndexOf(SlowQueryStats.SettingKey) + 1, limits.IndexOf(SlowQueryStats.BackgroundSettingKey));

        var def = SettingsRegistry.Find(SlowQueryStats.BackgroundSettingKey)!;
        Assert.True(def.IsBoolean);
        Assert.Equal("true", def.Default);
        Assert.True(new SlowQueryStats().IncludesBackground);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData(" False ", false)]
    [InlineData("false", false)]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("maybe", true)]
    public void The_switch_reads_true_or_false_and_anything_else_is_the_default(string? raw, bool expected) =>
        Assert.Equal(expected, SlowQueryStats.ParseIncludesBackground(raw));

    [Fact]
    public void Switched_off_background_work_is_neither_logged_nor_tallied_but_a_request_still_is()
    {
        var stats = new SlowQueryStats(100);
        stats.SetIncludesBackground(false);
        string? during = null;
        var log = new Capture();
        var i = new SlowQueryInterceptor(stats, log, () => during);

        Assert.False(i.Note(TimeSpan.FromSeconds(3), 1, "SaveChanges", "UPDATE \"EmailOutbox\" SET x = @p0"));
        Assert.Empty(log.Lines);
        Assert.Empty(stats.Snapshot(10));

        during = "GET /api/opportunities";
        Assert.True(i.Note(TimeSpan.FromMilliseconds(250), 4, "Query", "SELECT 2"));
        Assert.Equal(["GET /api/opportunities"], stats.Snapshot(10).Select(s => s.LastDuring));
        Assert.Single(log.Lines);
    }

    [Fact]
    public void Switched_on_background_work_says_so()
    {
        var stats = new SlowQueryStats(100);
        var i = new SlowQueryInterceptor(stats, new Capture(), () => null);
        Assert.True(i.Note(TimeSpan.FromSeconds(3), 1, "SaveChanges", "UPDATE x SET y = 1"));
        Assert.Equal([SlowQueryInterceptor.BackgroundWork], stats.Snapshot(10).Select(s => s.LastDuring));
    }

    [Fact]
    public void Flipping_the_switch_starts_the_tally_again_and_the_same_value_keeps_it()
    {
        var clock = new Clock(T0);
        var stats = new SlowQueryStats(100, clock);
        stats.Record("SELECT a", "Query", 300, 1, "background work");
        Assert.False(stats.SetIncludesBackground(true));
        Assert.Single(stats.Snapshot(10));

        clock.Now = T0.AddHours(1);
        Assert.True(stats.SetIncludesBackground(false));
        Assert.False(stats.IncludesBackground);
        Assert.Empty(stats.Snapshot(10));
        Assert.Equal(T0.AddHours(1), stats.SinceUtc);
    }
}
