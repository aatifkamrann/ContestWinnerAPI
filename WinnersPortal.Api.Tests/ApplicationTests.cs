using WinnersPortal.Services.Auth;
using WinnersPortal.Services.Profiles;
using WinnersPortal.Services.Opportunities;
using WinnersPortal.Domain;
using Xunit;

namespace WinnersPortal.Api.Tests;

/// <summary>
/// The rules of applying to compete — all pure, so every figure the wizard
/// shows and every refusal the door gives is pinned here.
/// </summary>
public class ApplicationTests
{
    private static Fit SomeFit(
        int match = 70, bool canEnter = true, bool categoryOk = true,
        int? skills = 90, int? projects = 60, int? record = null, int? availability = 80) =>
        new(
            CanEnter: canEnter, Recommended: canEnter && categoryOk, MeritOk: canEnter, SkillsOk: canEnter,
            CategoryOk: categoryOk, MeritScore: 55, MinMerit: 0, MissingSkills: [], Reasons: [],
            Match: match,
            Factors:
            [
                new("skills", "Expertise in the required skills", skills, ""),
                new("projects", "Relevant project history", projects, ""),
                new("record", "On-time delivery record", record, ""),
                new("availability", "Availability in timeline", availability, ""),
            ],
            Risk: null);

    // ------------------------------------------------------ the approach

    [Fact]
    public void The_approach_is_between_a_hundred_and_a_thousand_characters()
    {
        Assert.Null(ApplicationRules.CleanApproach(new string('a', 99), out var tooShort));
        Assert.Contains("at least 100", tooShort);
        Assert.Equal(new string('a', 100), ApplicationRules.CleanApproach("  " + new string('a', 100) + "\n", out var ok));
        Assert.Null(ok);
        Assert.Null(ApplicationRules.CleanApproach(new string('a', 1001), out var tooLong));
        Assert.Contains("under 1000", tooLong);
    }

    [Fact]
    public void Advantages_are_the_closed_list_in_its_own_order_each_once()
    {
        var kept = ApplicationRules.CleanAdvantages(
            ["cost-efficiency", "made-up", "technical-depth", " cost-efficiency ", null]);
        Assert.Equal(["technical-depth", "cost-efficiency"], kept);
        Assert.Empty(ApplicationRules.CleanAdvantages(null));
    }

    // ----------------------------------------------------- the relevance

    [Fact]
    public void A_project_s_relevance_is_the_kind_of_work_and_the_skills_it_mentions()
    {
        static ProjectFacts Facts(string? category, string text) => ProjectFacts.Of(category, text, null, null, null, null);

        // Filed under the brief's kind of work is 60; each required skill mentioned shares the other 40.
        Assert.Equal(100, ApplicationRules.Relevance(Facts("web", "Django shop with Celery"), ["Django", "Celery"], "web"));
        Assert.Equal(80, ApplicationRules.Relevance(Facts("web", "Django shop"), ["Django", "Celery"], "web"));
        Assert.Equal(60, ApplicationRules.Relevance(Facts("web", "A brochure site"), ["Django", "Celery"], "web"));
        Assert.Equal(20, ApplicationRules.Relevance(Facts("mobile", "Django app"), ["Django", "Celery"], "web"));
        // No skills named: the kind of work is the whole answer.
        Assert.Equal(100, ApplicationRules.Relevance(Facts("web", "anything"), [], "web"));
        Assert.Equal(0, ApplicationRules.Relevance(Facts("data", "anything"), [], "web"));
        // No kind of work: the skills are.
        Assert.Equal(50, ApplicationRules.Relevance(Facts(null, "Django only"), ["Django", "Celery"], null));
        // Neither: nothing to measure against.
        Assert.Null(ApplicationRules.Relevance(Facts("web", "anything"), [], null));
    }

    [Fact]
    public void Suggested_advantages_follow_what_the_assessment_can_see()
    {
        Assert.Equal(
            ["technical-depth", "delivery-record", "similar-projects", "domain-expertise"],
            ApplicationRules.SuggestedAdvantages(SomeFit(skills: 90, record: 95), relevantProjects: 2));
        Assert.Empty(ApplicationRules.SuggestedAdvantages(
            SomeFit(skills: 70, record: null, categoryOk: false), relevantProjects: 0));
    }

    // ---------------------------------------------------- the comparison

    [Fact]
    public void Stronger_than_counts_the_others_below_and_says_nothing_alone()
    {
        Assert.Null(ApplicationRules.StrongerThan(80, []));
        Assert.Equal(67, ApplicationRules.StrongerThan(80, [50, 80, 70]));
        Assert.Equal(0, ApplicationRules.StrongerThan(40, [50, 80]));
        Assert.Equal(100, ApplicationRules.StrongerThan(90, [50, 80]));
    }

    // ---------------------------------------------------- the evaluation

