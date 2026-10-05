using System.Text.Json;
using WinnersPortal.AiEvals;
using WinnersPortal.Domain;
using WinnersPortal.Services.Ai;
using Xunit;

namespace WinnersPortal.Api.Tests;

/// <summary>
/// The golden-set eval's pure parts: every case on disk loads and builds
/// the prompt it says it does (so a broken case fails the build, not a
/// run somebody paid for), the checks read what they claim to, the
/// runner spends no more than the cap, and a report can be set beside
/// another. Nothing here calls a model.
/// </summary>
public class AiEvalTests
{
    // ------------------------------------------------------ the cases

    [Fact]
    public void Every_case_on_disk_loads_and_builds_its_prompt()
    {
        var cases = EvalCases.Load(Path.Combine(EvalCases.RepoRoot(), "evals"));

        Assert.True(cases.Count >= 5, $"only {cases.Count} cases were found");
        foreach (var slug in EvalFeatures.Slugs.Keys)
            Assert.Contains(cases, c => c.Slug == slug);
        foreach (var c in cases)
        {
            var job = EvalFeatures.Build(c.Feature, c.Input);
            Assert.Contains("JSON", job.Prompt.System);
            Assert.NotEmpty(job.CanonicalInput);
            Assert.True(c.Expect.Judge is null || c.Expect.Judge.Length > 20, $"{c.Id}: a rubric this short says nothing");
        }
    }

    [Fact]
    public void A_case_with_an_unknown_check_or_a_stray_key_is_refused_by_name()
    {
        using var dir = new TempCases();
        dir.Write("categorise", "typo.json", """
            { "input": { "title": "A logo for a small pharmacy chain", "briefMarkdown": "Mark, wordmark, two colour directions and vector files." },
              "expect": { "category": "design", "mentionAny": [["logo"]] }, "expected": {} }
            """);

        var e = Assert.Throws<EvalCaseException>(() => EvalCases.Load(dir.Path));

        Assert.Contains(e.Problems, p => p.Contains("categorise/typo.json") && p.Contains("expect.mentionAny"));
        Assert.Contains(e.Problems, p => p.Contains("categorise/typo.json") && p.Contains("expected is not part of a case"));
    }

    [Fact]
    public void A_folder_that_is_not_a_feature_and_an_input_that_builds_no_prompt_are_refused()
    {
        using var dir = new TempCases();
        dir.Write("digests", "x.json", """{ "input": {}, "expect": {} }""");
        dir.Write("categorise", "thin.json", """{ "input": { "title": "Logo" }, "expect": {} }""");

        var e = Assert.Throws<EvalCaseException>(() => EvalCases.Load(dir.Path));

        Assert.Contains(e.Problems, p => p.StartsWith("digests/ is not a feature"));
        Assert.Contains(e.Problems, p => p.Contains("categorise/thin.json") && p.Contains("too thin"));
    }

    [Fact]
    public void A_folders_defaults_merge_under_each_case_and_forbidden_words_add_up()
    {
        using var dir = new TempCases();
        dir.Write("entry-digest", EvalCases.DefaultsFile, """{ "forbidWords": ["best"], "minCount": 2, "maxCount": 4 }""");
        dir.Write("entry-digest", "one.json", """
            { "input": { "facts": { "opportunityTitle": "T", "brief": "B", "entrantNote": "", "pushCount": 1, "lastPushAtUtc": null,
                "milestonesAll": ["a"], "milestonesClaimed": [], "languages": ["Python"], "fileCount": 3, "hasTests": false, "hasReadme": true, "hasCi": false },
                "sendCode": false },
              "expect": { "forbidWords": ["models.py"], "maxCount": 3 } }
            """);

        var c = Assert.Single(EvalCases.Load(dir.Path));

        Assert.Equal(["best", "models.py"], c.Expect.ForbidWords);
        Assert.Equal(2, c.Expect.MinCount);
        Assert.Equal(3, c.Expect.MaxCount);
        Assert.Contains("\"fileTree\":null", EvalFeatures.Build(c.Feature, c.Input).CanonicalInput);
    }

    [Fact]
    public void Loading_can_be_narrowed_to_a_feature_and_a_case()
    {
        var root = Path.Combine(EvalCases.RepoRoot(), "evals");
        var one = EvalCases.Load(root, ["categorise"], "textile-dashboard");
        Assert.Equal("categorise/textile-dashboard", Assert.Single(one).Id);
        Assert.All(EvalCases.Load(root, ["milestones"]), c => Assert.Equal(AiFeature.MilestoneExtraction, c.Feature));
    }

    // ------------------------------------------------------ the checks

