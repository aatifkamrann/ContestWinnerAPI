using WinnersPortal.Domain;
using WinnersPortal.Services.Email;
using WinnersPortal.Services.Preview;
using Xunit;

namespace WinnersPortal.Api.Tests;

/// <summary>
/// The rules of a claimed milestone's build: the worker's patience with
/// the host, what of the log the row keeps, who may read it, and what the
/// entrant is told.
/// </summary>
public class CheckpointBuildsTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-24T12:00:00Z");

    [Fact]
    public void The_host_is_tried_again_with_backoff_that_stops_at_an_hour()
    {
        Assert.Equal(TimeSpan.FromMinutes(2), CheckpointBuilds.Backoff(1));
        Assert.Equal(TimeSpan.FromMinutes(4), CheckpointBuilds.Backoff(2));
        Assert.Equal(TimeSpan.FromMinutes(32), CheckpointBuilds.Backoff(5));
        Assert.Equal(TimeSpan.FromMinutes(60), CheckpointBuilds.Backoff(9));
        Assert.Equal(6, CheckpointBuilds.MaxAttempts);
    }

    [Fact]
    public void A_build_the_host_still_calls_running_is_given_up_past_its_timeout_and_the_grace()
    {
        var timeout = TimeSpan.FromMinutes(15);
        Assert.False(CheckpointBuilds.TimedOut(null, timeout, Now));
        Assert.False(CheckpointBuilds.TimedOut(Now - TimeSpan.FromMinutes(19), timeout, Now));
        Assert.True(CheckpointBuilds.TimedOut(Now - TimeSpan.FromMinutes(21), timeout, Now));
    }

    [Fact]
    public void The_row_keeps_the_last_sixty_lines_and_no_more_than_eight_kilobytes()
    {
        Assert.Null(CheckpointBuilds.Tail(null));
        Assert.Null(CheckpointBuilds.Tail("  \n"));
        Assert.Equal("one\ntwo", CheckpointBuilds.Tail("one\r\ntwo\r\n"));

        var hundred = string.Join('\n', Enumerable.Range(1, 100).Select(i => $"line {i}"));
        var tail = CheckpointBuilds.Tail(hundred)!;
        Assert.StartsWith("line 41\n", tail);
        Assert.EndsWith("line 100", tail);
        Assert.Equal(60, tail.Split('\n').Length);

        var wide = string.Join('\n', Enumerable.Range(1, 10).Select(_ => new string('x', 2000)));
        var cut = CheckpointBuilds.Tail(wide)!;
        Assert.Equal(CheckpointBuilds.TailChars, cut.Length);
        Assert.StartsWith("…", cut);
    }

    [Fact]
    public void The_log_lives_under_the_checkpoints_id_and_downloads_named_for_the_milestone_and_commit()
    {
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");
        Assert.Equal("builds/11111111222233334444555555555555.log", CheckpointBuilds.LogKey(id));
        Assert.Equal("build-m2-abc1234.log", CheckpointBuilds.LogFileName(2, "abc1234def5678"));
        Assert.Equal("build-m1-commit.log", CheckpointBuilds.LogFileName(1, null));
    }

    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, true, true)]
    [InlineData(false, false, false, false)]
    public void The_entrant_the_client_and_an_administrator_may_read_a_build(bool entrant, bool owner, bool admin, bool expected) =>
        Assert.Equal(expected, CheckpointBuilds.CanSee(entrant, owner, admin));

    [Fact]
    public void The_states_have_wire_names_the_board_reads()
    {
        Assert.Equal("none", CheckpointBuilds.StatusName(PreviewBuildStatus.None));
        Assert.Equal("pending", CheckpointBuilds.StatusName(PreviewBuildStatus.Pending));
        Assert.Equal("building", CheckpointBuilds.StatusName(PreviewBuildStatus.Building));
        Assert.Equal("built", CheckpointBuilds.StatusName(PreviewBuildStatus.Built));
        Assert.Equal("failed", CheckpointBuilds.StatusName(PreviewBuildStatus.Failed));
    }

    [Fact]
    public void The_words_on_the_row_say_whose_failure_it_was()
    {
        Assert.Contains("6 tries", CheckpointBuilds.Unreachable("No such host is known."));
        Assert.Contains("15 minutes", CheckpointBuilds.TimedOutError(TimeSpan.FromMinutes(15)));
        Assert.Equal("npm ci failed", CheckpointBuilds.Verdict(new PreviewHost.BuildAnswer("failed", null, null, 1, "npm ci failed", null)));
        Assert.Equal("docker compose build exited with 2.", CheckpointBuilds.Verdict(new PreviewHost.BuildAnswer("failed", null, null, 2, null, null)));
        Assert.Contains("without saying why", CheckpointBuilds.Verdict(new PreviewHost.BuildAnswer("failed", null, null, null, "  ", null)));
        Assert.InRange(CheckpointBuilds.Cut(new string('e', 900)).Length, 390, 400); // the 400-character column
        Assert.EndsWith("…", CheckpointBuilds.Cut(new string('e', 900)));
    }

    [Fact]
    public void The_entrant_is_told_what_failed_where_the_log_is_and_that_the_claim_stands()
    {
        var mail = Emails.BuildFailed("ERP", "erp", 2, "npm ci exited 1");
        Assert.Contains("Milestone 2", mail.Subject);
        Assert.Contains("npm ci exited 1", mail.TextBody);
        Assert.Contains("claim stands", mail.TextBody);
        Assert.Contains("log", mail.TextBody);
        Assert.Equal("/opportunities/erp", mail.ActionPath);
    }
}
