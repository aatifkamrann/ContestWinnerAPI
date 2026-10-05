using WinnersPortal.Services.Opportunities;
using WinnersPortal.Services.Profiles;
using Xunit;

namespace WinnersPortal.Api.Tests;

/// <summary>
/// The standing score — the number an opportunity board is ordered by and the
/// reading beside the remove button. It is public arithmetic about people
/// who staked work on a brief, so the properties that make it fair are
/// pinned: this opportunity outweighs the past, the past outweighs the typed,
/// an unreadable part is left out rather than counted as a miss, and a row
/// that has gone quiet here is not rescued by a record elsewhere.
/// </summary>
public class StandingTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-05T12:00:00Z");

    /// <summary>A row with everything going for it, on a repository opportunity.</summary>
    private static Standing.Facts Full() => new(
        Board: new Schedule.Standing(Done: 3, OnTime: 3, Late: 0, Overdue: 0, Dated: 3),
        Milestones: 3,
        DatedMilestones: 3,
        EnteredAtUtc: Now.AddDays(-10),
        LastActivityUtc: Now.AddHours(-5),
        AsOfUtc: Now,
        Frozen: false,
        UsesRepository: true,
        Repo: new Standing.RepoFacts(FileCount: 40, HasTests: true, HasReadme: true, HasCi: true),
        UsesUpload: false,
        FilesUploaded: 0,
        DocumentsUploaded: 0,
        OpportunitiesDecided: 4,
        OpportunitiesWon: 4,
        RatingCount: 5,
        RatingSum: 25,
        PortfolioScore: Merit.PortfolioMax,
        SkillsListed: 6,
        SkillsMatched: ["C#", "PostgreSQL", "Next.js"]);

    /// <summary>Somebody who joined an hour ago and has done nothing yet.</summary>
    private static Standing.Facts JustJoined() => Full() with
    {
        Board = new Schedule.Standing(0, 0, 0, 0, 0),
        EnteredAtUtc = Now.AddHours(-1),
        LastActivityUtc = null,
        Repo = null,
        OpportunitiesDecided = 0,
        OpportunitiesWon = 0,
        RatingCount = 0,
        RatingSum = 0,
        PortfolioScore = 0,
        SkillsListed = 0,
        SkillsMatched = [],
    };

    [Fact]
    public void A_row_with_everything_scores_a_hundred_and_the_parts_add_up_to_it()
    {
        var parts = Standing.Parts(Full());
        Assert.Equal(100, parts.Sum(p => p.Available));
        Assert.Equal(100, Standing.Score(parts));
        Assert.Equal(Standing.Band.OnTrack, Standing.BandOf(Full(), parts));
        foreach (var part in parts) Assert.InRange(part.Earned, 0, part.Available);
        Assert.Null(Standing.NextStep(Full(), parts)); // nothing left to do
    }

    [Fact]
    public void Builds_count_only_where_the_opportunity_requires_them_and_a_claim_has_finished_building()
    {
        // Without the rule the part is not there at all — not even as unreadable.
        Assert.DoesNotContain(Standing.Parts(Full()), p => p.Key == "builds");

        var required = Full() with { RequiresCompose = true };
        var nothingYet = Standing.Parts(required).Single(p => p.Key == "builds");
        Assert.Equal(0, nothingYet.Available);
        Assert.Equal(100, Standing.Score(Standing.Parts(required))); // not marked down for a build still to come

        var two = Standing.Parts(required with { BuildsFinished = 3, BuildsOk = 2 }).Single(p => p.Key == "builds");
        Assert.Equal(7, two.Earned);
        Assert.Equal(Standing.BuildsMax, two.Available);
        Assert.Contains("2 of 3", two.Detail);

        var all = Standing.Parts(required with { BuildsFinished = 2, BuildsOk = 2 }).Single(p => p.Key == "builds");
        Assert.Equal(Standing.BuildsMax, all.Earned);
        Assert.Contains("Every claimed milestone builds", all.Detail);
    }

    [Fact]
    public void Builds_that_fail_lower_the_standing_and_are_the_thing_to_fix()
    {
        // A row that is on track without the rule…
        var quiet = Full() with
        {
            Board = new Schedule.Standing(Done: 2, OnTime: 1, Late: 1, Overdue: 1, Dated: 3),
            LastActivityUtc = Now.AddDays(-10),
        };
        Assert.Equal(Standing.Band.OnTrack, Standing.BandOf(quiet, Standing.Parts(quiet)));

        // …reads as behind once every build of it has failed.
        var failing = quiet with { RequiresCompose = true, BuildsFinished = 2, BuildsOk = 0 };
        var parts = Standing.Parts(failing);
        Assert.Equal(Standing.Band.Behind, Standing.BandOf(failing, parts));
        Assert.Equal(0, parts.Single(p => p.Key == "builds").Earned);

        // And the fix is the first thing the entrant's box suggests.
        var perfectButBroken = Full() with { RequiresCompose = true, BuildsFinished = 1, BuildsOk = 0 };
        Assert.Contains("Fix the build", Standing.NextStep(perfectButBroken, Standing.Parts(perfectButBroken))!);
    }

    [Fact]
    public void This_opportunity_is_half_the_score_and_the_typed_quarter_is_the_smallest()
    {
        var parts = Standing.Parts(Full());
        int Of(params string[] keys) => parts.Where(p => keys.Contains(p.Key)).Sum(p => p.Available);
        Assert.Equal(50, Of("onTime", "progress", "activity", "quality"));
        Assert.Equal(25, Of("finished", "wins", "ratings"));
        Assert.Equal(25, Of("portfolio", "skills"));
        // …and on-time delivery is the single biggest line.
        Assert.Equal(parts.Max(p => p.Available), parts.Single(p => p.Key == "onTime").Available);
    }

    [Fact]
    public void Somebody_who_just_joined_reads_too_early_not_at_risk()
    {
        var facts = JustJoined();
        var parts = Standing.Parts(facts);
        Assert.Equal(Standing.Band.TooEarly, Standing.BandOf(facts, parts));
        Assert.Equal(0, Standing.Score(parts));
        // The dated milestones exist but none has come due: not a miss.
        Assert.Equal(0, parts.Single(p => p.Key == "onTime").Available);
        Assert.Contains("come due", parts.Single(p => p.Key == "onTime").Detail);
    }

    [Fact]
    public void What_cannot_be_read_leaves_the_denominator_instead_of_counting_as_a_miss()
    {
        // No dated milestones anywhere, and a repository the portal has
        // not read: two rows compared only on what is known about both.
        var facts = Full() with
        {
            Board = new Schedule.Standing(Done: 3, OnTime: 0, Late: 0, Overdue: 0, Dated: 0),
            DatedMilestones = 0,
            Repo = null,
        };
        var parts = Standing.Parts(facts);
        Assert.Equal(0, parts.Single(p => p.Key == "onTime").Available);
        Assert.Equal(0, parts.Single(p => p.Key == "quality").Available);
        Assert.Contains("Not readable", parts.Single(p => p.Key == "quality").Detail);
        // Everything readable is full, so the score is still a hundred.
        Assert.Equal(100, Standing.Score(parts));
        Assert.Equal(Standing.Band.OnTrack, Standing.BandOf(facts, parts));
    }

    [Fact]
    public void Two_overdue_milestones_with_nothing_claimed_is_at_risk_whatever_the_record_says()
    {
        var facts = Full() with
        {
            Board = new Schedule.Standing(Done: 0, OnTime: 0, Late: 0, Overdue: 2, Dated: 2),
        };
        var parts = Standing.Parts(facts);
        // A perfect record elsewhere and a perfect portfolio still score…
        Assert.True(Standing.Score(parts) > 50);
        // …but the reading is about this opportunity, and this opportunity is stalled.
        Assert.Equal(Standing.Band.AtRisk, Standing.BandOf(facts, parts));
    }

    [Fact]
    public void A_quiet_row_reads_behind_then_at_risk_as_the_silence_grows()
    {
        var parts = Standing.Parts(Full());
        Assert.Equal(Standing.ActivityMax, parts.Single(p => p.Key == "activity").Earned);

        var quiet = Full() with { LastActivityUtc = Now.AddDays(-20) };
        Assert.Equal(2, Standing.Parts(quiet).Single(p => p.Key == "activity").Earned);

        var silent = Full() with
        {
            LastActivityUtc = null,
            Board = new Schedule.Standing(Done: 0, OnTime: 0, Late: 0, Overdue: 1, Dated: 1),
            Repo = null,
        };
        var silentParts = Standing.Parts(silent);
        Assert.Equal(Standing.Band.AtRisk, Standing.BandOf(silent, silentParts));
        Assert.Contains("Nothing pushed", silentParts.Single(p => p.Key == "activity").Detail);
    }

    [Fact]
    public void A_frozen_row_is_read_as_it_stood_at_the_deadline()
    {
        var deadline = Now.AddDays(-30);
        var facts = Full() with
        {
            AsOfUtc = deadline,
            Frozen = true,
            LastActivityUtc = deadline.AddHours(-2),
        };
        var activity = Standing.Parts(facts).Single(p => p.Key == "activity");
        // Thirty days after the deadline the row is not "quiet" — it is finished.
        Assert.Equal(Standing.ActivityMax, activity.Earned);
        Assert.Contains("before the deadline", activity.Detail);
    }

    [Fact]
    public void Late_claims_count_half_and_the_counts_are_spelled_out()
    {
        var facts = Full() with
        {
            Board = new Schedule.Standing(Done: 3, OnTime: 1, Late: 2, Overdue: 1, Dated: 4),
            Milestones = 4,
            DatedMilestones = 4,
        };
        var onTime = Standing.Parts(facts).Single(p => p.Key == "onTime");
        Assert.Equal(10, onTime.Earned); // (1 + 2 × 0.5) / 4 of 20
        Assert.Equal("1 of 4 dated milestones met on time, 2 late, 1 overdue.", onTime.Detail);
    }

    [Fact]
    public void Code_and_docs_read_the_repository_where_there_is_one_and_the_files_where_there_is_not()
    {
        var repo = Standing.Parts(Full() with
        {
            Repo = new Standing.RepoFacts(FileCount: 3, HasTests: false, HasReadme: true, HasCi: false),
        }).Single(p => p.Key == "quality");
        Assert.Equal(3, repo.Earned);
        Assert.Contains("no tests and CI", repo.Detail);

        var upload = Full() with { UsesRepository = false, UsesUpload = true, Repo = null };
        Assert.Equal(0, Standing.Parts(upload).Single(p => p.Key == "quality").Earned);
        Assert.Equal(5, Standing.Parts(upload with { FilesUploaded = 4 }).Single(p => p.Key == "quality").Earned);
        var withNotes = Standing.Parts(upload with { FilesUploaded = 4, DocumentsUploaded = 1 })
            .Single(p => p.Key == "quality");
        Assert.Equal(Standing.QualityMax, withNotes.Earned);
        Assert.Contains("with a document", withNotes.Detail);

        // Both channels, repository unread: the files are the reading.
        var both = Full() with { UsesUpload = true, Repo = null, FilesUploaded = 2 };
        Assert.Equal(5, Standing.Parts(both).Single(p => p.Key == "quality").Earned);
    }

    [Fact]
    public void One_win_in_one_opportunity_is_not_a_hundred_percent_of_anything()
    {
        static int Wins(int decided, int won) => Standing.Parts(Full() with
        {
            OpportunitiesDecided = decided, OpportunitiesWon = won,
        }).Single(p => p.Key == "wins").Earned;

        Assert.Equal(0, Wins(0, 0));
        Assert.Equal(3, Wins(1, 1));   // a full ratio on a sample of one
        Assert.Equal(7, Wins(2, 2));
        Assert.Equal(10, Wins(3, 3));
        Assert.Equal(5, Wins(4, 2));   // half the opportunities, full confidence
        Assert.Contains("small sample", Standing.Parts(Full() with { OpportunitiesDecided = 1, OpportunitiesWon = 1 })
            .Single(p => p.Key == "wins").Detail);
    }

    [Fact]
    public void Finishing_opportunities_flattens_and_ratings_need_a_few_to_count_in_full()
    {
        static int Finished(int n) => Standing.Parts(Full() with { OpportunitiesDecided = n, OpportunitiesWon = 0 })
            .Single(p => p.Key == "finished").Earned;
        Assert.Equal(0, Finished(0));
        Assert.Equal(4, Finished(1));
        Assert.Equal(10, Finished(3));
        Assert.Equal(10, Finished(30));

        static int Ratings(int count, int sum) => Standing.Parts(Full() with { RatingCount = count, RatingSum = sum })
            .Single(p => p.Key == "ratings").Earned;
        Assert.Equal(0, Ratings(0, 0));
        Assert.Equal(2, Ratings(1, 5));   // one five-star rating is not a reputation
        Assert.Equal(5, Ratings(3, 15));
        Assert.Equal(3, Ratings(10, 30)); // three stars on average, plenty of them
    }

    [Fact]
    public void The_portfolio_is_the_merit_scores_written_half_scaled_down()
    {
        var full = Standing.Parts(Full()).Single(p => p.Key == "portfolio");
        Assert.Equal(Standing.PortfolioMax, full.Earned);
        // Ten of the twenty-five written points is six of fifteen here.
        var part = Standing.Parts(Full() with { PortfolioScore = 10 }).Single(p => p.Key == "portfolio");
        Assert.Equal(6, part.Earned);
    }

    [Fact]
    public void Skills_count_only_where_the_brief_names_them_and_only_a_few()
    {
        static Standing.Part Skills(int listed, params string[] matched) =>
            Standing.Parts(Full() with { SkillsListed = listed, SkillsMatched = matched }).Single(p => p.Key == "skills");

        Assert.Equal(0, Skills(0).Earned);
        Assert.Contains("No skills listed", Skills(0).Detail);
        Assert.Equal(0, Skills(8).Earned);
        Assert.Contains("None of the 8 skills", Skills(8).Detail);
        Assert.Equal(5, Skills(8, "React").Earned);
        Assert.Equal(8, Skills(8, "React", "Node.js").Earned);
        Assert.Equal(10, Skills(8, "React", "Node.js", "PostgreSQL", "Docker").Earned);
        Assert.Contains("React, Node.js", Skills(8, "React", "Node.js").Detail);
    }

    [Fact]
    public void Skills_match_the_brief_as_whole_words_case_insensitively()
    {
        var brief = "Build a React dashboard in C# and .NET 8 against PostgreSQL. Reactive extensions are not needed.";
        var matched = Standing.MatchSkills(
            ["react", "C#", ".NET", "postgresql", "Vue", "React", "  ", "Rx"], brief);
        Assert.Equal(["react", "C#", ".NET", "postgresql"], matched);
        // "Reactive" is not "React", and a skill listed twice matches once.
        Assert.DoesNotContain("Rx", matched);
        Assert.Empty(Standing.MatchSkills(["C#"], ""));
    }

    [Theory]
    [InlineData("text/markdown", "notes.md", true)]
    [InlineData("application/octet-stream", "brief.pdf", true)]
    [InlineData("application/pdf", "x", true)]
    [InlineData("image/svg+xml", "mark.svg", false)]
    [InlineData("application/zip", "pack.zip", false)]
    [InlineData(null, "README", false)]
    public void A_document_is_a_file_that_explains_work_rather_than_being_it(
        string? contentType, string fileName, bool expected) =>
        Assert.Equal(expected, Standing.IsDocument(contentType, fileName));

    [Fact]
    public void The_next_step_names_this_opportunities_gap_before_anything_typed()
    {
        // A thin portfolio and one milestone still open: the milestone comes first.
        var facts = Full() with
        {
            Board = new Schedule.Standing(Done: 2, OnTime: 2, Late: 0, Overdue: 0, Dated: 2),
            PortfolioScore = 5,
        };
        var step = Standing.NextStep(facts, Standing.Parts(facts));
        Assert.NotNull(step);
        Assert.Contains("milestone", step, StringComparison.OrdinalIgnoreCase);

        // Nothing left on this opportunity: the profile is the next thing to fix.
        var done = Full() with { PortfolioScore = 5 };
        Assert.Contains("profile", Standing.NextStep(done, Standing.Parts(done))!);
    }

    [Theory]
    [InlineData(Standing.Band.TooEarly, "too_early")]
    [InlineData(Standing.Band.OnTrack, "on_track")]
    [InlineData(Standing.Band.Behind, "behind")]
    [InlineData(Standing.Band.AtRisk, "at_risk")]
    public void Bands_have_stable_wire_names(Standing.Band band, string expected) =>
        Assert.Equal(expected, Standing.BandName(band));
}