    [Fact]
    public void The_evaluation_reads_strengths_and_risks_off_the_lines_and_the_portfolio()
    {
        var e = ApplicationRules.Evaluate(SomeFit(match: 87, skills: 90, projects: 40, record: null, availability: 85), portfolioCount: 2);
        Assert.Equal(87, e.Percent);
        Assert.Equal("Strong Candidate", e.Band);
        Assert.Equal(["skills", "portfolio", "availability"], e.Strengths.Select(s => s.Id));
        Assert.Equal("Technical fit", e.Strengths[0].Label);
        Assert.Equal(["projects"], e.Risks.Select(r => r.Id));
        Assert.Equal("Limited domain experience", e.Risks[0].Label);

        // A line the portal cannot read is neither; nothing attached is a risk.
        var thin = ApplicationRules.Evaluate(SomeFit(match: 55, skills: 70, projects: null, record: null, availability: null), 0);
        Assert.Equal("Developing Candidate", thin.Band);
        Assert.Empty(thin.Strengths);
        Assert.Equal(["portfolio"], thin.Risks.Select(r => r.Id));
        Assert.Equal("Outside their usual work", ApplicationRules.Evaluate(SomeFit(categoryOk: false), 1).Band);
    }

    [Fact]
    public void Advice_after_a_no_reads_the_evaluation_and_nothing_else()
    {
        var e = ApplicationRules.Evaluate(
            SomeFit(match: 94, skills: 90, projects: 40, record: null, availability: 85), portfolioCount: 2);
        var advice = ApplicationRules.AdviceFor(e, "Mobile apps");
        Assert.Equal(["Mobile apps experience"], advice.Strong);
        Assert.Equal(["Add more relevant portfolio examples", "Increase competition experience"], advice.Improve);

        // One line per weakness, cheapest first; a strong record needs none.
        var weak = ApplicationRules.Evaluate(SomeFit(skills: 50, projects: 90, record: 40, availability: 50), 1);
        Assert.Equal(
            [
                "List the required skills you have on your profile",
                "Deliver your next milestones on time",
                "Keep more of your week free for the opportunity timeline",
            ],
            ApplicationRules.AdviceFor(weak, "Web development").Improve);
        var clean = ApplicationRules.Evaluate(SomeFit(skills: 90, projects: 90, record: 95, availability: 90), 1);
        Assert.Empty(ApplicationRules.AdviceFor(clean, "Web development").Improve);

        // Outside their usual work there is no kind of work to call strong:
        // the named strengths stand in, and the kind of work is what to fix.
        var outside = ApplicationRules.Evaluate(
            SomeFit(categoryOk: false, skills: 90, projects: 90, record: 95, availability: 90), 1);
        var o = ApplicationRules.AdviceFor(outside, "Web development");
        Assert.Equal(
            ["Technical fit", "Relevant portfolio", "Domain experience", "Delivery history", "Available for the timeline"],
            o.Strong);
        Assert.Equal(["Apply where the kind of work matches what your profile shows"], o.Improve);

        // A brief with no kind of work named: the strengths again.
        Assert.Equal(
            ["Technical fit", "Relevant portfolio", "Available for the timeline"],
            ApplicationRules.AdviceFor(e, null).Strong);
    }

    // ------------------------------------------------------------ the door

    [Fact]
    public void The_door_refuses_in_the_old_order_then_the_two_new_answers()
    {
        var fit = SomeFit();
        Assert.Null(ApplicationRules.SubmitProblem(OpportunityStatus.Open, true, fit, false, false, false));
        Assert.Contains("no longer open", ApplicationRules.SubmitProblem(OpportunityStatus.Reviewing, true, fit, false, false, false));
        Assert.Contains("last joining date", ApplicationRules.SubmitProblem(OpportunityStatus.Open, false, fit, false, false, false));
        Assert.StartsWith("You cannot enter", ApplicationRules.SubmitProblem(
            OpportunityStatus.Open, true, SomeFit(canEnter: false) with { MeritOk = false, MinMerit = 60 }, false, false, false));
        Assert.NotNull(ApplicationRules.SubmitProblem(OpportunityStatus.Open, true, fit, removedBefore: true, false, false));
        Assert.Equal("You are already competing in this opportunity.",
            ApplicationRules.SubmitProblem(OpportunityStatus.Open, true, fit, false, alreadyEntered: true, false));
        Assert.Equal("You have already applied to this opportunity.",
            ApplicationRules.SubmitProblem(OpportunityStatus.Open, true, fit, false, false, alreadyApplied: true));
        // A viewer with no fit to read — never a freelancer here — is not refused by it.
        Assert.Null(ApplicationRules.SubmitProblem(OpportunityStatus.Open, true, null, false, false, false));
    }

