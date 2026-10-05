using WinnersPortal.Services.Auth;
using WinnersPortal.Services.Opportunities;
using WinnersPortal.Domain;
using WinnersPortal.Services.Profiles;
using Xunit;

namespace WinnersPortal.Api.Tests;

/// <summary>
/// The portfolio's rules and the merit score. The score is public arithmetic
/// on somebody's reputation, so the properties that make it fair — that the
/// self-reported half cannot dominate, that volume stops paying, that every
/// line is worth what the breakdown says — are pinned here rather than left
/// to a reading of the code.
/// </summary>
public class ProfileTests
{
    private static readonly Merit.Portfolio Empty = new(false, false, 0, 0, 0, false, false, true);
    private static readonly Merit.Record NoRecord = new(0, 0, 0, 0, 0, 0);

    // ------------------------------------------------ profile strength

    private static readonly Strength.Facts NothingYet = new(
        Confirmed: false, HasHeadline: false, HasBio: false, HasCategory: false,
        Skills: 0, Projects: 0, HasAvailability: false, Payments: 0);

    private static readonly Strength.Facts Everything = new(
        Confirmed: true, HasHeadline: true, HasBio: true, HasCategory: true,
        Skills: 1, Projects: 1, HasAvailability: true, Payments: 1);

    [Fact]
    public void Strength_lists_the_seven_steps_of_joining_in_the_rail_s_order()
    {
        Assert.Equal(
            new[] { "Account", "Confirm it's you", "Professional identity", "Expertise", "Portfolio", "Availability", "Payout Setup" },
            Strength.Steps(NothingYet).Select(s => s.Label));
    }

    [Fact]
    public void Having_an_account_is_the_one_step_done_by_arriving()
    {
        var steps = Strength.Steps(NothingYet);
        Assert.True(steps[0].Done);
        Assert.All(steps.Skip(1), s => Assert.False(s.Done));
        Assert.Equal(14, Strength.Percent(NothingYet));
    }

    [Fact]
    public void Six_of_seven_reads_eighty_six_and_all_seven_a_hundred()
    {
        Assert.Equal(100, Strength.Percent(Everything));
        Assert.Equal(86, Strength.Percent(Everything with { Payments = 0 }));
        Assert.Equal(86, Strength.Percent(Everything with { Confirmed = false }));
    }

    // ---------------------------------------- the welcome screen

    private static Fit Judged(bool canEnter, bool recommended, params string[] missing) =>
        new(canEnter, recommended, MeritOk: canEnter, SkillsOk: missing.Length == 0, CategoryOk: recommended,
            MeritScore: 50, MinMerit: 0, MissingSkills: missing, Reasons: [], Match: missing.Length == 0 ? 100 : 50,
            Factors: [], Risk: null);