    [Fact]
    public void A_categorise_case_reads_the_key_the_subcategory_and_confidence()
    {
        var c = Case(AiFeature.CategorySuggestion, """{ "title": "Logo for a pharmacy chain", "briefMarkdown": "A mark and a wordmark, two colour directions, vector files." }""",
            """{ "category": "design", "subcategory": "logo", "confident": true }""");

        Assert.Empty(EvalChecks.Run(c, """{"category":"design","subcategory":"logo","because":"asks for a logo","confident":true}""").Failures);

        var (_, failures) = EvalChecks.Run(c, """{"category":"web","subcategory":null,"because":"mentions a website","confident":false}""");
        Assert.Contains("category is web, expected design", failures);
        Assert.Contains("subcategory is none, expected logo", failures);
        Assert.Contains("confident is false, expected true", failures);
    }

    [Fact]
    public void A_case_that_says_no_subcategory_fails_one_that_names_one()
    {
        var c = Case(AiFeature.CategorySuggestion, """{ "title": "Something for the shop", "briefMarkdown": "We need a website, or maybe an app, not sure yet, for the shop." }""",
            """{ "category": "web", "subcategory": null }""");

        var (_, failures) = EvalChecks.Run(c, """{"category":"web","subcategory":"landing","because":"","confident":false}""");

        Assert.Equal(["subcategory is landing, expected none"], failures);
    }

    [Fact]
    public void An_answer_the_validator_refuses_fails_on_the_validator_alone()
    {
        var c = Case(AiFeature.CategorySuggestion, """{ "title": "Logo for a pharmacy chain", "briefMarkdown": "A mark and a wordmark, two colour directions, vector files." }""",
            """{ "category": "design" }""");

        var (canonical, failures) = EvalChecks.Run(c, """{"category":"graphics"}""");

        Assert.Null(canonical);
        Assert.Equal(["validator: The model named a category this portal does not have."], failures);
    }

    [Fact]
    public void Milestone_bounds_mentions_and_forbidden_words_read_the_list()
    {
        var c = Case(AiFeature.MilestoneExtraction,
            """{ "title": "WhatsApp order bridge for a grocery chain", "briefMarkdown": "Watch the order sheet and send WhatsApp status messages; a one-page admin screen to resend.", "delivery": "repository", "requiresCompose": true }""",
            """{ "minCount": 3, "maxCount": 4, "mentionsAny": [["WhatsApp"], ["admin", "resend"]], "forbidWords": ["payment"], "forbidPattern": "\\$\\s?\\d" }""");

        Assert.Empty(EvalChecks.Run(c, """
            {"milestones":[
              {"title":"Compose file runs","description":"docker compose up brings everything up"},
              {"title":"Sheet polling","description":null},
              {"title":"WhatsApp templates","description":"status messages on each change"},
              {"title":"Admin screen","description":"today's orders and a resend button"}]}
            """).Failures);

        var (_, failures) = EvalChecks.Run(c, """
            {"milestones":[
              {"title":"Sheet polling","description":"idempotent"},
              {"title":"Payment step","description":"collect $5 per order via a whatsapp link"}]}
            """);
        Assert.Contains("milestones has 2 items, expected at least 3", failures);
        Assert.Contains("nothing in the answer mentions admin / resend", failures);
        Assert.Contains(failures, f => f.StartsWith("the answer says \"payment\""));
        Assert.Contains(failures, f => f.StartsWith("the answer matches /"));
        // "whatsapp" inside "a whatsapp link" is a whole word and counts; "WhatsApps" would not.
        Assert.DoesNotContain("nothing in the answer mentions WhatsApp", failures);
    }

    [Theory]
    [InlineData("Add Docker to your skills", "docker", true)]
    [InlineData("Dockerise the app", "docker", false)]
    [InlineData("Ranked third of ten", "rank", false)]
    [InlineData("Ranked third of ten", "ranked", true)]
    [InlineData("You are better than most", "better than", true)]
    [InlineData("Strength 83%", "%", true)]
    public void Forbidden_and_expected_words_match_whole_words_only(string text, string word, bool hit) =>
        Assert.Equal(hit, EvalChecks.ContainsWord(text, word));