    [Fact]
    public void Only_an_application_under_review_on_an_open_opportunity_can_be_decided()
    {
        const ApplicationStatus review = ApplicationStatus.UnderReview, yes = ApplicationStatus.Selected,
            no = ApplicationStatus.NotSelected, removed = ApplicationStatus.Removed;
        Assert.Null(ApplicationRules.DecisionProblem(review, yes, OpportunityStatus.Open, workArrived: false));
        Assert.Null(ApplicationRules.DecisionProblem(review, no, OpportunityStatus.Open, workArrived: false));
        // An answer can be changed while the opportunity is open: a no becomes a
        // yes whatever has happened, a yes becomes a no only before work.
        Assert.Null(ApplicationRules.DecisionProblem(no, yes, OpportunityStatus.Open, workArrived: true));
        Assert.Null(ApplicationRules.DecisionProblem(yes, no, OpportunityStatus.Open, workArrived: false));
        Assert.Contains("Remove from opportunity", ApplicationRules.DecisionProblem(yes, no, OpportunityStatus.Open, workArrived: true));
        // Not to itself, not once the opportunity has moved on, never after a removal.
        Assert.Contains("already selected", ApplicationRules.DecisionProblem(yes, yes, OpportunityStatus.Open, workArrived: false));
        Assert.Contains("already turned down", ApplicationRules.DecisionProblem(no, no, OpportunityStatus.Open, workArrived: false));
        Assert.Contains("no longer open", ApplicationRules.DecisionProblem(review, yes, OpportunityStatus.Reviewing, workArrived: false));
        Assert.Contains("final", ApplicationRules.DecisionProblem(removed, yes, OpportunityStatus.Open, workArrived: false));
        Assert.Contains("withdrew", ApplicationRules.DecisionProblem(ApplicationStatus.Withdrawn, yes, OpportunityStatus.Open, workArrived: false));
        Assert.Contains("withdrew", ApplicationRules.DecisionProblem(ApplicationStatus.Withdrawn, no, OpportunityStatus.Open, workArrived: false));
    }

    [Fact]
    public void A_closed_opportunity_answers_for_an_undecided_application_and_a_selection_follows_the_opportunity()
    {
        Assert.False(ApplicationRules.ClosedUndecided(ApplicationStatus.UnderReview, OpportunityStatus.Open));
        Assert.True(ApplicationRules.ClosedUndecided(ApplicationStatus.UnderReview, OpportunityStatus.Reviewing));
        Assert.True(ApplicationRules.ClosedUndecided(ApplicationStatus.UnderReview, OpportunityStatus.Cancelled));
        // A decided application keeps its own answer whatever the opportunity did.
        Assert.False(ApplicationRules.ClosedUndecided(ApplicationStatus.NotSelected, OpportunityStatus.Reviewing));
        Assert.False(ApplicationRules.ClosedUndecided(ApplicationStatus.Selected, OpportunityStatus.Awarded));

        Assert.Equal("open", ApplicationRules.Outcome(ApplicationStatus.Selected, OpportunityStatus.Open, won: false));
        Assert.Equal("reviewing", ApplicationRules.Outcome(ApplicationStatus.Selected, OpportunityStatus.Reviewing, won: false));
        Assert.Equal("won", ApplicationRules.Outcome(ApplicationStatus.Selected, OpportunityStatus.Awarded, won: true));
        Assert.Equal("lost", ApplicationRules.Outcome(ApplicationStatus.Selected, OpportunityStatus.Awarded, won: false));
        Assert.Equal("cancelled", ApplicationRules.Outcome(ApplicationStatus.Selected, OpportunityStatus.Cancelled, won: false));
        // Only a selection has an outcome; every other status is its own answer.
        Assert.Null(ApplicationRules.Outcome(ApplicationStatus.Withdrawn, OpportunityStatus.Awarded, won: false));
        Assert.Null(ApplicationRules.Outcome(ApplicationStatus.UnderReview, OpportunityStatus.Cancelled, won: false));
    }

    // -------------------------------------------------------- the names

    [Fact]
    public void Names_and_the_portfolio_round_trip()
    {
        Assert.Equal("under_review", ApplicationRules.StatusName(ApplicationStatus.UnderReview));
        Assert.Equal("not_selected", ApplicationRules.StatusName(ApplicationStatus.NotSelected));
        Assert.Equal("removed", ApplicationRules.StatusName(ApplicationStatus.Removed));
        Assert.Equal("withdrawn", ApplicationRules.StatusName(ApplicationStatus.Withdrawn));
        Assert.Equal(Commitment.Partial, ApplicationRules.ParseCommitment(" Partial "));
        Assert.Null(ApplicationRules.ParseCommitment("all of it"));
        Assert.Equal("limited", ApplicationRules.CommitmentName(Commitment.Limited));

        var items = new[] { new PortfolioItem("Shop", "Lead", "Django", "Orders stopped vanishing.", 100) };
        var back = ApplicationRules.Portfolio(ApplicationRules.PortfolioJson(items));
        Assert.Single(back);
        Assert.Equal("Shop", back[0].Title);
        Assert.Equal(100, back[0].Relevance);
        Assert.Empty(ApplicationRules.Portfolio("not json"));
        Assert.Empty(ApplicationRules.Portfolio((string?)null));
    }
}