    private static (string, Fit, int, decimal, DateTimeOffset?) Open(
        string slug, Fit fit, int requiredSkills, decimal award, int day = 1) =>
        (slug, fit, requiredSkills, award, new DateTimeOffset(2026, 9, day, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Rank_is_one_more_than_the_scores_above_and_equals_share_it()
    {
        int[] scores = [90, 80, 70, 70, 10];
        Assert.Equal(1, Welcome.Rank(90, scores));
        Assert.Equal(3, Welcome.Rank(70, scores));
        Assert.Equal(5, Welcome.Rank(10, scores));
        // Alone on the portal is first, not nowhere.
        Assert.Equal(1, Welcome.Rank(0, [0]));
    }

    [Fact]
    public void The_strongest_opportunity_is_recommended_first_then_the_most_skills_matched()
    {
        var rows = new[]
        {
            Open("blocked", Judged(false, false, "kubernetes"), 2, 9000m),
            Open("outside-rich", Judged(true, false), 1, 9000m),
            Open("fit-one", Judged(true, true), 1, 1000m),
            Open("fit-two", Judged(true, true), 2, 500m),
        };
        Assert.Equal("fit-two", Welcome.Strongest(rows));
        Assert.Equal("fit-one", Welcome.Strongest(rows.Where(r => r.Item1 != "fit-two")));
        // Enterable but not their kind of work still beats nothing; a shut door is not an opportunity.
        Assert.Equal("outside-rich", Welcome.Strongest(rows.Take(2)));
        Assert.Null(Welcome.Strongest(rows.Take(1)));
        Assert.Null(Welcome.Strongest(Array.Empty<(string, Fit, int, decimal, DateTimeOffset?)>()));
    }

    [Fact]
    public void A_tie_on_pull_falls_to_the_larger_award_then_the_newer_opportunity()
    {
        Assert.Equal("richer", Welcome.Strongest(new[]
        {
            Open("poorer", Judged(true, true), 1, 500m, day: 9),
            Open("richer", Judged(true, true), 1, 700m, day: 1),
        }));
        Assert.Equal("newer", Welcome.Strongest(new[]
        {
            Open("older", Judged(true, true), 1, 700m, day: 1),
            Open("newer", Judged(true, true), 1, 700m, day: 9),
        }));
    }

    [Fact]
    public void The_past_work_beside_an_opportunity_is_the_first_fitting_project_that_says_what_came_of_it()
    {
        static PastWork Work(string title, string? category, string? outcome, string? tech = null) =>
            new(title, outcome, ProjectFacts.Of(category, title, null, outcome, null, tech));
        var portfolio = new[]
        {
            Work("Brand refresh", "mobile", "Sign-ups doubled."),
            Work("Ledger", "web", null, "Django"),
            Work("Blank outcome", "web", "   "),
            Work("Invoicing", null, "Month-end close cut from three days to one.", "Django, Celery"),
            Work("Shop", "web", "Checkout stopped losing orders."),
        };

        // In the member's own order; a fitting project with no outcome, or a
        // blank one, is passed over rather than shown empty.
        Assert.Equal("Invoicing", Assessment.FittingWork(portfolio, ["Django"], "web")?.Title);
        // Filed under the kind of work counts without naming a skill.
        Assert.Equal("Shop", Assessment.FittingWork(portfolio, [], "web")?.Title);
        Assert.Null(Assessment.FittingWork(portfolio, ["Kotlin"], "data"));
        // A brief naming neither has nothing to match past work against.
        Assert.Null(Assessment.FittingWork(portfolio, [], null));
    }

    // ---------------------------------------- the review's arithmetic

    private static ProfileReview.Door Door(int minMerit, params string[] skills) =>
        new(minMerit, skills.Select(s => (s, OpportunityFit.Key(s))).ToList());

    private static IReadOnlySet<string> Mine(params string[] skills) =>
        skills.Select(OpportunityFit.Key).ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void A_missing_skill_is_worth_the_share_of_open_opportunities_it_opens()
    {
        // Four doors: two want React, one wants React and Docker together,
        // one is open already. Score 7 clears every floor.
        var written = Empty with { HasHeadline = true, HasBio = true };
        var doors = new[] { Door(0, "React"), Door(0, "react"), Door(0, "React", "Docker"), Door(0) };
        var lines = ProfileReview.Candidates(written, NoRecord, Mine("TypeScript"), doors);

        var react = Assert.Single(lines, c => c.Id == "skill:react");
        Assert.Equal(50, react.Gain); // 2 of 4
        Assert.Equal("Add React to your skills", react.Change);
        Assert.Equal("3 open opportunities require it", react.Because);
        // Docker alone opens nothing, so it is not a line; the pair is,
        // and it is worth everything the pair opens — the two React doors
        // as well as its own — because that is what adding both does.
        Assert.DoesNotContain(lines, c => c.Id == "skill:docker");
        var pair = Assert.Single(lines, c => c.Id == "skill:react+docker");
        Assert.Equal(75, pair.Gain);
        Assert.Equal("Add React and Docker to your skills", pair.Change);
        Assert.Equal("1 open opportunity requires both", pair.Because);
        // Best first.
        Assert.Equal(pair, lines[0]);
        Assert.Equal(react, lines[1]);
    }

    [Fact]
    public void A_merit_lever_is_a_line_only_where_its_points_reach_a_floor()
    {
        // Nothing written: score 0. An About is worth 3 and reaches the
        // floor of 3; a title is worth 2 and reaches nothing.
        var doors = new[] { Door(3), Door(10) };
        var lines = ProfileReview.Candidates(Empty, NoRecord, Mine(), doors);

        var about = Assert.Single(lines, c => c.Id == "about");
        Assert.Equal(50, about.Gain);
        Assert.Equal("worth 3 merit points, enough for 1 more open opportunity", about.Because);
        Assert.DoesNotContain(lines, c => c.Id == "title");
        // Six projects are worth 6 — the same one door.
        Assert.Equal("Add 6 portfolio projects", Assert.Single(lines, c => c.Id == "projects").Change);
        // GitHub is worth 2: a lever where a floor of 2 exists and the
        // portal offers the connection, and not a lever at all where it
        // does not — those points are not on offer to anybody there.
        Assert.DoesNotContain(lines, c => c.Id == "github");
        Assert.Contains(ProfileReview.Candidates(Empty, NoRecord, Mine(), [Door(2)]), c => c.Id == "github");
        Assert.DoesNotContain(
            ProfileReview.Candidates(Empty with { GithubOffered = false }, NoRecord, Mine(), [Door(2)]),
            c => c.Id == "github");
        // Links count for the first four projects: two of five linked (score
        // 7) leaves two to link, worth 2 — enough for a floor of 9.
        var linked = Empty with { Projects = 5, ProjectsWithLinks = 2 };
        Assert.Equal("Add a link or repository to 2 of your projects",
            Assert.Single(ProfileReview.Candidates(linked, NoRecord, Mine(), [Door(9)]), c => c.Id == "links").Change);
    }

    [Fact]
    public void The_figures_are_shares_of_the_open_opportunities_and_one_of_many_still_counts()
    {
        var doors = Enumerable.Range(0, 199).Select(_ => Door(0)).Append(Door(0, "Rust")).ToList();
        var rust = Assert.Single(ProfileReview.Candidates(Full, StrongRecord, Mine(), doors));
        Assert.Equal(1, rust.Gain); // half a point rounds up to one, not away to nothing
        Assert.Empty(ProfileReview.Candidates(Full, StrongRecord, Mine("Rust"), doors));
        Assert.Empty(ProfileReview.Candidates(Empty, NoRecord, Mine(), []));
    }

    [Fact]
    public void A_tick_is_the_least_a_section_is_for()
    {
        // One skill and one project tick their steps; twenty of each is the
        // merit score's business, not this list's.
        Assert.All(Strength.Steps(Everything), s => Assert.True(s.Done));
        // A title without an About is not Professional Info, and a kind of
        // work with no skill under it is not Expertise.
        Assert.False(Strength.Steps(Everything with { HasBio = false })[2].Done);
        Assert.False(Strength.Steps(Everything with { Skills = 0 })[3].Done);
        Assert.False(Strength.Steps(Everything with { HasCategory = false })[3].Done);
    }

    private static readonly Merit.Portfolio Full = new(
        HasHeadline: true, HasBio: true, Skills: 12, Projects: 8,
        ProjectsWithLinks: 8, HasDetails: true, GithubConnected: true, GithubOffered: true);

    private static readonly Merit.Record StrongRecord = new(
        Entries: 12, Wins: 4, MilestonesOnTime: 40, MilestonesDated: 40,
        RatingCount: 20, RatingSum: 100);

    [Fact]
    public void A_brand_new_account_scores_nothing_and_is_told_so()
    {
        Assert.Equal(0, Merit.Score(Empty, NoRecord));
        Assert.Equal("new here", Merit.Band(0));
        Assert.Equal(0, Merit.Completeness(Empty));
    }

    [Fact]
    public void Writing_about_yourself_cannot_carry_the_score()
    {
        // The whole design: an afternoon of typing gets you 25, and the
        // other 75 has to be earned on opportunities.
        Assert.Equal(Merit.PortfolioMax, Merit.PortfolioScore(Full));
        Assert.Equal(Merit.PortfolioMax, Merit.Score(Full, NoRecord));
        Assert.Equal(100, Merit.Completeness(Full));
        // …and a perfect portfolio alone still reads as somebody unproven.
        Assert.Equal("building", Merit.Band(Merit.Score(Full, NoRecord)));
    }

    [Fact]
    public void The_two_halves_add_up_to_exactly_a_hundred()
    {
        Assert.Equal(Merit.PortfolioMax + Merit.RecordMax, Merit.Max);
        Assert.Equal(100, Merit.Max);
        Assert.Equal(Merit.Max, Merit.Score(Full, StrongRecord));
        Assert.Equal("proven", Merit.Band(Merit.Max));

        // Every part's earned figure stays inside what it says is available,
        // which is what makes the breakdown readable as arithmetic.
        foreach (var part in Merit.PortfolioParts(Full)
                     .Concat(Merit.PortfolioParts(Full with { GithubOffered = false }))
                     .Concat(Merit.PortfolioParts(Empty with { GithubOffered = false }))
                     .Concat(Merit.RecordParts(StrongRecord)))
            Assert.InRange(part.Earned, 0, part.Available);
    }

    [Fact]
    public void Each_line_is_worth_what_the_owner_set()
    {
        Assert.Equal(25, Merit.PortfolioMax);
        Assert.Equal(75, Merit.RecordMax);
        Assert.Equal(
            new[]
            {
                ("A headline and an introduction", 5), ("Skills listed", 5), ("Past work", 6),
                ("Work anyone can look at", 4), ("Where and when you work", 3), ("GitHub connected", 2),
            },
            Merit.PortfolioParts(Full).Select(p => (p.Label, p.Available)));
        Assert.Equal(
            new[]
            {
                ("Opportunities entered", 20), ("Milestones met on time", 30),
                ("Opportunities won", 20), ("Ratings from clients", 5),
            },
            Merit.RecordParts(StrongRecord).Select(p => (p.Label, p.Available)));

        // One point a project for six, one more for each of the first four
        // with a link: a linked project is worth double one without.
        Assert.Equal(3, Merit.PortfolioScore(Empty with { Projects = 3 }));
        Assert.Equal(6, Merit.PortfolioScore(Empty with { Projects = 3, ProjectsWithLinks = 3 }));
        Assert.Equal(10, Merit.PortfolioScore(Empty with { Projects = 9, ProjectsWithLinks = 9 }));
        // Two for a title, three for an About.
        Assert.Equal(2, Merit.PortfolioScore(Empty with { HasHeadline = true }));
        Assert.Equal(3, Merit.PortfolioScore(Empty with { HasBio = true }));
    }

    [Fact]
    public void Wins_are_five_each_for_the_first_four()
    {
        Assert.Equal(5, Merit.RecordScore(new Merit.Record(0, 1, 0, 0, 0, 0)));
        Assert.Equal(15, Merit.RecordScore(new Merit.Record(0, 3, 0, 0, 0, 0)));
        Assert.Equal(20, Merit.RecordScore(new Merit.Record(0, 4, 0, 0, 0, 0)));
        Assert.Equal(20, Merit.RecordScore(new Merit.Record(0, 9, 0, 0, 0, 0)));
    }

    [Fact]
    public void A_portal_with_no_github_signin_stops_charging_for_it()
    {
        var here = Full with { GithubConnected = false };
        var elsewhere = here with { GithubOffered = false };

        Assert.Equal(Merit.PortfolioMax, Merit.PortfolioAvailable(here));
        Assert.Equal(Merit.PortfolioMax - 2, Merit.PortfolioAvailable(elsewhere));
        Assert.Equal(Merit.Max - 2, Merit.MaxFor(elsewhere));

        // The reason this matters at all: a portal with no GitHub App used to
        // cap everybody's "finish your profile" bar at 90% with nothing on
        // the page they could do about it.
        Assert.True(Merit.Completeness(here) < 100);
        Assert.Equal(100, Merit.Completeness(elsewhere));
    }

    [Fact]
    public void The_github_line_says_why_rather_than_reading_nought_of_two()
    {
        var line = Merit.PortfolioParts(Empty with { GithubOffered = false })
            .Single(p => p.Label == "GitHub connected");
        Assert.Equal(0, line.Available);
        Assert.Contains("no GitHub sign-in", line.Detail);
    }

    [Fact]
    public void Taking_the_github_app_away_does_not_move_a_score_under_anybody()
    {
        // Connected before an admin emptied the GitHub settings: the
        // connection is still a fact about the account, so the two points
        // stay earned and stay in the denominator for that person.
        var kept = Full with { GithubOffered = false };
        Assert.Equal(Merit.PortfolioMax, Merit.PortfolioScore(kept));
        Assert.Equal(Merit.PortfolioMax, Merit.PortfolioAvailable(kept));
        Assert.Equal(Merit.Score(Full, StrongRecord), Merit.Score(kept, StrongRecord));
    }

    [Fact]
    public void Entering_everything_is_not_a_strategy()
    {
        var few = new Merit.Record(2, 0, 0, 0, 0, 0);
        var many = new Merit.Record(50, 0, 0, 0, 0, 0);

        // Two each, and volume stops paying at ten: fifty entries are worth
        // no more than ten.
        Assert.Equal(4, Merit.RecordScore(few));
        Assert.Equal(20, Merit.RecordScore(new Merit.Record(10, 0, 0, 0, 0, 0)));
        Assert.Equal(Merit.RecordScore(new Merit.Record(10, 0, 0, 0, 0, 0)), Merit.RecordScore(many));
    }

    [Fact]
    public void Punctuality_is_a_ratio_not_a_count()
    {
        // Ten of ten beats forty of eighty, which is the point: the dates are
        // about keeping to them, not about how many were on offer.
        var punctual = new Merit.Record(3, 0, 10, 10, 0, 0);
        var busy = new Merit.Record(3, 0, 40, 80, 0, 0);

        Assert.True(Merit.RecordScore(punctual) > Merit.RecordScore(busy));
        // Thirty times the share met on time: eight of ten is 24, one of
        // eight is 3.75, and a half rounds up (five of twelve is 12.5 → 13).
        Assert.Equal(24, Merit.RecordScore(new Merit.Record(0, 0, 8, 10, 0, 0)));
        Assert.Equal(4, Merit.RecordScore(new Merit.Record(0, 0, 1, 8, 0, 0)));
        Assert.Equal(13, Merit.RecordScore(new Merit.Record(0, 0, 5, 12, 0, 0)));
        // An opportunity whose milestones carried no dates cannot cost anybody
        // points — they never had the chance to be on time.
        Assert.Equal(
            Merit.RecordScore(new Merit.Record(3, 0, 0, 0, 0, 0)),
            Merit.RecordScore(new Merit.Record(3, 0, 0, 0, 0, 0)));
    }

    [Fact]
    public void Ratings_are_worth_their_star_average()
    {
        // The average itself, to the nearest whole point: one five-star
        // rating is worth what twenty are.
        Assert.Equal(5, Merit.RecordScore(new Merit.Record(0, 0, 0, 0, 1, 5)));
        Assert.Equal(5, Merit.RecordScore(new Merit.Record(0, 0, 0, 0, 20, 100)));
        Assert.Equal(1, Merit.RecordScore(new Merit.Record(0, 0, 0, 0, 5, 5)));
        Assert.Equal(4, Merit.RecordScore(new Merit.Record(0, 0, 0, 0, 5, 22)));
        Assert.Equal(5, Merit.RecordScore(new Merit.Record(0, 0, 0, 0, 2, 9)));

        var line = Merit.RecordParts(new Merit.Record(0, 0, 0, 0, 5, 22)).Single(p => p.Label == "Ratings from clients");
        Assert.Equal("4.4 stars on average from 5 ratings, to the nearest whole point.", line.Detail);
    }

    [Theory]
    [InlineData(0, "new here")]
    [InlineData(24, "new here")]
    [InlineData(25, "building")]
    [InlineData(49, "building")]
    [InlineData(50, "established")]
    [InlineData(74, "established")]
    [InlineData(75, "proven")]
    [InlineData(100, "proven")]
    public void Every_score_has_a_word_for_it(int score, string band) =>
        Assert.Equal(band, Merit.Band(score));

    // ------------------------------------------------------------- rules

    [Fact]
    public void Blank_and_missing_are_the_same_answer()
    {
        Assert.Null(ProfileRules.Clean(null, 100));
        Assert.Null(ProfileRules.Clean("", 100));
        Assert.Null(ProfileRules.Clean("   ", 100));
        Assert.Equal("Django", ProfileRules.Clean("  Django  ", 100));
        Assert.Equal(5, ProfileRules.Clean("abcdefghij", 5)!.Length);
    }

    [Fact]
    public void Only_a_link_the_portal_would_render_survives()
    {
        // The reason this function exists: these end up as anchors on
        // somebody else's screen.
        Assert.Null(ProfileRules.CleanUrl("javascript:alert(1)"));
        Assert.Null(ProfileRules.CleanUrl("data:text/html;base64,PGgxPmhpPC9oMT4="));
        Assert.Null(ProfileRules.CleanUrl("file:///etc/passwd"));

        Assert.Equal("https://example.test/work", ProfileRules.CleanUrl("https://example.test/work"));
        Assert.Equal("http://example.test/", ProfileRules.CleanUrl("http://example.test"));
        // A bare domain is what people actually type; assume the safe scheme.
        Assert.Equal("https://example.test/", ProfileRules.CleanUrl("example.test"));
        Assert.Null(ProfileRules.CleanUrl(null));
    }

    [Fact]
    public void Numbers_that_are_not_answers_become_no_answer()
    {
        Assert.Null(ProfileRules.CleanCount(null, 60));
        Assert.Null(ProfileRules.CleanCount(-1, 60));
        Assert.Equal(60, ProfileRules.CleanCount(900, 60));
        Assert.Equal(12, ProfileRules.CleanCount(12, 60));

        Assert.Null(ProfileRules.CleanYear(1200, 2026));
        Assert.Null(ProfileRules.CleanYear(2099, 2026));
        // Next year is allowed: work finishing in December, filed in November.
        Assert.Equal(2027, ProfileRules.CleanYear(2027, 2026));
        Assert.Equal(2019, ProfileRules.CleanYear(2019, 2026));
    }

    [Fact]
    public void A_submission_is_refused_before_a_row_is_written()
    {
        Assert.Null(ProfileRules.Problem(
            ["C#", "SQL", "React"], ["English", "Urdu"], ["A thing", "Another"]));
        Assert.Contains("At most", ProfileRules.Problem(
            [.. Enumerable.Range(0, ProfileRules.MaxSkills + 1).Select(i => $"skill-{i}")], [], [])!);
        Assert.Contains("At most", ProfileRules.Problem(
            [], [.. Enumerable.Range(0, ProfileRules.MaxLanguages + 1).Select(i => $"lang-{i}")], [])!);
        Assert.Contains("At most", ProfileRules.Problem(
            [], [], [.. Enumerable.Range(0, ProfileRules.MaxProjects + 1).Select(i => $"project-{i}")])!);
        Assert.Contains("needs a name", ProfileRules.Problem(["C#", "  "], [], [])!);
        Assert.Contains("needs a name", ProfileRules.Problem([], ["English", null], [])!);
        Assert.Contains("needs a title", ProfileRules.Problem([], [], ["A thing", null])!);
    }

    // ------------------------------------------------------- duplicates

    [Fact]
    public void The_same_thing_said_twice_is_a_mistake_not_two_claims()
    {
        // The whole point of the check: two rows for one skill are two
        // levels and two year counts for the same thing, and a client
        // reading them has no way to know which to believe.
        Assert.Contains("twice", ProfileRules.Problem(["React", "SQL", "react"], [], [])!);
        Assert.Contains("twice", ProfileRules.Problem([], ["English", "english"], [])!);
        Assert.Contains("twice", ProfileRules.Problem([], [], ["A thing", "A thing"])!);

        // The message names the repeat as it was typed the second time —
        // that is the row the member has to go and find.
        Assert.StartsWith("\u201Creact\u201D", ProfileRules.Problem(["React", "react"], [], [])!);
    }

    [Fact]
    public void A_repeat_is_judged_on_the_word_not_the_typing()
    {
        Assert.Equal("react native", ProfileRules.FirstDuplicate(["React Native", "react native"]));
        Assert.Equal("React  Native", ProfileRules.FirstDuplicate(["React Native", "React  Native"]));
        Assert.Equal("PostgreSQL", ProfileRules.FirstDuplicate(["  postgresql ", "PostgreSQL"]));

        // Two different things stay two different things.
        Assert.Null(ProfileRules.FirstDuplicate(["React", "React Native", "Preact"]));
        // Blanks are refused by their own rule, and must not read as repeats
        // of each other before that rule ever runs.
        Assert.Null(ProfileRules.FirstDuplicate([null, "", "   ", "C#"]));
    }

    [Fact]
    public void One_text_box_holding_a_list_drops_the_repeat_instead_of_refusing()
    {
        // A project's "built with" is a single field. There is no row to
        // point at, so the second mention is dropped rather than argued
        // about — and the first spelling is the one that survives.
        Assert.Equal("Django, Postgres", ProfileRules.DedupeList("Django, Postgres, django", 200));
        Assert.Equal("Django, Postgres", ProfileRules.DedupeList(" Django ,, Postgres , ", 200));
        Assert.Null(ProfileRules.DedupeList("  ,  , ", 200));
        Assert.Null(ProfileRules.DedupeList(null, 200));
    }

    // -------------------------------------------------------- languages

    [Fact]
    public void A_language_level_survives_the_trip_out_and_back()
    {
        foreach (var level in Enum.GetValues<LanguageLevel>())
            Assert.Equal(level, ProfileRules.ParseLanguageLevel(ProfileRules.LanguageLevelName(level)));
    }

    [Theory]
    [InlineData(null, LanguageLevel.Professional)]
    [InlineData("", LanguageLevel.Professional)]
    [InlineData("wizard", LanguageLevel.Professional)]
    [InlineData("  NATIVE ", LanguageLevel.Native)]
    [InlineData("Fluent", LanguageLevel.Fluent)]
    public void An_unrecognised_language_level_lands_on_what_the_old_field_meant(
        string? sent, LanguageLevel expected)
    {
        // "Languages you work in" was the label on the column these rows
        // were migrated out of, so Professional is not an arbitrary default:
        // it is the claim the old field was already making.
        Assert.Equal(expected, ProfileRules.ParseLanguageLevel(sent));
    }

    [Fact]
    public void The_five_language_words_are_the_ones_the_form_knows()
    {
        Assert.Equal("beginner", ProfileRules.LanguageLevelName(LanguageLevel.Beginner));
        Assert.Equal("conversational", ProfileRules.LanguageLevelName(LanguageLevel.Conversational));
        Assert.Equal("professional", ProfileRules.LanguageLevelName(LanguageLevel.Professional));
        Assert.Equal("fluent", ProfileRules.LanguageLevelName(LanguageLevel.Fluent));
        Assert.Equal("native", ProfileRules.LanguageLevelName(LanguageLevel.Native));
    }

    [Fact]
    public void A_skill_level_survives_the_trip_out_and_back()
    {
        // The bug this pins: reads handed out the word and saves demanded
        // the number, so the form could not send back what it was given and
        // every save carrying a skill was refused with an empty 400. The two
        // directions have to be inverses, for every value of the enum.
        foreach (var level in Enum.GetValues<SkillLevel>())
        {
            Assert.Equal(level, ProfileRules.ParseLevel(ProfileRules.LevelName(level)));
        }
    }

    [Fact]
    public void The_words_on_the_wire_are_the_four_the_form_knows()
    {
        // Lower case, and exactly these: the web types the field as a union
        // of the four, and a fifth spelling would fail there silently by
        // leaving the dropdown blank rather than by throwing.
        Assert.Equal("beginner", ProfileRules.LevelName(SkillLevel.Beginner));
        Assert.Equal("intermediate", ProfileRules.LevelName(SkillLevel.Intermediate));
        Assert.Equal("advanced", ProfileRules.LevelName(SkillLevel.Advanced));
        Assert.Equal("expert", ProfileRules.LevelName(SkillLevel.Expert));
    }

    [Fact]
    public void The_scale_grew_a_rung_without_moving_anybody_up_it()
    {
        // The stored column is the number, and the numbers did not change
        // when "learning, working, strong" became four rungs: a migration
        // that wrote to this column would have promoted every member who
        // had said "strong" to expert overnight, which is a claim they
        // never made. Expert is the new number.
        Assert.Equal(0, (int)SkillLevel.Beginner);
        Assert.Equal(1, (int)SkillLevel.Intermediate);
        Assert.Equal(2, (int)SkillLevel.Advanced);
        Assert.Equal(3, (int)SkillLevel.Expert);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("wizard")]
    // The words the three-rung scale used. They are not translated, because
    // a client that still sends them is a client that has not been reloaded,
    // and quietly reading "strong" as expert would file a claim nobody made.
    [InlineData("strong")]
    [InlineData("working")]
    [InlineData("Advanced")]
    [InlineData("  expert  ")]
    public void An_unrecognised_level_is_a_default_and_never_a_refusal(string? sent)
    {
        // A save is a whole portfolio. Refusing all of it over one unknown
        // word in one row would lose the rest, so the row lands on a level
        // instead — the same one a new row starts on, except where the word
        // is recognisable after trimming and lowering.
        var expected = sent?.Trim().ToLowerInvariant() switch
        {
            "advanced" => SkillLevel.Advanced,
            "expert" => SkillLevel.Expert,
            _ => SkillLevel.Intermediate,
        };
        Assert.Equal(expected, ProfileRules.ParseLevel(sent));
    }

    // ------------------------------------------------------ how they work

    [Fact]
    public void A_work_type_survives_the_trip_out_and_back()
    {
        foreach (var type in Enum.GetValues<WorkType>())
        {
            Assert.Equal(type, ProfileRules.ParseWorkType(ProfileRules.WorkTypeName(type)));
        }
        Assert.Equal("fulltime", ProfileRules.WorkTypeName(WorkType.FullTimeFreelancer));
        Assert.Equal("agency", ProfileRules.WorkTypeName(WorkType.AgencyMember));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("contractor")]
    public void Not_saying_how_you_work_is_an_answer_and_not_a_default(string? sent)
    {
        // Unlike a skill level, there is no rung to land on: the field is
        // optional and an unreadable word has to come back as nothing said,
        // rather than filing somebody as a full-time freelancer.
        Assert.Null(ProfileRules.ParseWorkType(sent));
        Assert.Null(ProfileRules.WorkTypeName(null));
        Assert.Null(ProfileRules.WorkTypeLabel(null));
    }

    // ------------------------------------------------- when they can work

    [Fact]
    public void An_availability_survives_the_trip_out_and_back()
    {
        foreach (var status in Enum.GetValues<Availability>())
        {
            Assert.Equal(status, ProfileRules.ParseAvailability(ProfileRules.AvailabilityName(status)));
            // Every status has words: the summary drafter and anything
            // else reading a profile as prose must never see "twoweeks".
            Assert.False(string.IsNullOrWhiteSpace(ProfileRules.AvailabilityLabel(status)));
        }
        Assert.Equal("now", ProfileRules.AvailabilityName(Availability.Now));
        Assert.Equal("unavailable", ProfileRules.AvailabilityName(Availability.Unavailable));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("soon")]
    [InlineData("0")]
    public void Not_saying_whether_you_can_start_is_not_saying_you_can(string? sent)
    {
        // The one status a client would act on is "now", and nobody lands
        // on it by leaving the question alone or sending a word the portal
        // does not know. Null out, null back.
        Assert.Null(ProfileRules.ParseAvailability(sent));
        Assert.Null(ProfileRules.AvailabilityName(null));
        Assert.Null(ProfileRules.AvailabilityLabel(null));
    }

    [Fact]
    public void A_preference_is_a_key_from_its_list_said_once_in_the_lists_order()
    {
        // Sent out of order, in the wrong case, twice, and with a word the
        // list does not know; stored as the list spells and orders it, and
        // the unknown one is dropped rather than refusing the save.
        var durations = ProfileRules.CleanPreferences(
            ["LONG", " short ", "long", "forever"], ProjectDurations.All);
        Assert.Equal(["short", "long"], durations);

        var types = ProfileRules.CleanPreferences(["hourly", "direct"], EngagementTypes.All);
        Assert.Equal(["direct", "hourly"], types);
    }

    [Fact]
    public void No_preference_is_an_empty_list_and_never_a_default()
    {
        Assert.Empty(ProfileRules.CleanPreferences(null, ProjectDurations.All));
        Assert.Empty(ProfileRules.CleanPreferences([], EngagementTypes.All));
        Assert.Empty(ProfileRules.CleanPreferences([null, "", "  "], ProjectDurations.All));
    }

    [Fact]
    public void The_two_preference_lists_are_closed_and_every_key_is_findable()
    {
        foreach (var p in ProjectDurations.All)
        {
            Assert.Same(p, ProjectDurations.Find(p.Key));
            Assert.Same(p, ProjectDurations.Find(" " + p.Key.ToUpperInvariant()));
        }
        foreach (var p in EngagementTypes.All) Assert.Same(p, EngagementTypes.Find(p.Key));
        Assert.Null(ProjectDurations.Find("eternal"));
        Assert.Null(EngagementTypes.Find(null));
        // A key is never a key on the other list: the form shows two sets
        // of chips and a save must not be able to cross them.
        Assert.Empty(ProjectDurations.All.Select(p => p.Key)
            .Intersect(EngagementTypes.All.Select(p => p.Key)));
    }

    [Fact]
    public void A_working_window_is_kept_as_written_and_no_longer_than_a_line()
    {
        Assert.Equal("09:00 – 18:00", ProfileRules.Clean("  09:00 – 18:00 ", ProfileRules.MaxWorkingWindow));
        Assert.Null(ProfileRules.Clean("   ", ProfileRules.MaxWorkingWindow));
        var essay = new string('x', 200);
        Assert.Equal(ProfileRules.MaxWorkingWindow,
            ProfileRules.Clean(essay, ProfileRules.MaxWorkingWindow)!.Length);
    }

    // --------------------------------------------------- kinds of work done

    [Fact]
    public void The_kinds_of_work_are_keys_from_the_one_taxonomy()
    {
        var (primary, secondary) = ProfileRules.CleanCategories(
            " Mobile ", ["web", "design"]);

        // Cleaned the way a category key is anywhere else: trimmed, lowered,
        // and recognised — or not a category.
        Assert.Equal("mobile", primary);
        Assert.Equal(["web", "design"], secondary);
    }

    [Fact]
    public void A_category_the_taxonomy_lost_is_dropped_rather_than_refused()
    {
        // A retired category must not be able to make somebody's whole
        // profile unsaveable — the rest of the form is still theirs.
        var (primary, secondary) = ProfileRules.CleanCategories(
            "telepathy", ["web", "alchemy", "design"]);
        Assert.Null(primary);
        Assert.Equal(["web", "design"], secondary);
    }

    [Fact]
    public void The_primary_kind_of_work_is_not_also_one_of_the_others()
    {
        // Said twice it reads as two claims; it is one. The same rule as a
        // skill listed twice, applied where the form could send it by
        // accident rather than by typing.
        var (primary, secondary) = ProfileRules.CleanCategories(
            "web", ["web", "design", "design", "qa"]);
        Assert.Equal("web", primary);
        Assert.Equal(["design", "qa"], secondary);
    }

    [Fact]
    public void At_most_three_other_kinds_of_work_are_kept()
    {
        var (_, secondary) = ProfileRules.CleanCategories(
            "web", ["design", "qa", "data", "games", "mobile"]);
        Assert.Equal(ProfileRules.MaxSecondaryCategories, secondary.Count);
        Assert.Equal(["design", "qa", "data"], secondary);
    }

    [Fact]
    public void Every_kind_of_work_offers_names_a_member_could_pick_from()
    {
        // The chips under the skills list are this, filtered in the browser
        // against what is already typed. "Something else" is the one kind
        // with nothing to offer, for the same reason it cannot be judged.
        foreach (var category in OpportunityCategories.All)
        {
            if (OpportunityCategories.Judgeable(category))
                Assert.NotEmpty(category.Skills);
            else
                Assert.Empty(category.Skills);
            Assert.All(category.Skills, s => Assert.False(string.IsNullOrWhiteSpace(s)));
            // One spelling each: the whole point of offering the list.
            Assert.Null(ProfileRules.FirstDuplicate(category.Skills));
            // The opportunity form offers a subcategory's own names first.
            foreach (var sub in category.Subcategories)
            {
                Assert.NotEmpty(sub.Skills);
                Assert.All(sub.Skills, s => Assert.False(string.IsNullOrWhiteSpace(s)));
                Assert.Null(ProfileRules.FirstDuplicate(sub.Skills));
            }
        }
        Assert.Contains("Kotlin", OpportunityCategories.Find("mobile")!.Skills);
        Assert.Contains("SwiftUI", OpportunityCategories.FindSub(OpportunityCategories.Find("mobile")!, "ios")!.Skills);
    }

    // ------------------------------------------------------- past work

    [Fact]
    public void A_project_is_filed_under_the_same_kinds_of_work_an_opportunity_is()
    {
        // One taxonomy for both halves of the market, and one cleaner: the
        // key a project carries is the key a brief carries.
        Assert.Equal("mobile", ProfileRules.CleanCategory("  MOBILE  "));
        Assert.Null(ProfileRules.CleanCategory("archaeology"));
        Assert.Null(ProfileRules.CleanCategory(null));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(12)]
    public void A_completion_date_keeps_the_month_it_was_given(int month) =>
        Assert.Equal(month, ProfileRules.CleanMonth(month, 2024));

    [Theory]
    [InlineData(0)]
    [InlineData(13)]
    [InlineData(-3)]
    [InlineData(null)]
    public void A_month_that_is_not_a_month_is_not_stored(int? month) =>
        Assert.Null(ProfileRules.CleanMonth(month, 2024));

    [Fact]
    public void A_month_without_a_year_is_half_a_date_and_is_dropped()
    {
        // The form cannot produce one — the picker hands over both or
        // neither — so a request that sends one is saying nothing.
        Assert.Null(ProfileRules.CleanMonth(7, null));
        // And a year the cleaner already refused takes its month with it.
        Assert.Null(ProfileRules.CleanMonth(7, ProfileRules.CleanYear(1492, 2026)));
    }

    [Fact]
    public void The_year_that_was_already_stored_is_not_rewritten_to_add_a_month()
    {
        // Past work written down before the month was asked for keeps its
        // year and gains nothing: no January was invented for anybody.
        var year = ProfileRules.CleanYear(2019, 2026);
        Assert.Equal(2019, year);
        Assert.Null(ProfileRules.CleanMonth(null, year));
    }

    [Fact]
    public void A_screenshot_is_read_for_what_its_bytes_are()
    {
        // The same three signatures the profile picture is judged on, and
        // the same refusal to believe a declared type.
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3 };
        var (bytes, contentType, problem) = ImageRules.Read(
            "data:image/gif;base64," + Convert.ToBase64String(png),
            ProjectImageRules.MaxBytes, ProjectImageRules.TooBig, "nothing there");

        Assert.Null(problem);
        Assert.Equal("image/png", contentType);
        Assert.Equal(png, bytes);
    }

    [Fact]
    public void A_picture_over_the_ceiling_is_refused_in_words_a_member_can_act_on()
    {
        var big = new byte[ProjectImageRules.MaxBytes + 1];
        big[0] = 0xFF; big[1] = 0xD8; big[2] = 0xFF;
        var (bytes, _, problem) = ImageRules.Read(
            Convert.ToBase64String(big),
            ProjectImageRules.MaxBytes, ProjectImageRules.TooBig, "nothing there");

        Assert.Null(bytes);
        Assert.Equal(ProjectImageRules.TooBig, problem);
    }

    [Fact]
    public void A_file_that_is_not_a_picture_is_named_as_one_that_is_not()
    {
        var (_, _, problem) = ImageRules.Read(
            Convert.ToBase64String("a plain text file"u8.ToArray()),
            ProjectImageRules.MaxBytes, ProjectImageRules.TooBig, "nothing there");

        Assert.Contains("JPEG, PNG or WebP", problem);
    }

    [Fact]
    public void A_screenshot_may_be_larger_than_a_face_and_still_bounded()
    {
        // A picture of somebody is glanced at; a screenshot is read, so the
        // ceiling is higher — and a save that carries several of them has a
        // ceiling of its own, so nothing dies in the server unexplained.
        Assert.True(ProjectImageRules.MaxBytes > AvatarRules.MaxBytes);
        Assert.True(ProjectImageRules.MaxNewBytesPerSave
            > ProjectImageRules.MaxBytes * ProjectImageRules.MaxPerProject);
        Assert.Contains(ProjectImageRules.MaxPerProject.ToString(), ProjectImageRules.TooMany("Ledger"));
        Assert.Contains("Ledger", ProjectImageRules.TooMany("  Ledger  "));
    }

    [Fact]
    public void A_pictures_address_is_its_id_and_nothing_else()
    {
        // No version in the query, because a replaced picture is a new row
        // with a new id — which is what lets a browser keep one for a year.
        var id = Guid.NewGuid();
        Assert.Equal($"/api/project-images/{id}", ProjectImageRules.Url(id));
        Assert.DoesNotContain("?", ProjectImageRules.Url(id));
    }

    // ------------------------------------------------------- suggestions

    [Fact]
    public void Suggestions_fold_case_and_offer_the_commonest_spelling()
    {
        var list = ProfileRules.Suggest(
            ["PostgreSQL", "postgresql", "PostgreSQL", "Django", "  django "], split: false);

        // One entry per skill however it was cased, spelt the way most
        // people spelt it, and the most-used one first.
        Assert.Equal(["PostgreSQL", "Django"], list);
    }

    [Fact]
    public void Suggestions_split_the_comma_fields_and_skip_the_blanks()
    {
        var list = ProfileRules.Suggest(
            ["English, Urdu", "english,, Punjabi", null, "  ", "Urdu"], split: true);

        Assert.Equal(["English", "Urdu", "Punjabi"], list);
    }

    [Fact]
    public void Suggestions_stop_at_the_cap()
    {
        var many = Enumerable.Range(0, 500).Select(i => $"skill-{i}");
        Assert.Equal(ProfileRules.MaxSuggestions, ProfileRules.Suggest(many, split: false).Count);
        Assert.Equal(5, ProfileRules.Suggest(many, split: false, max: 5).Count);
    }
}