    [Fact]
    public void A_review_must_copy_every_id_back_invent_none_and_write_no_figure()
    {
        var c = Case(AiFeature.ProfileReview, """
            { "headline": "Backend developer", "hasAbout": true, "primaryCategory": "Web development", "otherCategories": [],
              "skills": [{ "name": "Python", "level": "expert", "years": 6 }], "projects": 1, "projectsWithLinks": 0, "projectKinds": [],
              "hasAvailability": true, "hoursPerWeek": 30, "yearsExperience": 6, "hasPayout": true, "strengthPercent": 70, "stepsNotDone": [],
              "openOpportunities": 4, "openOpportunitiesEnterable": 1,
              "improvements": [
                { "id": "skill:docker", "change": "Add Docker to your skills", "because": "2 open opportunities require it", "gain": 50 },
                { "id": "projects", "change": "Add 1 more portfolio project", "because": "worth 4 merit points", "gain": 25 },
                { "id": "links", "change": "Link your projects", "because": "worth 2 merit points", "gain": 10 },
                { "id": "about", "change": "Write an About You", "because": "worth 3 merit points", "gain": 5 } ] }
            """, "{}");

        Assert.Empty(EvalChecks.Run(c, """
            {"strongFor":"python backend work","improvements":[
              {"id":"skill:docker","text":"Add Docker to your skills, since two open opportunities require it."},
              {"id":"projects","text":"Add another portfolio project to open one more opportunity."},
              {"id":"links","text":"Link your projects so a client can open them."}]}
            """).Failures);

        var (_, failures) = EvalChecks.Run(c, """
            {"strongFor":"python","improvements":[
              {"id":"skill:docker","text":"Add Docker: 2 open opportunities require it."},
              {"id":"about","text":"Write an About You."},
              {"id":"skill:react","text":"Add React."}]}
            """);
        Assert.Contains("improvement projects was handed in and came back without words", failures);
        Assert.Contains("improvement links was handed in and came back without words", failures);
        Assert.Contains("improvement about was not handed in — an id was invented", failures);
        Assert.Contains("improvement skill:react was not handed in — an id was invented", failures);
        Assert.Contains("an improvement's words carry a figure; the figures are the portal's arithmetic", failures);
    }

    [Fact]
    public void A_digest_needs_two_to_four_starting_points()
    {
        var c = Case(AiFeature.EntryDigest, """
            { "facts": { "opportunityTitle": "T", "brief": "B", "entrantNote": "", "pushCount": 3, "lastPushAtUtc": "2026-09-27T16:40:00Z",
                "milestonesAll": ["a", "b"], "milestonesClaimed": ["a"], "languages": ["Python"], "fileCount": 10, "hasTests": true, "hasReadme": true, "hasCi": false },
              "treePaths": ["README.md", "app/models.py"], "readme": "# App", "sendCode": false }
            """, """{ "forbidWords": ["models.py"] }""");

        Assert.Empty(EvalChecks.Run(c, """{"summary":"A Python app with one of two milestones claimed.","stack":["Python"],"milestoneCoverage":"1 of 2","quality":"Tests and a README are present; no CI.","reviewFocus":["The unclaimed milestone","The tests"]}""").Failures);

        var (_, failures) = EvalChecks.Run(c, """{"summary":"See app/models.py.","stack":[],"milestoneCoverage":null,"quality":null,"reviewFocus":["Everything"]}""");
        Assert.Contains("reviewFocus has 1 starting points; the prompt asks for 2 to 4", failures);
        Assert.Contains(failures, f => f.StartsWith("the answer says \"models.py\""));
    }

    // ------------------------------------------------------- the judge

    [Theory]
    [InlineData("{\"pass\":true,\"reason\":\"Says only what the facts show.\"}", true, "Says only what the facts show.")]
    [InlineData("```json\n{\"pass\":false,\"reason\":\"Invents a client.\"}\n```", false, "Invents a client.")]
    [InlineData("I think it passes.", false, "the judge answered with no JSON object")]
    [InlineData("{\"pass\":\"yes\"}", false, "(no reason given)")]
    public void The_judges_verdict_is_read_strictly_and_an_unreadable_one_fails(string text, bool pass, string reason)
    {
        var v = EvalJudge.Parse(text);
        Assert.Equal(pass, v.Pass);
        Assert.Equal(reason, v.Reason);
    }

    [Fact]
    public void The_judge_is_handed_the_rubric_the_facts_and_the_answer_and_asked_for_pass_or_fail()
    {
        var (system, user) = EvalJudge.Prompt("No invented clients.", "{\"a\":1}", "{\"b\":2}");
        Assert.Contains("{\"pass\":true|false,\"reason\":string}", system);
        Assert.Contains("never to judge, score or rank", system);
        Assert.Contains("No invented clients.", user);
        Assert.Contains("{\"a\":1}", user);
        Assert.Contains("{\"b\":2}", user);
    }

    // ------------------------------------------------------- the runner

