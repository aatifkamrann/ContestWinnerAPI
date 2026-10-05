using WinnersPortal.Services.Auth;
using WinnersPortal.Services.Opportunities;
using WinnersPortal.Domain;
using Xunit;

namespace WinnersPortal.Api.Tests;

/// <summary>
/// The door and the browse page's colours: the category registry, what a
/// freelancer's skills say about the kinds of work they do, and the
/// judgement of one person against one opportunity.
/// </summary>
public class OpportunityFitTests
{
    private static readonly IReadOnlySet<string> NoSkills = new HashSet<string>();
    private static readonly IReadOnlySet<string> NoCategories = new HashSet<string>();

    private static IReadOnlySet<string> Keys(params string[] skills) =>
        skills.Select(OpportunityFit.Key).ToHashSet();

    // --------------------------------------------------------- the registry

    [Fact]
    public void Every_key_is_unique_short_and_lower_case()
    {
        var keys = OpportunityCategories.All.Select(c => c.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
        foreach (var c in OpportunityCategories.All)
        {
            Assert.Equal(c.Key, c.Key.ToLowerInvariant());
            Assert.InRange(c.Key.Length, 1, OpportunityCategories.MaxKey);
            var subs = c.Subcategories.Select(s => s.Key).ToList();
            Assert.Equal(subs.Count, subs.Distinct(StringComparer.Ordinal).Count());
            foreach (var s in c.Subcategories)
            {
                Assert.Equal(s.Key, s.Key.ToLowerInvariant());
                Assert.InRange(s.Key.Length, 1, OpportunityCategories.MaxKey);
                Assert.NotEmpty(s.Label);
            }
        }
    }

    [Fact]
    public void Every_key_is_offered_to_the_model_and_only_those_are_accepted()
    {
        // The reading is validated against this set, so a key that is in the
        // registry and not in here would be an answer the portal refuses to
        // believe about its own taxonomy.
        Assert.Equal(
            OpportunityCategories.All.Select(c => c.Key).OrderBy(k => k, StringComparer.Ordinal),
            OpportunityCategories.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void Something_else_is_not_a_kind_anybody_specialises_in()
    {
        // It is the escape hatch on the form, so no opportunity under it is ever
        // called outside somebody’s usual work.
        Assert.False(OpportunityCategories.Judgeable(OpportunityCategories.Find("other")!));
        Assert.True(OpportunityCategories.Judgeable(OpportunityCategories.Find("web")!));
    }

    [Fact]
    public void A_category_is_found_by_key_whatever_the_case_and_a_subcategory_under_it()
    {
        var web = OpportunityCategories.Find(" Web ");
        Assert.NotNull(web);
        Assert.Equal("Web development", web!.Label);
        Assert.NotNull(OpportunityCategories.FindSub(web, "FRONTEND"));
        Assert.Null(OpportunityCategories.FindSub(web, "android"));
        Assert.Null(OpportunityCategories.Find("nope"));
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("web", null, null)]
    [InlineData("web", "frontend", null)]
    [InlineData("other", null, null)]
    [InlineData(null, "frontend", "Pick a category before a subcategory.")]
    [InlineData("nope", null, "Pick a category from the list.")]
    [InlineData("web", "android", "That subcategory is not under Web development — pick one from its list.")]
    public void A_draft_may_leave_the_category_blank_but_not_make_one_up(string? category, string? sub, string? expected) =>
        Assert.Equal(expected, OpportunityCategories.Problem(category, sub));

    [Fact]
    public void Publishing_insists_on_a_category()
    {
        Assert.NotNull(OpportunityCategories.PublishProblem(null));
        Assert.NotNull(OpportunityCategories.PublishProblem("nope"));
        Assert.Null(OpportunityCategories.PublishProblem("design"));
    }

    // ------------------------------------ what the portal knows about work

    // ---------------------------------------------------------- the match

    [Fact]
    public void The_match_is_the_assessment_averaged_with_skills_counting_double()
    {
        // Three skills, two of them listed with nothing said about depth:
        // 75 each and nought for the missing one, 50 on the skills line.
        // No past work is nought on the projects line; the record and the
        // availability are unread and leave the average. (40×50 + 20×0)/60.
        var fit = OpportunityFit.Judge(25, ["React", "Docker", "Postgres"], null, 50, Keys("React", "Postgres"), null);
        Assert.Equal(33, fit.Match);
        Assert.Equal("moderate", fit.MatchBand);
        Assert.False(fit.CanEnter);
        Assert.Equal(["skills", "projects", "record", "availability"], fit.Factors.Select(f => f.Key));
        Assert.Equal(50, fit.Factors[0].Score);
        Assert.Equal(0, fit.Factors[1].Score);
        Assert.Null(fit.Factors[2].Score);
        Assert.Null(fit.Factors[3].Score);

        // Nothing asked for and nothing known is nothing short of.
        var nothing = OpportunityFit.Judge(0, [], null, 0, NoSkills, null);
        Assert.Equal(100, nothing.Match);
        Assert.All(nothing.Factors, f => Assert.Null(f.Score));
        Assert.Null(nothing.Risk);

        // The merit floor is the door, not a line: short of it, the match
        // reads the same as over it.
        Assert.Equal(
            OpportunityFit.Judge(60, ["React"], null, 50, Keys("React"), null).Match,
            OpportunityFit.Judge(10, ["React"], null, 50, Keys("React"), null).Match);
    }

    [Fact]
    public void A_strong_match_can_still_be_a_shut_door()
    {
        // Four of five skills at expert with years behind them (80 on the
        // skills line) and three relevant projects (100): 87, strong — and
        // turned away, with the reason beside it. The ring is how near; the
        // door is the door.
        var facts = new ViewerFacts(
            new Dictionary<string, (SkillLevel, int)>
            {
                ["a"] = (SkillLevel.Expert, 6), ["b"] = (SkillLevel.Expert, 6),
                ["c"] = (SkillLevel.Expert, 6), ["d"] = (SkillLevel.Expert, 6),
            },
            [new ProjectFacts(null, Assessment.Words("Built with a")),
             new ProjectFacts(null, Assessment.Words("b and c")),
             new ProjectFacts(null, Assessment.Words("d again"))],
            null, null, 0, 0, 0);
        var fit = OpportunityFit.Judge(25, ["a", "b", "c", "d", "e"], null, 50, Keys("a", "b", "c", "d"), null, facts);
        Assert.Equal(87, fit.Match);
        Assert.Equal("strong", fit.MatchBand);
        Assert.False(fit.CanEnter);
        Assert.Single(fit.Reasons);
        // The weakest line is the skills, and the sentence names the gap.
        Assert.Equal("e is not on your profile, and the client requires it.", fit.Risk);
    }

    [Fact]
    public void Outside_their_kind_of_work_matches_nought_whatever_the_skills_say()
    {
        // A judgeable category the reading does not cover, no required skills.
        var fit = OpportunityFit.Judge(0, [], "games", 50, Keys("React"), NoCategories);
        Assert.False(fit.CategoryOk);
        Assert.Equal(0, fit.Match);
        Assert.Equal("outside", fit.MatchBand);
    }

    [Fact]
    public void With_no_reading_of_a_freelancer_nothing_is_outside_their_usual_work()
    {
        // AI off, or no provider key: there is nobody to judge the affinity,
        // so it is not judged. Anything they can enter, they are recommended.
        var fit = OpportunityFit.Judge(0, [], "games", 50, Keys("React"), null);
        Assert.True(fit.CategoryOk);
        Assert.True(fit.Recommended);
        Assert.Equal("fit", fit.Verdict);
        Assert.Empty(fit.Reasons);
    }

    [Fact]
    public void A_reading_that_covers_nothing_is_not_the_same_as_no_reading()
    {
        // An empty list is a real answer about a profile with nothing on it,
        // and it does colour the card amber.
        var fit = OpportunityFit.Judge(0, [], "games", 50, NoSkills, NoCategories);
        Assert.False(fit.CategoryOk);
        Assert.False(fit.Recommended);
        Assert.True(fit.CanEnter);
        Assert.Equal("outside", fit.Verdict);
    }

    // ---------------------------------------------------- the skill list

    [Fact]
    public void The_required_skills_are_tidied_and_blanks_dropped()
    {
        var list = OpportunityFit.CleanSkills(["  React ", "", null, "Entity   Framework"], out var problem);
        Assert.Null(problem);
        Assert.Equal(["React", "Entity Framework"], list);
    }

    [Fact]
    public void A_skill_listed_twice_is_refused_by_name()
    {
        Assert.Null(OpportunityFit.CleanSkills(["React", "react"], out var problem));
        Assert.Equal("“react” is in the required skills twice — one row each.", problem);
    }

    [Fact]
    public void Too_many_or_too_long_is_refused()
    {
        Assert.Null(OpportunityFit.CleanSkills(Enumerable.Range(0, OpportunityFit.MaxSkills + 1).Select(i => $"s{i}"), out var many));
        Assert.Contains("At most", many);
        Assert.Null(OpportunityFit.CleanSkills([new string('x', OpportunityFit.MaxSkillName + 1)], out var longOne));
        Assert.Contains("under", longOne);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(0, null)]
    [InlineData(100, null)]
    [InlineData(-1, "The minimum merit score is a number from 0 to 100; zero means no minimum.")]
    [InlineData(101, "The minimum merit score is a number from 0 to 100; zero means no minimum.")]
    public void The_minimum_merit_is_a_score(int? min, string? expected) =>
        Assert.Equal(expected, OpportunityFit.MeritProblem(min));

    // ------------------------------------------------------- the judgement

    [Fact]
    public void An_opportunity_with_no_terms_fits_anyone()
    {
        var fit = OpportunityFit.Judge(0, [], null, 0, NoSkills, NoCategories);
        Assert.True(fit.CanEnter);
        Assert.True(fit.Recommended);
        Assert.Equal("fit", fit.Verdict);
        Assert.Empty(fit.Reasons);
    }

    [Fact]
    public void Short_of_the_merit_floor_is_blocked_and_told_both_numbers()
    {
        var fit = OpportunityFit.Judge(40, [], "web", 22, Keys("React"), new HashSet<string> { "web" });
        Assert.False(fit.CanEnter);
        Assert.Equal("blocked", fit.Verdict);
        Assert.Equal(["Needs a merit score of 40 — yours is 22."], fit.Reasons);
        Assert.Contains("needs a merit score of 40 and yours is 22", OpportunityFit.EntryProblem(fit));
    }

    [Fact]
    public void A_missing_required_skill_is_blocked_and_named_case_and_spacing_aside()
    {
        var fit = OpportunityFit.Judge(0, ["React Native", "PostgreSQL"], "mobile", 50,
            Keys("react   native"), new HashSet<string> { "mobile" });
        Assert.False(fit.CanEnter);
        Assert.Equal(["PostgreSQL"], fit.MissingSkills);
        Assert.Equal(["Your profile does not list PostgreSQL — the client requires it."], fit.Reasons);
    }

    [Fact]
    public void Two_missing_skills_read_as_a_sentence()
    {
        var fit = OpportunityFit.Judge(0, ["React", "Docker", "Redis"], null, 50, Keys("React"), NoCategories);
        Assert.Equal(["Your profile does not list Docker and Redis — the client requires them."], fit.Reasons);
        Assert.Contains("it requires Docker and Redis, which your profile does not list", OpportunityFit.EntryProblem(fit));
    }

    [Fact]
    public void Outside_their_category_may_still_enter_but_is_not_recommended()
    {
        var fit = OpportunityFit.Judge(0, [], "design", 50, Keys("React"), new HashSet<string> { "web" });
        Assert.True(fit.CanEnter);
        Assert.False(fit.Recommended);
        Assert.Equal("outside", fit.Verdict);
        Assert.Null(OpportunityFit.EntryProblem(fit));
        Assert.Single(fit.Reasons);
        Assert.Contains("Design & branding", fit.Reasons[0]);
    }

    [Fact]
    public void Covering_the_required_skills_outranks_the_category_word_list()
    {
        // A data opportunity that requires React: the React developer covers
        // it, and the category's own hints are not asked.
        var fit = OpportunityFit.Judge(0, ["React"], "data", 50, Keys("React"), new HashSet<string> { "web" });
        Assert.True(fit.Recommended);
    }

    [Fact]
    public void Something_else_is_in_nobodys_category_and_outside_nobodys()
    {
        var fit = OpportunityFit.Judge(0, [], "other", 10, NoSkills, NoCategories);
        Assert.True(fit.Recommended);
    }

    [Fact]
    public void Both_hard_reasons_are_given_together()
    {
        var fit = OpportunityFit.Judge(60, ["Docker"], "devops", 30, NoSkills, NoCategories);
        Assert.Equal(2, fit.Reasons.Count);
        var door = OpportunityFit.EntryProblem(fit)!;
        Assert.Contains("merit score of 60", door);
        Assert.Contains("requires Docker", door);
    }
}
