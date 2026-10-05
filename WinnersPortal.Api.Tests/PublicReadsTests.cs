using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using WinnersPortal.Services.Common;
using WinnersPortal.Services.Opportunities;
using WinnersPortal.Services.Settings;
using Xunit;

namespace WinnersPortal.Api.Tests;

/// <summary>
/// The public lists' cache (Common/PublicReads.cs): what it keeps and for
/// how long, that a crowd arriving together shares one read, that a read
/// which fails or a caller who leaves harms nobody else, the ceiling on
/// what it holds, a clear heard from another process over Redis, and the
/// setting that sets its lifetime.
/// </summary>
public class PublicReadsTests
{
    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly DateTimeOffset T0 = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Ten = TimeSpan.FromSeconds(10);

    // Only the lifetime-taking overload is under test, which needs neither
    // the settings nor a scope: those are the thin wrapper around it.
    private static (PublicReads Reads, Clock Clock) Make()
    {
        var clock = new Clock(T0);
        return (new PublicReads(null!, null!, clock: clock), clock);
    }

    /// <summary>A read that counts how often it ran, and answers a new object each time.</summary>
    private sealed class Counted
    {
        public int Runs;
        public Task<List<int>> Read() { Interlocked.Increment(ref Runs); return Task.FromResult(new List<int> { Runs }); }
    }

    [Fact]
    public async Task A_second_caller_within_the_lifetime_is_answered_from_the_first_read()
    {
        var (reads, clock) = Make();
        var db = new Counted();
        var first = await reads.GetAsync("k", Ten, db.Read, CancellationToken.None);
        clock.Now = T0.AddSeconds(9);
        var second = await reads.GetAsync("k", Ten, db.Read, CancellationToken.None);
        Assert.Same(first, second);
        Assert.Equal(1, db.Runs);
    }

    [Fact]
    public async Task Once_the_lifetime_has_passed_the_next_caller_reads_again()
    {
        var (reads, clock) = Make();
        var db = new Counted();
        await reads.GetAsync("k", Ten, db.Read, CancellationToken.None);
        clock.Now = T0.AddSeconds(10);
        var again = await reads.GetAsync("k", Ten, db.Read, CancellationToken.None);
        Assert.Equal(2, db.Runs);
        Assert.Equal([2], again);
    }

    [Fact]
    public async Task Different_questions_are_read_and_held_apart()
    {
        var (reads, _) = Make();
        var db = new Counted();
        await reads.GetAsync("a", Ten, db.Read, CancellationToken.None);
        await reads.GetAsync("b", Ten, db.Read, CancellationToken.None);
        await reads.GetAsync("a", Ten, db.Read, CancellationToken.None);
        Assert.Equal(2, db.Runs);
    }

    [Fact]
    public async Task Callers_who_arrive_during_a_read_wait_for_it_rather_than_starting_their_own()
    {
        var (reads, _) = Make();
        var gate = new TaskCompletionSource<List<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = 0;
        Task<List<int>> Read() { Interlocked.Increment(ref runs); return gate.Task; }

        var callers = Enumerable.Range(0, 20)
            .Select(_ => reads.GetAsync("k", Ten, Read, CancellationToken.None))
            .ToList();
        gate.SetResult([7]);
        var answers = await Task.WhenAll(callers);

        Assert.Equal(1, runs);
        Assert.All(answers, a => Assert.Same(answers[0], a));
    }