    [Fact]
    public async Task A_run_scores_each_case_asks_the_judge_where_there_is_a_rubric_and_reports_the_versions()
    {
        var cases = new[]
        {
            Case(AiFeature.CategorySuggestion, """{ "title": "Logo for a pharmacy chain", "briefMarkdown": "A mark and a wordmark, two colour directions, vector files." }""",
                """{ "category": "design", "judge": "The reason names the logo, not the website." }""", "logo"),
            Case(AiFeature.CategorySuggestion, """{ "title": "Store locator widget for a pharmacy chain", "briefMarkdown": "An embeddable map widget: branches, search by area, opening hours." }""",
                """{ "category": "web" }""", "locator"),
        };
        // The taxonomy travels in every user prompt ("Logo design" among
        // its labels), so the stub reads the title, not a word.
        var model = new StubModel("gemini", "test-model", (_, user) =>
            user.Contains("Logo for a pharmacy chain") ? """{"category":"design","subcategory":"logo","because":"asks for a logo","confident":true}"""
            : """{"category":"mobile","subcategory":null,"because":"a widget","confident":true}""");
        var judge = new StubModel("gemini", "judge-model", (_, _) => """{"pass":true,"reason":"Names the logo."}""");
        var log = new List<string>();

        var report = await new EvalRunner(model, judge, maxCalls: 10, dryRun: false, log.Add).RunAsync(cases, CancellationToken.None);

        Assert.Equal(3, report.Calls); // two cases, one judge
        Assert.Equal(new AiTokens(30, 15), report.Tokens); // ten in and five out on each of the three
        Assert.Null(report.CostUsd); // no price list was handed to the run
        Assert.Contains("Tokens: 30 in · 15 out · no price saved for the model", report.ToMarkdown());
        Assert.False(report.CapReached);
        Assert.Equal(1, report.Passed);
        Assert.Equal(1, report.Failed);
        Assert.Equal("gemini/judge-model", report.JudgeModel);
        Assert.Equal(AiPrompts.Version(AiFeature.CategorySuggestion), report.PromptVersions["categorise"]);
        var logo = report.Cases.Single(c => c.Name == "logo");
        Assert.True(logo.Pass);
        Assert.True(logo.Judge!.Pass);
        var locator = report.Cases.Single(c => c.Name == "locator");
        Assert.Equal(["category is mobile, expected web"], locator.Failures);
        Assert.Null(locator.Judge);
        Assert.Contains(log, l => l.StartsWith("categorise/locator: FAIL"));
    }

    [Fact]
    public async Task The_call_cap_ends_the_run_and_the_cases_after_it_say_so()
    {
        var cases = Enumerable.Range(1, 3).Select(i =>
            Case(AiFeature.CategorySuggestion, """{ "title": "Logo for a pharmacy chain", "briefMarkdown": "A mark and a wordmark, two colour directions, vector files." }""",
                """{ "category": "design" }""", $"c{i}")).ToArray();
        var model = new StubModel("openai", "m", (_, _) => """{"category":"design","subcategory":"logo","because":"","confident":true}""");

        var report = await new EvalRunner(model, null, maxCalls: 2, dryRun: false).RunAsync(cases, CancellationToken.None);

        Assert.Equal(2, report.Calls);
        Assert.True(report.CapReached);
        Assert.Equal(2, report.Passed);
        Assert.Equal(1, report.NotRun);
        var last = report.Cases.Single(c => c.Name == "c3");
        Assert.False(last.Ran);
        Assert.Equal("the call cap (2) was reached before it", last.Error);
        Assert.Contains("the call cap was reached", report.ToMarkdown());
    }

    [Fact]
    public async Task A_case_with_a_rubric_and_no_judge_fails_and_says_so_and_a_refusal_is_an_error_not_a_crash()
    {
        var cases = new[]
        {
            Case(AiFeature.CategorySuggestion, """{ "title": "Logo for a pharmacy chain", "briefMarkdown": "A mark and a wordmark, two colour directions, vector files." }""",
                """{ "category": "design", "judge": "The reason names the logo." }""", "judged"),
            Case(AiFeature.CategorySuggestion, """{ "title": "Logo for a pharmacy chain", "briefMarkdown": "A mark and a wordmark, two colour directions, vector files." }""",
                """{ "category": "design" }""", "refused"),
        };
        var calls = 0;
        var model = new StubModel("anthropic", "m", (_, _) => ++calls == 1
            ? """{"category":"design","subcategory":"logo","because":"","confident":true}"""
            : throw new AiProviderException(429, "The provider answered 429: slow down"));

        var report = await new EvalRunner(model, null, maxCalls: 10, dryRun: false).RunAsync(cases, CancellationToken.None);

        var judged = report.Cases.Single(c => c.Name == "judged");
        Assert.False(judged.Pass);
        Assert.Equal(["judge: the case has a rubric and the run has no judge (--no-judge)"], judged.Failures);
        var refused = report.Cases.Single(c => c.Name == "refused");
        Assert.True(refused.Ran);
        Assert.False(refused.Pass);
        Assert.StartsWith("the provider did not answer: The provider answered 429", refused.Error);
        Assert.Equal(2, report.Calls);
    }

