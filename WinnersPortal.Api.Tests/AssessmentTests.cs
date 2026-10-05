using WinnersPortal.Services.Auth;
using WinnersPortal.Services.Profiles;
using WinnersPortal.Services.Opportunities;
using WinnersPortal.Domain;
using Xunit;

namespace WinnersPortal.Api.Tests;

/// <summary>
/// The assessment on the entry box: four lines out of 100, their weighted
/// average as the match, and one sentence for the weakest of them.
/// </summary>
public class AssessmentTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

    private static ViewerFacts Facts(
        Dictionary<string, (SkillLevel, int)>? skills = null,
        IReadOnlyList<ProjectFacts>? projects = null,
        Availability? availability = null,
        int? hours = null,
        int due = 0,
        int onTime = 0,
        int inHand = 0) =>
        new(skills ?? new Dictionary<string, (SkillLevel, int)>(), projects ?? [], availability, hours, due, onTime, inHand);

    private static Assessment.Result Read(
        ViewerFacts f, IReadOnlyList<string>? skills = null, string? category = null, int? weeks = null) =>
        Assessment.Read(skills ?? [], category, true, f, Now, weeks is null ? null : Now.AddDays(weeks.Value * 7), Now);

    private static Factor Line(Assessment.Result r, string key) => r.Factors.Single(f => f.Key == key);

    private static readonly IReadOnlyList<ProjectFacts> ThreePythonProjects =
    [
        new(null, Assessment.Words("Python service")),
        new(null, Assessment.Words("Python again")),
        new(null, Assessment.Words("Python thrice")),
    ];

    // ------------------------------------------------------------- skills

    [Theory]
    [InlineData(SkillLevel.Beginner, 0, 55)]
    [InlineData(SkillLevel.Intermediate, 0, 75)]
    [InlineData(SkillLevel.Intermediate, 3, 81)]
    [InlineData(SkillLevel.Advanced, 5, 100)]
    [InlineData(SkillLevel.Expert, 0, 100)]
    [InlineData(SkillLevel.Expert, 20, 100)]
    public void A_skill_is_worth_its_level_with_up_to_five_years_on_top(SkillLevel level, int years, int expected) =>
        Assert.Equal(expected, Assessment.SkillScore(level, years));

    [Fact]
    public void The_skills_line_averages_the_required_skills_and_names_the_one_not_listed()
    {
        var f = Facts(new() { ["python"] = (SkillLevel.Expert, 6), ["pytorch"] = (SkillLevel.Advanced, 0) });
        var r = Read(f, ["Python", "PyTorch", "NLP"]);
        var line = Line(r, "skills");
        Assert.Equal("Expertise in the required skills", line.Label);
        Assert.Equal(63, line.Score); // (100 + 90 + 0) / 3
        Assert.Equal("Python expert, 6 years; PyTorch advanced; NLP not listed.", line.Detail);
    }

    [Fact]
    public void One_required_skill_names_the_line_after_itself()
    {
        var f = Facts(new() { ["python"] = (SkillLevel.Intermediate, 1) });
        var line = Line(Read(f, ["Python"]), "skills");
        Assert.Equal("Python expertise", line.Label);
        Assert.Equal(77, line.Score);
        Assert.Equal("Intermediate, 1 year on your profile.", line.Detail);
    }

    [Fact]
    public void A_brief_with_no_required_skills_leaves_the_skills_line_unread()
    {
        var line = Line(Read(Facts(new() { ["python"] = (SkillLevel.Expert, 6) })), "skills");
        Assert.Null(line.Score);
    }

    // ----------------------------------------------------------- projects

    [Fact]
    public void Past_work_counts_when_filed_under_the_category_or_naming_a_required_skill_as_a_whole_word()
    {
        var projects = new List<ProjectFacts>
        {
            new("data", Assessment.Words("Churn model", "Gradient boosting on billing data", null, "Python, XGBoost")),
            new("web", Assessment.Words("Shop front", "A storefront", "sole developer", "React, Node")),
            // "c" is a whole word nowhere in here: "react" must not count as C.
            new("web", Assessment.Words("Dashboard", null, null, "React")),
        };
        var r = Read(Facts(projects: projects), ["C", "Python"], "data");
        var line = Line(r, "projects");
        Assert.Equal(60, line.Score);
        Assert.Equal("1 of your 3 projects uses what this brief asks for.", line.Detail);

        // The category alone finds the same one.
        Assert.Equal(60, Line(Read(Facts(projects: projects), [], "data"), "projects").Score);

        Assert.Equal(80, Line(Read(Facts(projects: projects), ["React"]), "projects").Score);
        Assert.Equal(100, Line(Read(Facts(projects: projects), ["React", "Python"]), "projects").Score);
        Assert.Equal(0, Line(Read(Facts(projects: projects), ["Rust"]), "projects").Score);
    }

    [Fact]
    public void No_past_work_is_nought_and_a_brief_naming_no_kind_of_work_is_unread()
    {
        Assert.Equal(0, Line(Read(Facts(), ["Python"]), "projects").Score);
        Assert.Equal("No past work on your profile yet.", Line(Read(Facts(), ["Python"]), "projects").Detail);
        Assert.Null(Line(Read(Facts(projects: [new ProjectFacts("web", " shop ")])), "projects").Score);
    }

    // ------------------------------------------------------------- record

    [Fact]
    public void The_record_is_on_time_over_due_and_unread_until_something_is_due()
    {
        Assert.Null(Line(Read(Facts()), "record").Score);
        var line = Line(Read(Facts(due: 8, onTime: 7)), "record");
        Assert.Equal(88, line.Score);
        Assert.Equal("7 of 8 dated milestones claimed on time.", line.Detail);
    }

    // ------------------------------------------------------- availability

    [Fact]
    public void Availability_is_unread_until_something_is_said_and_reads_the_start_the_hours_and_the_load()
    {
        Assert.Null(Line(Read(Facts()), "availability").Score);

        var now = Line(Read(Facts(availability: Availability.Now, hours: 35), weeks: 10), "availability");
        Assert.Equal(100, now.Score);
        Assert.Equal("Available now, 35 hours a week for a 10-week timeline.", now.Detail);

        // Ten hours a week is three quarters of the week's worth; an
        // opportunity in hand costs eight.
        Assert.Equal(75, Line(Read(Facts(availability: Availability.Now, hours: 10)), "availability").Score);
        Assert.Equal(84, Line(Read(Facts(availability: Availability.Now, hours: 30, inHand: 2)), "availability").Score);
        // Free within two weeks of a two-week timeline is most of it gone.
        Assert.Equal(30, Line(Read(Facts(availability: Availability.WithinTwoWeeks, hours: 30), weeks: 2), "availability").Score);
        Assert.Equal(70, Line(Read(Facts(availability: Availability.WithinTwoWeeks, hours: 30), weeks: 8), "availability").Score);
        // Hours alone, nothing said about the start.
        Assert.Equal(80, Line(Read(Facts(hours: 40)), "availability").Score);
    }

    // -------------------------------------------------------- the average

    [Fact]
    public void The_match_weights_the_skills_double_and_leaves_unread_lines_out()
    {
        var f = Facts(
            new() { ["python"] = (SkillLevel.Expert, 6) },
            [new ProjectFacts(null, Assessment.Words("Python service"))],
            Availability.Now, 40, due: 4, onTime: 4);
        // 100, 60, 100, 100 at 40/20/20/20 → 92.
        Assert.Equal(92, Read(f, ["Python"], weeks: 6).Match);

        // Record and availability unread: (40×100 + 20×60)/60 → 87.
        var half = Facts(new() { ["python"] = (SkillLevel.Expert, 6) }, [new ProjectFacts(null, Assessment.Words("Python service"))]);
        Assert.Equal(87, Read(half, ["Python"]).Match);
    }

    [Fact]
    public void Outside_their_kind_of_work_is_nought_whatever_the_lines_say()
    {
        var f = Facts(new() { ["python"] = (SkillLevel.Expert, 6) }, availability: Availability.Now, hours: 40);
        var r = Assessment.Read(["Python"], "games", false, f, Now, null, Now);
        Assert.Equal(0, r.Match);
        Assert.Equal(100, Line(r, "skills").Score);
    }

    // ------------------------------------------------------------ the risk

    [Fact]
    public void The_risk_is_one_sentence_for_the_weakest_line_and_nothing_when_every_line_is_strong()
    {
        var strong = Facts(new() { ["python"] = (SkillLevel.Expert, 6) }, ThreePythonProjects,
            Availability.Now, 40, due: 3, onTime: 3);
        Assert.Null(Read(strong, ["Python"]).Risk);

        var thin = Facts(new() { ["python"] = (SkillLevel.Expert, 6), ["nlp"] = (SkillLevel.Beginner, 0) },
            ThreePythonProjects, Availability.Now, 40);
        Assert.Equal(
            "Your NLP is listed as beginner, lighter than the brief asks for; say what you have built with it in your note.",
            Read(thin, ["Python", "NLP"]).Risk);

        var noWork = Facts(new() { ["python"] = (SkillLevel.Expert, 6) }, availability: Availability.Now, hours: 40);
        Assert.Equal(
            "Nothing on your profile shows this kind of work; describe a relevant project in your note.",
            Read(noWork, ["Python"]).Risk);

        var late = Facts(new() { ["python"] = (SkillLevel.Expert, 6) }, ThreePythonProjects,
            Availability.Now, 40, due: 4, onTime: 2);
        Assert.Equal(
            "Your on-time record is 50%; a milestone plan in your note will reassure the client.",
            Read(late, ["Python"]).Risk);

        var busy = Facts(new() { ["python"] = (SkillLevel.Expert, 6) }, ThreePythonProjects,
            Availability.Now, 12, due: 4, onTime: 4);
        Assert.Equal(
            "12 hours a week is thin for a 6-week timeline; confirm your hours in your note.",
            Read(busy, ["Python"], weeks: 6).Risk);
    }

    // ---------------------------------------------------------- the words

    [Fact]
    public void Words_fold_text_to_whole_lower_case_words_with_a_space_at_each_end()
    {
        Assert.Equal(" react native node ", Assessment.Words("React   Native", null, "(Node)"));
        Assert.Equal("  ", Assessment.Words(null, "   "));
    }

    [Fact]
    public void The_timeline_is_whole_weeks_rounded_up_from_publish_to_deadline()
    {
        Assert.Equal(1, Assessment.TimelineWeeks(Now, Now.AddDays(3)));
        Assert.Equal(2, Assessment.TimelineWeeks(Now, Now.AddDays(8)));
        Assert.Equal(10, Assessment.TimelineWeeks(Now, Now.AddDays(70)));
        Assert.Null(Assessment.TimelineWeeks(Now, null));
        Assert.Null(Assessment.TimelineWeeks(Now, Now.AddDays(-1)));
    }
}
