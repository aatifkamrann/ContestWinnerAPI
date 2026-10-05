using WinnersPortal.Services.Reports;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WinnersPortal.Services.Opportunities;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Domain;
using WinnersPortal.Services.Help;
using Xunit;

namespace WinnersPortal.Api.Tests;

/// <summary>
/// The reports: one row per opportunity, application or entry, scoped to
/// whoever is asking, to sort, group, total and take away. Each query is
/// held to the same rule as the activity read — it must render to SQL from
/// the model alone, in every scope — and the arithmetic is held to what a
/// reader would expect of it.
/// </summary>
public class ReportTests
{
    private static AppDbContext Db() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=nowhere;Database=none;Username=x;Password=x")
            .Options);

    [Fact]
    public void An_administrators_report_is_every_opportunity_with_the_client_joined()
    {
        // No database: EF renders the SQL from the model, which catches a
        // projection it cannot translate — the skills list off the child
        // table, the client's name and email off the join.
        using var db = Db();

        var sql = ReportService.Opportunities(db, ReportService.Scope.All, Guid.NewGuid()).ToQueryString();

        Assert.Contains("\"Opportunities\"", sql);
        Assert.Contains("\"Users\"", sql);
        Assert.Contains("\"OpportunitySkills\"", sql);
        Assert.Contains("\"DisplayName\"", sql);
        Assert.Contains("\"Email\"", sql);
        Assert.Contains("\"CancelledAtUtc\"", sql);
        Assert.Contains("ORDER BY", sql);
        // Nobody's in particular — no account is a parameter — so drafts
        // and cancellations are rows too. (The client join is c."ClientId" = u."Id".)
        Assert.DoesNotContain("= @", sql);
    }

    [Fact]
    public void A_clients_report_is_what_they_posted_and_a_freelancers_what_they_applied_to_or_entered()
    {
        using var db = Db();
        var me = Guid.NewGuid();

        var posted = ReportService.Opportunities(db, ReportService.Scope.Posted, me).ToQueryString();
        Assert.Contains("\"ClientId\" = @", posted);

        var entered = ReportService.Opportunities(db, ReportService.Scope.Entered, me).ToQueryString();
        Assert.Contains("\"Applications\"", entered);
        Assert.Contains("\"Entries\"", entered);
        Assert.Contains("\"FreelancerId\" = @", entered);
        Assert.DoesNotContain("\"ClientId\" = @", entered);
    }

    [Theory]
    [InlineData("admin", ReportService.Scope.All)]
    [InlineData("client", ReportService.Scope.Posted)]
    [InlineData("freelancer", ReportService.Scope.Entered)]
    [InlineData(null, ReportService.Scope.Entered)]
    public void The_scope_follows_the_role(string? role, ReportService.Scope expected) =>
        Assert.Equal(expected, ReportService.ScopeOf(role));

    [Theory]
    [InlineData(ApplicationStatus.UnderReview, "under_review")]
    [InlineData(ApplicationStatus.Selected, "selected")]
    [InlineData(ApplicationStatus.NotSelected, "not_selected")]
    [InlineData(ApplicationStatus.Removed, "removed")]
    [InlineData(ApplicationStatus.Withdrawn, "withdrawn")]
    public void A_freelancers_part_reads_as_their_application_does(ApplicationStatus status, string expected) =>
        Assert.Equal(expected, ReportService.Part(status));

    [Theory]
    [InlineData(EntryStatus.Active, "selected")]
    [InlineData(EntryStatus.Deselected, "not_selected")]
    [InlineData(EntryStatus.Removed, "removed")]
    [InlineData(EntryStatus.Withdrawn, "withdrawn")]
    public void An_entry_without_an_application_reads_the_same_way(EntryStatus status, string expected) =>
        Assert.Equal(expected, ReportService.Part(status));

    [Fact]
    public void Duration_is_start_to_deadline_in_days_and_never_negative()
    {
        var start = new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);

        Assert.Equal(14.0, ReportService.DurationDays(start, start.AddDays(14)));
        Assert.Equal(2.5, ReportService.DurationDays(start, start.AddHours(60)));
        Assert.Equal(0, ReportService.DurationDays(start, start.AddDays(-1)));
        Assert.Null(ReportService.DurationDays(null, start));
        Assert.Null(ReportService.DurationDays(start, null));
    }

    [Fact]
    public void The_report_explains_itself_to_every_role_and_reads_only()
    {
        Assert.Contains("reports.opportunities", HelpRegistry.RequiredReportTopicIds);
        var topic = HelpRegistry.Find("reports.opportunities");
        Assert.NotNull(topic);
        Assert.Contains("export", topic!.Detail!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("freelancer", topic.Detail!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Reading only", topic.Why!);
    }

    [Fact]
    public void An_administrators_applications_report_is_every_application_with_its_applicant_opportunity_and_client()
    {
        using var db = Db();

        var sql = ReportService.Applications(db, ReportService.Scope.All, Guid.NewGuid()).ToQueryString();

        Assert.Contains("\"Applications\"", sql);
        Assert.Contains("\"Opportunities\"", sql);
        Assert.Contains("\"Users\"", sql);
        Assert.Contains("\"Email\"", sql);
        Assert.Contains("\"EvaluationJson\"", sql);
        Assert.Contains("\"Advantages\"", sql);
        Assert.Contains("ORDER BY", sql);
        // Nobody's in particular: the joins compare columns, never a parameter.
        Assert.DoesNotContain("= @", sql);
    }

    [Fact]
    public void A_clients_applications_are_the_ones_to_their_opportunities_and_a_freelancers_their_own()
    {
        using var db = Db();
        var me = Guid.NewGuid();

        var posted = ReportService.Applications(db, ReportService.Scope.Posted, me).ToQueryString();
        Assert.Contains("\"ClientId\" = @", posted);
        Assert.DoesNotContain("\"FreelancerId\" = @", posted);

        var entered = ReportService.Applications(db, ReportService.Scope.Entered, me).ToQueryString();
        Assert.Contains("\"FreelancerId\" = @", entered);
        Assert.DoesNotContain("\"ClientId\" = @", entered);
    }

    [Fact]
    public void An_answer_counts_from_the_selection_and_not_from_whatever_happened_after_it()
    {
        var selected = new DateTimeOffset(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);
        var later = selected.AddDays(4);

        Assert.Null(ReportService.AnsweredAt(ApplicationStatus.UnderReview, null, null));
        Assert.Equal(later, ReportService.AnsweredAt(ApplicationStatus.NotSelected, later, null));
        Assert.Equal(selected, ReportService.AnsweredAt(ApplicationStatus.Selected, selected, selected));
        // The decided stamp moved on to the withdrawal, the removal, the selection taken back.
        Assert.Equal(selected, ReportService.AnsweredAt(ApplicationStatus.Withdrawn, later, selected));
        Assert.Equal(selected, ReportService.AnsweredAt(ApplicationStatus.Removed, later, selected));
        Assert.Equal(selected, ReportService.AnsweredAt(ApplicationStatus.NotSelected, later, selected));
    }

    [Theory]
    [InlineData(ApplicationStatus.Selected, OpportunityStatus.Open, false, "open")]
    [InlineData(ApplicationStatus.Selected, OpportunityStatus.Awarded, true, "won")]
    [InlineData(ApplicationStatus.Selected, OpportunityStatus.Awarded, false, "lost")]
    [InlineData(ApplicationStatus.UnderReview, OpportunityStatus.Reviewing, false, "unanswered")]
    [InlineData(ApplicationStatus.UnderReview, OpportunityStatus.Open, false, null)]
    [InlineData(ApplicationStatus.NotSelected, OpportunityStatus.Awarded, false, null)]
    [InlineData(ApplicationStatus.Withdrawn, OpportunityStatus.Cancelled, false, null)]
    public void A_result_is_what_came_of_a_selection_or_of_an_application_nobody_answered(
        ApplicationStatus status, OpportunityStatus opportunity, bool won, string? expected) =>
        Assert.Equal(expected, ReportService.Result(status, opportunity, won));

    [Fact]
    public void The_evaluations_word_is_read_as_it_was_filed()
    {
        var filed = JsonSerializer.Serialize(new Evaluation(84, "Strong Candidate", [], []));

        Assert.Equal("Strong Candidate", ReportService.Band(filed));
        Assert.Null(ReportService.Band("{}"));
        Assert.Null(ReportService.Band("not json"));
    }

    [Fact]
    public void The_applications_report_explains_itself_to_every_role_and_reads_only()
    {
        Assert.Contains("reports.applications", HelpRegistry.RequiredReportTopicIds);
        var topic = HelpRegistry.Find("reports.applications");
        Assert.NotNull(topic);
        Assert.Contains("export", topic!.Detail!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("freelancer", topic.Detail!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("email", topic.Detail!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Reading only", topic.Why!);
    }

    [Fact]
    public void An_administrators_entries_report_is_every_entry_with_its_claims_and_its_entrant_opportunity_and_client()
    {
        using var db = Db();

        var sql = ReportService.Entries(db, ReportService.Scope.All, Guid.NewGuid()).ToQueryString();

        Assert.Contains("\"Entries\"", sql);
        Assert.Contains("\"Opportunities\"", sql);
        Assert.Contains("\"Users\"", sql);
        Assert.Contains("\"Checkpoints\"", sql);
        Assert.Contains("\"Milestones\"", sql);
        Assert.Contains("\"Submissions\"", sql);
        Assert.Contains("\"Email\"", sql);
        Assert.Contains("ORDER BY", sql);
        // Nobody's in particular, and every state: no account and no status is a filter.
        Assert.DoesNotContain("= @", sql);
        Assert.DoesNotContain("\"Status\" <> ", sql);
    }

    [Fact]
    public void A_clients_entries_are_the_ones_into_their_opportunities_and_a_freelancers_their_own_bar_a_selection_taken_back()
    {
        using var db = Db();
        var me = Guid.NewGuid();

        var posted = ReportService.Entries(db, ReportService.Scope.Posted, me).ToQueryString();
        Assert.Contains("\"ClientId\" = @", posted);
        Assert.DoesNotContain("\"FreelancerId\" = @", posted);
        Assert.DoesNotContain("\"Status\" <> ", posted);

        var entered = ReportService.Entries(db, ReportService.Scope.Entered, me).ToQueryString();
        Assert.Contains("\"FreelancerId\" = @", entered);
        Assert.DoesNotContain("\"ClientId\" = @", entered);
        Assert.Contains("\"Status\" <> 3", entered);
    }

    [Theory]
    [InlineData(EntryStatus.Active, OpportunityStatus.Open, false, "open")]
    [InlineData(EntryStatus.Active, OpportunityStatus.Reviewing, false, "reviewing")]
    [InlineData(EntryStatus.Active, OpportunityStatus.Awarded, true, "won")]
    [InlineData(EntryStatus.Active, OpportunityStatus.Awarded, false, "lost")]
    [InlineData(EntryStatus.Active, OpportunityStatus.Cancelled, false, "cancelled")]
    [InlineData(EntryStatus.Withdrawn, OpportunityStatus.Awarded, false, null)]
    [InlineData(EntryStatus.Removed, OpportunityStatus.Reviewing, false, null)]
    [InlineData(EntryStatus.Deselected, OpportunityStatus.Open, false, null)]
    public void An_entrys_result_is_what_came_of_it_while_it_was_still_in(
        EntryStatus status, OpportunityStatus opportunity, bool won, string? expected) =>
        Assert.Equal(expected, ReportService.EntryResult(status, opportunity, won));

    [Fact]
    public void A_board_is_read_as_it_stood_when_the_entrant_left()
    {
        var entered = new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);
        var left = entered.AddDays(5);
        var deadline = entered.AddDays(20);
        var now = entered.AddDays(30);
        var milestones = new List<ReportService.MilestoneDue>
        {
            new(0, entered.AddDays(2)),  // claimed the day before its date: on time
            new(1, entered.AddDays(4)),  // came due before they left, never claimed: overdue
            new(2, entered.AddDays(10)), // came due after they left: not held against them
            new(3, null),                // no date: claimed or not, never overdue
        };
        var claims = new List<ReportService.ClaimRow> { new() { Order = 0, ClaimedAtUtc = entered.AddDays(1) } };

        var ended = ReportService.EndedAt(EntryStatus.Withdrawn, left, null, null);
        Assert.Equal(left, ended);
        var asOf = ReportService.AsOf(now, deadline, null, ended);
        Assert.Equal(left, asOf);

        var board = ReportService.Board(milestones, claims, asOf);
        Assert.Equal(1, board.Done);
        Assert.Equal(1, board.OnTime);
        Assert.Equal(0, board.Late);
        Assert.Equal(1, board.Overdue);

        // Still in, past the deadline: read at the deadline, where both unclaimed dates are owed.
        var stillIn = ReportService.AsOf(now, deadline, null, ReportService.EndedAt(EntryStatus.Active, null, null, null));
        Assert.Equal(deadline, stillIn);
        Assert.Equal(2, ReportService.Board(milestones, claims, stillIn).Overdue);
        // A cancellation stops the clock too, and nothing in the future moves it.
        Assert.Equal(left, ReportService.AsOf(now, deadline, left, null));
        Assert.Equal(now, ReportService.AsOf(now, now.AddDays(3), null, null));

        // A removal ends at the removal; a selection taken back at the answer that took it back.
        Assert.Equal(left, ReportService.EndedAt(EntryStatus.Removed, null, left, null));
        Assert.Equal(left, ReportService.EndedAt(EntryStatus.Deselected, null, null, left));
    }

    [Fact]
    public void The_repository_shows_what_its_last_listing_found_and_nothing_before_one()
    {
        Assert.Null(ReportService.RepoHas(null, null, null, null));
        Assert.Empty(ReportService.RepoHas(12, false, false, false)!);
        Assert.Equal(new[] { "Tests", "CI" }, ReportService.RepoHas(40, true, false, true)!);
    }

    [Fact]
    public void The_entries_report_explains_itself_to_every_role_and_reads_only()
    {
        Assert.Contains("reports.entries", HelpRegistry.RequiredReportTopicIds);
        var topic = HelpRegistry.Find("reports.entries");
        Assert.NotNull(topic);
        Assert.Contains("export", topic!.Detail!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("freelancer", topic.Detail!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("email", topic.Detail!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("overdue", topic.Detail!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Reading only", topic.Why!);
    }
}