    [Fact]
    public async Task A_dry_run_builds_every_prompt_and_sends_nothing()
    {
        var cases = new[]
        {
            Case(AiFeature.CategorySuggestion, """{ "title": "Logo for a pharmacy chain", "briefMarkdown": "A mark and a wordmark, two colour directions, vector files." }""",
                """{ "category": "design" }""", "logo"),
        };
        var model = new StubModel("gemini", "m", (_, _) => throw new InvalidOperationException("must not be called"));

        var report = await new EvalRunner(model, model, maxCalls: 10, dryRun: true).RunAsync(cases, CancellationToken.None);

        Assert.Equal(0, report.Calls);
        Assert.True(report.DryRun);
        Assert.Equal(1, report.NotRun);
        Assert.Contains("Dry run", report.ToMarkdown());
    }

    [Fact]
    public async Task A_run_is_priced_case_by_case_when_the_models_have_prices_and_not_at_all_when_the_judges_has_none()
    {
        var cases = new[]
        {
            Case(AiFeature.CategorySuggestion, """{ "title": "Logo for a pharmacy chain", "briefMarkdown": "A mark and a wordmark, two colour directions, vector files." }""",
                """{ "category": "design", "judge": "The reason names the logo." }""", "judged"),
            Case(AiFeature.CategorySuggestion, """{ "title": "Logo for a pharmacy chain", "briefMarkdown": "A mark and a wordmark, two colour directions, vector files." }""",
                """{ "category": "design" }""", "plain"),
        };
        var model = new StubModel("gemini", "m", (_, _) => """{"category":"design","subcategory":"logo","because":"","confident":true}""");
        var judge = new StubModel("gemini", "j", (_, _) => """{"pass":true,"reason":"Fine."}""");

        // Ten in at $1 a million and five out at $2 a million: $0.00002 a call.
        var priced = await new EvalRunner(model, judge, 10, false, prices: AiPrices.Parse("m 1 2\nj 1 2")).RunAsync(cases, CancellationToken.None);
        Assert.Equal(0.00002m, priced.Cases.Single(c => c.Name == "plain").CostUsd);
        Assert.Equal(0.00004m, priced.Cases.Single(c => c.Name == "judged").CostUsd); // the judge's call is on the case
        Assert.Equal(new AiTokens(20, 10), priced.Cases.Single(c => c.Name == "judged").Tokens);
        Assert.Equal(0.00006m, priced.CostUsd);
        Assert.Contains("| categorise | 2 | 0 | 0 | 5 | 30 / 15 | $0.0001 |", priced.ToMarkdown());

        // The judge's model unpriced: the judged case has no number, and so the run has none.
        var half = await new EvalRunner(model, judge, 10, false, prices: AiPrices.Parse("m 1 2")).RunAsync(cases, CancellationToken.None);
        Assert.Equal(0.00002m, half.Cases.Single(c => c.Name == "plain").CostUsd);
        Assert.Null(half.Cases.Single(c => c.Name == "judged").CostUsd);
        Assert.Null(half.CostUsd);
        Assert.Contains("| categorise | 2 | 0 | 0 | 5 | 30 / 15 | – |", half.ToMarkdown());
    }

    [Fact]
    public void The_switch_decides_whether_a_run_may_start_and_only_a_dry_run_or_a_typed_no_settings_goes_round_it()
    {
        var on = new EvalSettings.Saved("gemini", "k", Enabled: "true");
        var off = new EvalSettings.Saved("gemini", "k", Enabled: "false");
        var unsaid = new EvalSettings.Saved("gemini", "k");
        Assert.Null(EvalSettings.RunProblem(on, "read", noSettings: false, dryRun: false));
        Assert.Contains("switched off", EvalSettings.RunProblem(off, "read", false, false));
        Assert.Contains("switched off", EvalSettings.RunProblem(unsaid, "read", false, false));
        Assert.Contains("cannot be seen", EvalSettings.RunProblem(null, "not read: no key ring in keys", false, false));
        Assert.Contains("no key ring in keys", EvalSettings.RunProblem(null, "not read: no key ring in keys", false, false));
        Assert.Null(EvalSettings.RunProblem(off, "read", noSettings: false, dryRun: true));
        Assert.Null(EvalSettings.RunProblem(null, "not read", noSettings: true, dryRun: false));
        Assert.True(on.IsEnabled);
        Assert.False(unsaid.IsEnabled);
    }