    [Fact]
    public async Task A_read_slower_than_the_lifetime_is_still_shared_rather_than_doubled()
    {
        var (reads, clock) = Make();
        var gate = new TaskCompletionSource<List<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = 0;
        Task<List<int>> Read() { Interlocked.Increment(ref runs); return gate.Task; }

        var early = reads.GetAsync("k", Ten, Read, CancellationToken.None);
        clock.Now = T0.AddSeconds(30);
        var late = reads.GetAsync("k", Ten, Read, CancellationToken.None);
        gate.SetResult([1]);

        Assert.Same(await early, await late);
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task A_read_that_fails_is_not_kept_and_the_next_caller_reads_again()
    {
        var (reads, _) = Make();
        var runs = 0;
        Task<List<int>> Read() => ++runs == 1
            ? Task.FromException<List<int>>(new InvalidOperationException("the database went away"))
            : Task.FromResult(new List<int> { runs });

        await Assert.ThrowsAsync<InvalidOperationException>(() => reads.GetAsync("k", Ten, Read, CancellationToken.None));
        var answer = await reads.GetAsync("k", Ten, Read, CancellationToken.None);

        Assert.Equal([2], answer);
        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task A_caller_who_stops_waiting_does_not_cancel_the_read_for_anyone_else()
    {
        var (reads, _) = Make();
        var gate = new TaskCompletionSource<List<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = 0;
        Task<List<int>> Read() { Interlocked.Increment(ref runs); return gate.Task; }

        using var leaving = new CancellationTokenSource();
        var gone = reads.GetAsync("k", Ten, Read, leaving.Token);
        var staying = reads.GetAsync("k", Ten, Read, CancellationToken.None);
        leaving.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gone);

        gate.SetResult([3]);
        Assert.Equal([3], await staying);
        // And what that read found is held for whoever comes next.
        Assert.Same(await staying, await reads.GetAsync("k", Ten, Read, CancellationToken.None));
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task Clear_sends_the_next_caller_to_the_database()
    {
        var (reads, _) = Make();
        var db = new Counted();
        await reads.GetAsync("k", Ten, db.Read, CancellationToken.None);
        reads.Clear();
        await reads.GetAsync("k", Ten, db.Read, CancellationToken.None);
        Assert.Equal(2, db.Runs);
    }

    [Fact]
    public async Task A_clear_heard_from_another_process_empties_this_one()
    {
        var (reads, _) = Make();
        var db = new Counted();
        await reads.GetAsync("k", Ten, db.Read, CancellationToken.None);
        reads.Heard(Guid.NewGuid().ToString("N"));
        Assert.Equal(0, reads.Count);
        await reads.GetAsync("k", Ten, db.Read, CancellationToken.None);
        Assert.Equal(2, db.Runs);
    }

    [Fact]
    public async Task Its_own_clear_coming_back_over_the_channel_is_ignored()
    {
        // Whatever was read between the clear and its echo is already fresh.
        var (reads, _) = Make();
        var db = new Counted();
        reads.Clear();
        await reads.GetAsync("k", Ten, db.Read, CancellationToken.None);
        reads.Heard(reads.Instance);
        Assert.Equal(1, reads.Count);
    }

    [Fact]
    public async Task With_Redis_set_but_unreachable_it_neither_throws_nor_waits()
    {
        // Nothing listens on port 1. Connecting is RedisConnection's own
        // business and its own timeout; what is measured here is that the
        // cache's subscribe and its clear add nothing to it, so a Redis that
        // is down never holds up a start or the write that clears.
        using var redis = new RedisConnection(new RedisSettings.Choice(true, "127.0.0.1:1", "test"), NullLogger<RedisConnection>.Instance);
        Assert.NotNull(redis.Muxer);

        var clock = Stopwatch.StartNew();
        var reads = new PublicReads(null!, null!, redis, NullLogger<PublicReads>.Instance, new Clock(T0));
        var db = new Counted();
        await reads.GetAsync("k", Ten, db.Read, CancellationToken.None);
        reads.Clear();
        clock.Stop();

        Assert.Equal(0, reads.Count);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"took {clock.Elapsed}");
    }

    [Fact]
    public async Task Past_the_ceiling_the_expired_answers_go_first()
    {
        var (reads, clock) = Make();
        var db = new Counted();
        for (var i = 0; i < PublicReads.MaxEntries; i++)
            await reads.GetAsync(i, Ten, db.Read, CancellationToken.None);
        clock.Now = T0.AddSeconds(11);
        await reads.GetAsync("one more", Ten, db.Read, CancellationToken.None);
        Assert.Equal(1, reads.Count);
    }

    [Fact]
    public async Task Past_the_ceiling_with_nothing_expired_everything_goes()
    {
        var (reads, _) = Make();
        var db = new Counted();
        for (var i = 0; i <= PublicReads.MaxEntries; i++)
            await reads.GetAsync(i, Ten, db.Read, CancellationToken.None);
        Assert.True(reads.Count <= PublicReads.MaxEntries);
    }

    [Fact]
    public void The_feeds_key_is_what_was_asked_and_not_when()
    {
        // The feed keys its rows by the query less its clock: two visitors
        // asking for the same page a second apart share one read.
        var asked = new OpportunityCards.FeedQuery(true, "inventory", "software", null, false, false,
            new Cursor(T0, Guid.Empty), 21, T0);
        var later = asked with { Now = T0.AddSeconds(1) };
        Assert.Equal(("opportunities.feed", asked with { Now = default }), ("opportunities.feed", later with { Now = default }));
        Assert.NotEqual(("opportunities.feed", asked with { Now = default }),
            ("opportunities.feed", later with { Q = "invoice", Now = default }));
    }

    [Theory]
    [InlineData("10", 10)]
    [InlineData(" 0 ", 0)]
    [InlineData("300", 300)]
    public void The_setting_takes_whole_seconds_up_to_five_minutes(string raw, int expected) =>
        Assert.Equal(expected, PublicReads.ParseSeconds(raw));

    [Theory]
    [InlineData("-1")]
    [InlineData("301")]
    [InlineData("2.5")]
    [InlineData("ten")]
    public void A_lifetime_that_is_not_whole_seconds_in_range_is_refused_on_save(string value)
    {
        Assert.Null(PublicReads.ParseSeconds(value));
        var problem = SettingsService.ValueProblem(SettingsRegistry.Find(PublicReads.SettingKey)!, value);
        Assert.NotNull(problem);
        Assert.Contains("0 turns it off", problem);
    }

    [Theory]
    [InlineData("30")]
    [InlineData("0")]
    [InlineData("")]
    [InlineData(null)]
    public void A_lifetime_in_range_or_blank_for_the_default_is_accepted(string? value) =>
        Assert.Null(SettingsService.ValueProblem(SettingsRegistry.Find(PublicReads.SettingKey)!, value));

    [Fact]
    public void The_setting_sits_under_limits_with_the_default_the_code_falls_back_to()
    {
        var def = SettingsRegistry.Find(PublicReads.SettingKey)!;
        Assert.Equal("limits", def.Group);
        Assert.Equal(PublicReads.DefaultSeconds, PublicReads.ParseSeconds(def.Default));
    }
}