    // ------------------------------------------------------- the report

    [Fact]
    public void A_report_round_trips_through_json_and_sits_beside_another_case_by_case()
    {
        var a = Report("gemini", "gemini-3.6-flash",
            new CaseResult("categorise", "logo", true, true, [], null, "{}", 900, null),
            new CaseResult("categorise", "locator", true, false, ["category is mobile, expected web"], null, "{}", 1200, null),
            new CaseResult("milestones", "bridge", true, true, [], new JudgeResult(true, "Runs first."), "{}", 3000, null));
        var b = EvalReport.FromJson(Report("openai", "gpt-5.5",
            new CaseResult("categorise", "logo", true, true, [], null, "{}", 700, null),
            new CaseResult("categorise", "locator", true, true, [], null, "{}", 800, null),
            new CaseResult("milestones", "bridge", false, false, [], null, null, 0, "the call cap (2) was reached before it")).ToJson());

        Assert.Equal("openai", b.Provider);
        Assert.Equal(2, b.Passed);
        Assert.Equal(1, b.NotRun);

        var table = a.Compare(b);
        Assert.Contains("| categorise | 1 | 2 | 2 |", table);
        Assert.Contains("- A: gemini/gemini-3.6-flash (categorise v1, milestones v1) · 2026-09-29 12:00 UTC · 0 in · 0 out · no price saved for the model", table);
        Assert.Contains("| categorise/locator | **fail** | pass | 1,200 | 800 |", table);
        Assert.Contains("| milestones/bridge | pass | not run | 3,000 | – |", table);
        Assert.Contains("### categorise/locator", table);
        Assert.Contains("- A failed: category is mobile, expected web", table);
        Assert.DoesNotContain("### milestones/bridge", table); // not run on one side is not a difference

        var md = a.ToMarkdown();
        Assert.Contains("**2 passed, 1 failed**", md);
        Assert.Contains("### categorise/locator", md);
        Assert.Contains("- milestones/bridge: pass — Runs first.", md);
        Assert.StartsWith("20260929-120000-gemini-gemini-3.6-flash", a.FileStem);
    }

    // ------------------------------------------- the provider and the key

    private static Func<string, string?> Env(params (string Name, string Value)[] set) =>
        name => set.FirstOrDefault(v => v.Name == name).Value;

    [Fact]
    public void What_is_saved_on_the_settings_screen_wins_over_the_variables()
    {
        var choice = EvalSettings.Choose(null, new EvalSettings.Saved("Anthropic", "saved-key"),
            Env(("WP_EVAL_PROVIDER", "openai"), ("WP_EVAL_API_KEY", "shell-key")));
        Assert.Equal(new EvalSettings.Choice("anthropic", EvalSettings.FromSettings, "saved-key", EvalSettings.FromSettings,
            EvalSettings.DefaultMaxCalls, "default", EvalSettings.DefaultTimeoutSeconds, "default"), choice);

        // The cap and the timeout are read the same way, and --max-calls wins for one run.
        var capped = EvalSettings.Choose(null, new EvalSettings.Saved("gemini", "k", MaxCalls: "40", TimeoutSeconds: "30"),
            Env(("WP_EVAL_MAX_CALLS", "7")));
        Assert.Equal((40, EvalSettings.FromSettings, 30, EvalSettings.FromSettings),
            (capped.MaxCalls, capped.MaxCallsFrom, capped.TimeoutSeconds, capped.TimeoutFrom));
        var shell = EvalSettings.Choose(null, new EvalSettings.Saved("gemini", "k", MaxCalls: "lots"), Env(("WP_EVAL_MAX_CALLS", "7")));
        Assert.Equal((7, "WP_EVAL_MAX_CALLS"), (shell.MaxCalls, shell.MaxCallsFrom));
        Assert.Equal("WP_EVAL_MAXCALLS", EvalSettings.Choose(null, null, Env(("WP_EVAL_MAXCALLS", "9"))).MaxCallsFrom);
        var typed = EvalSettings.Choose(null, new EvalSettings.Saved("gemini", "k", "40"), Env(), 3);
        Assert.Equal((3, "--max-calls"), (typed.MaxCalls, typed.MaxCallsFrom));
    }

    [Fact]
    public void The_variables_fill_in_only_what_is_not_saved()
    {
        var keyOnly = EvalSettings.Choose(null, new EvalSettings.Saved(null, "saved-key"),
            Env(("WP_EVAL_PROVIDER", "openai"), ("WP_EVAL_API_KEY", "shell-key")));
        Assert.Equal(("openai", "WP_EVAL_PROVIDER"), (keyOnly.Provider, keyOnly.ProviderFrom));
        Assert.Equal(("saved-key", EvalSettings.FromSettings), (keyOnly.ApiKey, keyOnly.ApiKeyFrom));

        // Blank is not saved; the tool's own spelling of the key comes before the convention's.
        var none = EvalSettings.Choose(null, new EvalSettings.Saved(" ", ""),
            Env(("WP_EVAL_APIKEY", "pinned-key"), ("WP_EVAL_API_KEY", "shell-key")));
        Assert.Equal(("gemini", "default"), (none.Provider, none.ProviderFrom));
        Assert.Equal(("shell-key", "WP_EVAL_API_KEY"), (none.ApiKey, none.ApiKeyFrom));
        Assert.Equal("WP_EVAL_APIKEY", EvalSettings.Choose(null, null, Env(("WP_EVAL_APIKEY", "k"))).ApiKeyFrom);
    }

    [Fact]
    public void A_provider_typed_for_one_run_wins_and_no_key_anywhere_is_null()
    {
        var run = EvalSettings.Choose("OpenAI", new EvalSettings.Saved("anthropic", null), Env());
        Assert.Equal(new EvalSettings.Choice("openai", "--provider", null, null,
            EvalSettings.DefaultMaxCalls, "default", EvalSettings.DefaultTimeoutSeconds, "default"), run);
    }

    // ------------------------------------------------------- helpers

    private static EvalCase Case(AiFeature feature, string inputJson, string expectJson, string name = "case")
    {
        var problems = new List<string>();
        using var expect = JsonDocument.Parse(expectJson);
        var expectation = Expectation.Parse(null, expect.RootElement.Clone(), problems);
        Assert.Empty(problems);
        using var input = JsonDocument.Parse(inputJson);
        return new EvalCase(feature, EvalFeatures.SlugOf(feature), name, null, input.RootElement.Clone(), expectation, name + ".json");
    }

    private static EvalReport Report(string provider, string model, params CaseResult[] cases) => new(
        new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero), provider, model, null,
        new Dictionary<string, int> { ["categorise"] = 1, ["milestones"] = 1 }, cases.Count(c => c.Ran), 100, false, false, cases);

    /// <summary>Answers in five milliseconds, counting ten tokens in and five out on every call.</summary>
    private sealed class StubModel(string provider, string model, Func<string, string, string> answer) : IEvalModel
    {
        public string Provider => provider;

        public string Model => model;

        public Task<(string Text, int LatencyMs, AiTokens? Tokens)> CompleteAsync(string system, string user, object schema, CancellationToken ct) =>
            Task.FromResult((answer(system, user), 5, (AiTokens?)new AiTokens(10, 5)));
    }

    // --------------------------------------------------- matching

    [Fact]
    public void Every_matching_profile_on_disk_loads_and_names_real_categories()
    {
        var profiles = EvalMatching.Load(Path.Combine(EvalCases.RepoRoot(), "evals"));

        Assert.True(profiles.Count >= 10, $"only {profiles.Count} profiles were found");
        var keys = EvalMatching.Categories.Select(c => c.Key).ToHashSet();
        Assert.DoesNotContain("other", keys);
        foreach (var p in profiles)
        {
            Assert.All(p.Expected, k => Assert.Contains(k, keys));
            Assert.NotEmpty(p.Expected);
            Assert.True(p.Text.Length > 20, $"{p.Name}: nothing to embed");
        }
        // The prompt cases' loader walks past the matching folder rather than refusing it.
        Assert.DoesNotContain(EvalCases.Load(Path.Combine(EvalCases.RepoRoot(), "evals")), c => c.Slug == EvalMatching.Folder);
    }

    [Fact]
    public void A_matching_profile_naming_a_category_the_portal_lacks_or_nothing_to_embed_is_refused_by_name()
    {
        using var dir = new TempCases();
        dir.Write(EvalMatching.Folder, "typo.json", """
            { "input": { "headline": "A Django developer", "skills": [{ "name": "Django", "level": "expert", "years": 5 }] },
              "expect": { "categories": ["backend"] } }
            """);
        dir.Write(EvalMatching.Folder, "empty.json", """{ "input": { "skills": [] }, "expect": { "categories": [] } }""");
        dir.Write(EvalMatching.Folder, "silent.json", """{ "input": { "headline": "A writer" } }""");

        var e = Assert.Throws<EvalCaseException>(() => EvalMatching.Load(dir.Path));

        Assert.Contains(e.Problems, p => p.StartsWith("matching/typo: expect.categories names \"backend\""));
        Assert.Contains(e.Problems, p => p.StartsWith("matching/empty: has nothing to embed"));
        Assert.Contains(e.Problems, p => p.StartsWith("matching/silent: has no expect.categories"));
        Assert.Throws<EvalCaseException>(() => EvalMatching.Load(Path.Combine(dir.Path, "nowhere")));
    }

    [Fact]
    public void A_profile_is_embedded_as_prose_and_a_category_with_its_kinds_of_work_and_skills()
    {
        Assert.Equal(
            "Android developer. Skills: Kotlin (expert, 6 years), Room (advanced, 1 year).",
            EvalMatching.ProfileText("Android developer.", [new MatchingSkill("Kotlin", "Expert", 6), new MatchingSkill(" Room ", "advanced", 1)]));
        Assert.Equal("Skills: Technical writing (expert, 6 years).", EvalMatching.ProfileText(null, [new MatchingSkill("Technical writing", "expert", 6)]));
        Assert.Equal("", EvalMatching.ProfileText("  ", []));

        var web = EvalMatching.CategoryText(EvalMatching.Categories.Single(c => c.Key == "web"));
        Assert.StartsWith("Web development. Kinds of work: Front end, ", web);
        Assert.Contains(" Skills: HTML, CSS, JavaScript, ", web);
        Assert.Equal(1, web.Split("React").Length - 1); // listed once however many subcategories name it
    }

    [Fact]
    public void The_agreement_is_counted_over_every_profile_and_category_pair_and_the_best_threshold_named()
    {
        var keys = EvalMatching.Categories.Select(c => c.Key).ToList();
        float[] Unit(params string[] on) => keys.Select(k => on.Contains(k) ? 1f : 0f).ToArray();
        var categoryVectors = keys.Select(k => Unit(k)).ToList();
        var cases = new List<MatchingCase>
        {
            new("a", null, "A", [], new HashSet<string> { "web" }, "a.json"),
            new("b", null, "B", [], new HashSet<string> { "mobile", "design" }, "b.json"),
        };
        // A points at web with a little data in it (0.89 and 0.45); B at mobile and design alike (0.71 each).
        var a = Unit("web");
        a[keys.IndexOf("data")] = 0.5f;
        var profileVectors = new List<float[]> { a, Unit("mobile", "design") };

        var report = EvalMatching.Score(cases, profileVectors, categoryVectors, "openai", "text-embedding-3-small", 2, 100, new AiTokens(40, 0), 0.0001m);

        Assert.Equal(0.70, report.BestThreshold, 9); // the highest line on which every profile reads exactly as today
        Assert.Equal((3, 0, 0, 1.0, 2), (report.Best.Tp, report.Best.Fp, report.Best.Fn, report.Best.F1, report.Best.Exact));
        var loose = report.Thresholds.Single(r => Math.Abs(r.Threshold - 0.30) < 1e-9);
        Assert.Equal((3, 1, 0), (loose.Tp, loose.Fp, loose.Fn)); // data counted for A
        var strict = report.Thresholds.Single(r => Math.Abs(r.Threshold - 0.90) < 1e-9);
        Assert.Equal((0, 0, 3, 0), (strict.Tp, strict.Fp, strict.Fn, strict.Exact));
        var b = report.Profiles.Single(p => p.Name == "b");
        Assert.Equal(["design", "mobile"], b.Predicted);
        Assert.Empty(b.Missed);
        Assert.Equal(0.7071, b.Similarities["mobile"], 3);

        var md = report.ToMarkdown();
        Assert.Contains("**Best threshold 0.70**: precision 1.00, recall 1.00, F1 1.00 — 2 of 2 profiles read exactly as today.", md);
        Assert.Contains("| 0.70 (best) | 3 | 0 | 0 |", md);
        Assert.Contains("- ✓ a — today: web; vectors: web", md);
        Assert.Contains("about $0.0001", md);
        Assert.StartsWith("20", report.FileStem);
        Assert.Contains("-matching-openai-text-embedding-3-small", report.FileStem);
        Assert.Throws<ArgumentException>(() => EvalMatching.Score(cases, [a], categoryVectors, "openai", "m", 1, 1, AiTokens.None, null));
    }

    private sealed class TempCases : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wp-evals-" + Guid.NewGuid().ToString("N"));

        public void Write(string slug, string file, string json)
        {
            Directory.CreateDirectory(System.IO.Path.Combine(Path, slug));
            File.WriteAllText(System.IO.Path.Combine(Path, slug, file), json);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // a temp folder left behind is not a failed test
            }
        }
    }
}
