using WinnersPortal.Domain;
using System.Text.Json;
using WinnersPortal.Services.Ai;
using WinnersPortal.Services.Opportunities;
using WinnersPortal.Services.Profiles;
using WinnersPortal.Services.Settings;
using Xunit;

namespace WinnersPortal.Api.Tests;

/// <summary>
/// The AI layer's pure rules: what leaves the server (and what must not),
/// the wire format both providers get, the daily ceiling arithmetic, the
/// model-output gate, and the local spam scan. The worker is thin around
/// these — this is where the behaviour is pinned.
/// </summary>
public class AiTests
{
    // ------------------------------------------------------ the AI's name

    [Theory]
    [InlineData("gemini", "Acme Talent")]
    [InlineData("anthropic", "Acme Talent")]
    [InlineData("openai", "Acme Talent")]
    [InlineData("local", "local")]
    [InlineData(null, null)]
    public void A_member_sees_the_portals_own_name_never_the_vendors(string? provider, string? shown)
    {
        Assert.Equal(shown, AiBrand.Public(provider, "Acme Talent"));
    }

    [Theory]
    [InlineData("Acme Talent", "Acme Talent")]
    [InlineData("  Acme Talent  ", "Acme Talent")]
    [InlineData("", AiBrand.DefaultName)]
    [InlineData("   ", AiBrand.DefaultName)]
    [InlineData(null, AiBrand.DefaultName)]
    public void A_blank_portal_name_falls_back_to_the_default(string? stored, string expected)
    {
        Assert.Equal(expected, AiBrand.NameOf(stored));
    }

    [Fact]
    public void A_stored_draft_goes_out_under_the_portals_AI_name_without_its_model()
    {
        var view = AiService.Dto(new AiArtifact
        {
            Status = AiArtifactStatus.Done,
            OutputJson = "{\"a\":1}",
            Provider = "openai",
            Model = "gpt-5.5",
            CompletedAtUtc = DateTimeOffset.UtcNow,
        }, "Acme Talent");

        var json = JsonSerializer.Serialize(view);
        Assert.Equal("Acme Talent", view.Provider);
        Assert.DoesNotContain("openai", json);
        Assert.DoesNotContain("gpt-5.5", json);
    }

    // ------------------------------------------------------- model output

    [Theory]
    [InlineData("{\"a\":1}", "{\"a\":1}")]
    [InlineData("```json\n{\"a\":1}\n```", "{\"a\":1}")]
    [InlineData("Here you go:\n{\"a\":1}\nHope that helps!", "{\"a\":1}")]
    public void ExtractJson_digs_the_object_out_of_wrapping(string text, string expected)
    {
        Assert.Equal(expected, AiRules.ExtractJson(text));
    }

    [Theory]
    [InlineData("no json here")]
    [InlineData("")]
    [InlineData("}{")]
    public void ExtractJson_returns_null_when_there_is_no_object(string text)
    {
        Assert.Null(AiRules.ExtractJson(text));
    }

    [Fact]
    public void InputHash_is_stable_and_input_sensitive()
    {
        Assert.Equal(AiRules.InputHash("brief v1"), AiRules.InputHash("brief v1"));
        Assert.NotEqual(AiRules.InputHash("brief v1"), AiRules.InputHash("brief v2"));
        Assert.Equal(64, AiRules.InputHash("x").Length); // sha-256 hex
    }

    // ---------------------------------------------------- prompt versions

    public static IEnumerable<object[]> PromptedFeatures() =>
        Enum.GetValues<AiFeature>().Where(AiOptions.RequiresProvider).Select(f => new object[] { f });

    [Theory]
    [MemberData(nameof(PromptedFeatures))]
    public void Every_prompt_carries_a_version_from_one_up(AiFeature feature)
    {
        Assert.True(AiPrompts.Version(feature) >= 1);
    }

    [Fact]
    public void The_spam_scan_has_no_prompt_and_so_no_version()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AiPrompts.Version(AiFeature.SpamFilter));
    }

    [Fact]
    public void The_cache_key_folds_in_the_prompt_version_so_a_reworded_prompt_is_a_changed_input()
    {
        const string input = "{\"title\":\"x\"}";
        var keyed = AiRules.InputHash(AiFeature.CategorySuggestion, input);

        Assert.Equal(64, keyed.Length);
        Assert.Equal(keyed, AiRules.InputHash(AiFeature.CategorySuggestion, input));
        // The same text hashed without the version is a different key: the
        // version is really in there, not beside it.
        Assert.NotEqual(AiRules.InputHash(input), keyed);
        Assert.Equal(AiRules.InputHash($"prompt v{AiPrompts.Version(AiFeature.CategorySuggestion)}\n{input}"), keyed);
        // Two features at the same version and input share a key: the
        // artifact row is already per feature, so the key need not be.
        Assert.Equal(AiRules.InputHash(AiFeature.BriefCoach, input), keyed);
    }

    [Fact]
    public void The_spam_scan_hashes_its_input_alone()
    {
        Assert.Equal(AiRules.InputHash("[]"), AiRules.InputHash(AiFeature.SpamFilter, "[]"));
    }

    [Theory]
    [InlineData(AiFeature.EntryDigest, "entryDigest · prompt v2")]
    [InlineData(AiFeature.CategorySuggestion, "categorySuggestion · prompt v2")]
    public void A_calls_activity_row_names_the_feature_the_way_its_switch_does_and_the_prompt_version(
        AiFeature feature, string detail)
    {
        Assert.Equal(detail, AiPrompts.CallDetail(feature));
    }

    [Fact]
    public void Clip_marks_what_it_removed()
    {
        Assert.Equal("short", AiRules.Clip("  short  ", 100));
        var clipped = AiRules.Clip(new string('x', 200), 50);
        Assert.StartsWith(new string('x', 50), clipped);
        Assert.EndsWith("[…clipped]", clipped);
    }

    // ------------------------------------------------------ daily ceiling

    [Fact]
    public void The_day_key_is_the_utc_date()
    {
        // Half past eleven at UTC+2 is still the 31st in UTC; three hours on, it is the 1st.
        Assert.Equal("2026-08-31", AiQuotaRules.DayKey(new DateTimeOffset(2026, 8, 31, 23, 30, 0, TimeSpan.FromHours(2))));
        Assert.Equal("2026-09-01", AiQuotaRules.DayKey(new DateTimeOffset(2026, 8, 31, 23, 30, 0, TimeSpan.FromHours(2)).AddHours(3)));
    }

    [Fact]
    public void A_member_is_judged_by_the_minute_the_day_then_the_portal_and_each_refusal_has_its_line()
    {
        Assert.Contains("last minute", AiQuotaRules.Refusal(AiQuotaVerdict.MemberBurst));
        Assert.Contains("today's AI drafts", AiQuotaRules.Refusal(AiQuotaVerdict.MemberDay));
        Assert.Contains("daily AI call ceiling", AiQuotaRules.Refusal(AiQuotaVerdict.Portal));
        Assert.Throws<ArgumentOutOfRangeException>(() => AiQuotaRules.Refusal(AiQuotaVerdict.Allowed));

        // The portal alone is never capped as a member; an administrator presses uncapped.
        Assert.False(AiSpender.Portal.Capped);
        Assert.False(AiSpender.Member(Guid.NewGuid(), isAdmin: true).Capped);
        Assert.True(AiSpender.Member(Guid.NewGuid(), isAdmin: false).Capped);
    }

    [Fact]
    public void The_minutes_burst_admits_up_to_the_setting_and_counts_a_refused_press_too()
    {
        var burst = new AiBurst();
        var me = Guid.NewGuid();
        var you = Guid.NewGuid();
        var t = DateTimeOffset.Parse("2026-10-01T10:00:00Z");

        Assert.True(burst.TryAdmit(me, t, perMinute: 2));
        Assert.True(burst.TryAdmit(me, t.AddSeconds(10), perMinute: 2));
        Assert.False(burst.TryAdmit(me, t.AddSeconds(20), perMinute: 2));
        // Another member's minute is their own.
        Assert.True(burst.TryAdmit(you, t.AddSeconds(20), perMinute: 2));
        // A key held down: the refused press at :20 is in the window, so :61 still refuses…
        Assert.False(burst.TryAdmit(me, t.AddSeconds(61), perMinute: 2));
        // …and the window clears from the first press on.
        Assert.True(burst.TryAdmit(me, t.AddSeconds(125), perMinute: 2));
        // Zero refuses every press.
        Assert.False(burst.TryAdmit(you, t.AddMinutes(5), perMinute: 0));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 5)]
    [InlineData(3, 5)]
    public void A_job_waits_a_minute_then_five_between_attempts(int attempts, int minutes) =>
        Assert.Equal(TimeSpan.FromMinutes(minutes), AiRules.Backoff(attempts));

    // ------------------------------------------------------------ routing

    [Fact]
    public void The_queued_opportunity_route_carries_the_spam_scan_and_nothing_else()
    {
        // It is the one a client asks for that reads something stored —
        // entries. Everything else they ask for while writing reads the
        // form on screen and is answered in its own request.
        Assert.Equal(AiFeature.SpamFilter, AiFeatureRoutes.FromSlug("spam"));
    }

    [Theory]
    [InlineData("digest")]     // addressed by entry, not by opportunity
    [InlineData("narrative")]  // scheduled by the worker, never requestable
    [InlineData("standing")]   // its own pair: the owner or an administrator, not client-only
    [InlineData("coach")]      // reads the form now
    [InlineData("milestones")] // reads the form now
    [InlineData("seo")]        // reads the form now
    [InlineData("categorise")] // never had a stored subject to read
    [InlineData("anything")]
    public void Unroutable_slugs_yield_null(string slug)
    {
        Assert.Null(AiFeatureRoutes.FromSlug(slug));
    }

    [Theory]
    [InlineData("categorise", AiFeature.CategorySuggestion)]
    [InlineData("coach", AiFeature.BriefCoach)]
    [InlineData("requirements", AiFeature.RequirementsSuggestion)]
    [InlineData("milestones", AiFeature.MilestoneExtraction)]
    [InlineData("criteria", AiFeature.CriteriaSuggestion)]
    [InlineData("seo", AiFeature.SeoMetadata)]
    public void Form_slugs_route_to_the_tools_that_read_the_opportunity_form(string slug, AiFeature expected)
    {
        Assert.Equal(expected, AiFeatureRoutes.FromFormSlug(slug));
        // And every one of them has a prompt built from the form, which
        // always carries the title and the brief and says what a present
        // field means.
        var (system, user) = AiPrompts.ForForm(expected, ShopForm);
        Assert.Contains("never judge, score, rank", system);
        Assert.Contains("their decision", system);
        Assert.Contains("storefront", user);
        Assert.Contains("A shop", user);
    }

    /// <summary>A form with every section filled, so what each tool is handed can be read off its prompt.</summary>
    private static readonly OpportunityFormSnapshot ShopForm = new(
        Title: "A shop",
        BriefMarkdown: "Build me a storefront with a cart.",
        Category: "web",
        Subcategory: null,
        Skills: ["React", "react", "  ", "Postgres"],
        Requirements: [new("Language", "TypeScript 5"), new("Half", ""), new("", "no name")],
        Delivery: "repository",
        RequiresCompose: true,
        Milestones: [new("Checkout works", "Cards are charged"), new("", "untitled")],
        Criteria: [new(40, "Functionality", "Works end to end"), new(null, "", null)]);

    // ------------------------------------------- what each form tool reads

    [Theory]
    [InlineData(AiFeature.CategorySuggestion, "A shop", "Postgres")]
    [InlineData(AiFeature.RequirementsSuggestion, "Postgres", "TypeScript 5")]
    [InlineData(AiFeature.MilestoneExtraction, "TypeScript 5", "Checkout works")]
    [InlineData(AiFeature.CriteriaSuggestion, "Checkout works", "Functionality")]
    [InlineData(AiFeature.SeoMetadata, "Web", "Postgres")]
    public void A_form_tool_is_handed_the_sections_above_the_one_it_fills_and_nothing_below(
        AiFeature feature, string above, string below)
    {
        var (_, user) = AiPrompts.ForForm(feature, ShopForm);
        Assert.Contains(above, user);
        Assert.DoesNotContain(below, user);
    }

    [Fact]
    public void The_coach_reads_the_whole_form_because_contradictions_are_its_business()
    {
        Assert.Equal(AiFormReads.Everything, AiFormReads.Of(AiFeature.BriefCoach));
        var (_, user) = AiPrompts.ForForm(AiFeature.BriefCoach, ShopForm);
        foreach (var word in new[] { "A shop", "storefront", "Web", "Postgres", "TypeScript 5", "Checkout works", "Functionality" })
            Assert.Contains(word, user);
    }

    [Fact]
    public void The_form_travels_cleaned_and_labelled_and_never_carries_the_award_or_the_dates()
    {
        using var doc = JsonDocument.Parse(AiInputs.OpportunityForm(ShopForm, AiFormReads.Everything));
        var root = doc.RootElement;
        // Labels, not keys; the model writes sentences.
        Assert.Equal("Web development", root.GetProperty("kindOfWork").GetString());
        // Skills tidied and said once, blanks dropped.
        Assert.Equal(new[] { "React", "Postgres" },
            root.GetProperty("requiredSkills").EnumerateArray().Select(s => s.GetString()).ToArray());
        // Half-filled rows are not rows.
        Assert.Equal(1, root.GetProperty("requirements").GetArrayLength());
        Assert.Equal(1, root.GetProperty("milestones").GetArrayLength());
        Assert.Equal(1, root.GetProperty("scoringCriteria").GetArrayLength());
        Assert.Equal("a GitHub repository per entrant", root.GetProperty("handedInAs").GetString());
        Assert.True(root.GetProperty("entriesMustRunWithDockerCompose").GetBoolean());
        Assert.False(root.TryGetProperty("award", out _));
        Assert.False(root.TryGetProperty("deadline", out _));
    }

    [Fact]
    public void A_part_the_client_has_not_filled_in_is_absent_not_empty()
    {
        var bare = new OpportunityFormSnapshot("A shop", "Build me a storefront with a cart.",
            null, null, [], [], null, null, [], []);
        using var doc = JsonDocument.Parse(AiInputs.OpportunityForm(bare, AiFormReads.Everything));
        Assert.Equal(new[] { "title", "brief" },
            doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
    }

    [Fact]
    public void The_compose_tick_means_nothing_without_a_repository_to_build_from()
    {
        var upload = ShopForm with { Delivery = "upload", RequiresCompose = true };
        using var doc = JsonDocument.Parse(AiInputs.OpportunityForm(upload, FormPart.Delivery));
        Assert.False(doc.RootElement.TryGetProperty("entriesMustRunWithDockerCompose", out _));
        Assert.Equal("files uploaded on the opportunity page", doc.RootElement.GetProperty("handedInAs").GetString());
    }

    [Fact]
    public void Substance_is_the_title_and_the_brief_whatever_else_is_filled_in()
    {
        var categoryOverNothing = ShopForm with { Title = "", BriefMarkdown = "  " };
        Assert.Equal(0, AiInputs.Substance(categoryOverNothing));
        Assert.Equal("A shop".Length + "Build me a storefront with a cart.".Length, AiInputs.Substance(ShopForm));
    }

    [Theory]
    [InlineData("spam")]      // local, and about entries, not the brief
    [InlineData("digest")]    // about one entry
    [InlineData("standing")]  // about a whole board
    [InlineData("narrative")] // scheduled
    [InlineData("anything")]
    public void Nothing_else_reaches_the_form_route(string slug)
    {
        Assert.Null(AiFeatureRoutes.FromFormSlug(slug));
    }

    [Fact]
    public void A_feature_with_no_form_prompt_is_refused_rather_than_guessed_at()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => AiPrompts.ForForm(AiFeature.EntryDigest, ShopForm));
        Assert.Throws<ArgumentOutOfRangeException>(() => AiFormReads.Of(AiFeature.EntryDigest));
    }

    [Fact]
    public void Only_the_spam_scan_runs_without_a_provider()
    {
        foreach (var feature in Enum.GetValues<AiFeature>())
            Assert.Equal(feature != AiFeature.SpamFilter, AiOptions.RequiresProvider(feature));
    }

    // --------------------------------------------- what leaves the server

    [Fact]
    public void Digest_input_with_sendCode_off_carries_nothing_from_inside_the_repo()
    {
        var facts = new AiInputs.DigestFacts(
            "Opportunity", "Brief", "note", 12, DateTimeOffset.UtcNow,
            ["m1", "m2"], ["m1"], ["C#"], 40, true, true, false);
        var treePaths = new[] { "src/Program.cs", "tests/RulesTests.cs" };

        var json = AiInputs.Digest(facts, treePaths, "# secret readme", sendCode: false);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("fileTree").ValueKind);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("readme").ValueKind);
        Assert.DoesNotContain("Program.cs", json);
        Assert.DoesNotContain("secret readme", json);
        // The locally derived facts still travel — that is the fallback digest.
        Assert.Equal(40, doc.RootElement.GetProperty("fileCount").GetInt32());
        Assert.True(doc.RootElement.GetProperty("hasTests").GetBoolean());
    }

    [Fact]
    public void Digest_input_with_sendCode_on_carries_structure_and_readme_capped()
    {
        var facts = new AiInputs.DigestFacts(
            "Opportunity", "Brief", "", 12, null, [], [], [], 1, false, true, false);
        var manyPaths = Enumerable.Range(0, 1000).Select(i => $"src/f{i}.cs").ToList();

        var json = AiInputs.Digest(facts, manyPaths, new string('r', 5000), sendCode: true);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(AiInputs.MaxTreePaths, doc.RootElement.GetProperty("fileTree").GetArrayLength());
        Assert.True(doc.RootElement.GetProperty("readme").GetString()!.Length
            <= AiInputs.MaxReadmeChars + "[…clipped]".Length + 1);
    }

    [Fact]
    public void TreeFacts_reads_tests_readme_and_ci_from_paths_alone()
    {
        var (count, hasTests, hasReadme, hasCi, hasCompose) = AiInputs.TreeFacts(
            ["README.md", "src/app.py", "tests/test_app.py", ".github/workflows/ci.yml", "compose.yaml"]);
        Assert.Equal((5, true, true, true, true), (count, hasTests, hasReadme, hasCi, hasCompose));

        var bare = AiInputs.TreeFacts(["main.go"]);
        Assert.Equal((1, false, false, false, false), bare);
    }

    [Theory]
    [InlineData("compose.yaml", true)]
    [InlineData("compose.yml", true)]
    [InlineData("docker-compose.yml", true)]
    [InlineData("Docker-Compose.yaml", true)]
    [InlineData("deploy/compose.yaml", false)]   // not at the root: Compose would not find it either
    [InlineData("compose.override.yml", false)]
    [InlineData("Dockerfile", false)]
    public void TreeFacts_counts_a_compose_file_only_at_the_root_under_its_four_names(string path, bool expected)
    {
        Assert.Equal(expected, AiInputs.TreeFacts([path]).HasCompose);
    }

    // ---------------------------------------------------------- spam scan

    [Theory]
    [InlineData("README.md", false)]
    [InlineData("docs/readme.txt", false)]
    [InlineData("LICENSE", false)]
    [InlineData(".gitignore", false)]
    [InlineData("src/app.ts", true)]
    public void Scaffolding_does_not_count_as_work(string path, bool meaningful)
    {
        Assert.Equal(meaningful, SpamRules.IsMeaningful(path));
    }

    [Fact]
    public void Jaccard_is_overlap_over_union()
    {
        Assert.Equal(1.0, SpamRules.Jaccard(["a", "b"], ["a", "b"]));
        Assert.Equal(0.0, SpamRules.Jaccard(["a"], ["b"]));
        Assert.Equal(1 / 3.0, SpamRules.Jaccard(["a", "b"], ["b", "c"]), precision: 10);
        Assert.Equal(0.0, SpamRules.Jaccard([], []));
    }

    [Fact]
    public void Scan_flags_empty_and_readme_only_entries()
    {
        var flags = SpamRules.Scan(
        [
            new(Guid.NewGuid(), "Idle", PushCount: 0, TreePaths: ["README.md"]),
            new(Guid.NewGuid(), "Scaffold", PushCount: 3, TreePaths: ["README.md", "LICENSE"]),
            new(Guid.NewGuid(), "Builder", PushCount: 9, TreePaths: ["README.md", "src/app.ts"]),
        ]);
        Assert.Equal(2, flags.Count);
        Assert.Contains(flags, f => f.Entrant == "Idle" && f.Reasons.Single().Contains("No pushes"));
        Assert.Contains(flags, f => f.Entrant == "Scaffold" && f.Reasons.Single().Contains("scaffolding"));
    }

    [Fact]
    public void Scan_flags_near_identical_file_layouts_both_ways()
    {
        string[] work = ["src/a.ts", "src/b.ts", "src/c.ts", "src/d.ts", "src/e.ts", "src/f.ts"];
        var flags = SpamRules.Scan(
        [
            new(Guid.NewGuid(), "Original", 20, ["README.md", .. work]),
            new(Guid.NewGuid(), "Copyist", 2, [.. work]),
        ]);
        Assert.Equal(2, flags.Count);
        Assert.All(flags, f => Assert.Contains(f.Reasons, r => r.Contains("identical")));
    }

    [Fact]
    public void Scan_never_calls_two_seeded_repos_duplicates()
    {
        // Every entry starts from the same seeded repository, so small
        // identical trees prove nothing.
        var flags = SpamRules.Scan(
        [
            new(Guid.NewGuid(), "A", 5, ["README.md", "src/app.ts"]),
            new(Guid.NewGuid(), "B", 5, ["README.md", "src/app.ts"]),
        ]);
        Assert.Empty(flags);
    }

    [Fact]
    public void Scan_without_visible_repos_still_catches_the_empty_ones()
    {
        var flags = SpamRules.Scan(
        [
            new(Guid.NewGuid(), "Ghost", PushCount: 0, TreePaths: null),
            new(Guid.NewGuid(), "Worker", PushCount: 4, TreePaths: null),
        ]);
        Assert.Single(flags);
        Assert.Equal("Ghost", flags[0].Entrant);
    }

    // ----------------------------------------------------- output gating

    [Fact]
    public void Milestone_output_is_validated_and_canonicalised()
    {
        var canonical = AiOutputs.Validate(AiFeature.MilestoneExtraction,
            "```json\n{\"milestones\":[{\"title\":\"Set up auth\",\"description\":\"Login works\"}," +
            "{\"title\":\"\"},{\"title\":\"Ship it\"}]}\n```", out var error);
        Assert.Null(error);
        using var doc = JsonDocument.Parse(canonical!);
        var items = doc.RootElement.GetProperty("milestones");
        Assert.Equal(2, items.GetArrayLength()); // the untitled one dropped
        Assert.Equal(JsonValueKind.Null, items[1].GetProperty("description").ValueKind);
    }

    [Fact]
    public void Milestone_output_with_nothing_usable_is_an_error()
    {
        Assert.Null(AiOutputs.Validate(AiFeature.MilestoneExtraction, "{\"milestones\":[]}", out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Coach_output_coerces_severity_and_allows_a_clean_bill()
    {
        var canonical = AiOutputs.Validate(AiFeature.BriefCoach,
            "{\"flags\":[{\"severity\":\"catastrophic\",\"issue\":\"No definition of done\"}]}", out _);
        using (var doc = JsonDocument.Parse(canonical!))
            Assert.Equal("info", doc.RootElement.GetProperty("flags")[0].GetProperty("severity").GetString());

        // Zero flags is a legitimate verdict, not a failure.
        var clean = AiOutputs.Validate(AiFeature.BriefCoach, "{\"flags\":[]}", out var error);
        Assert.Null(error);
        Assert.Equal("{\"flags\":[]}", clean);
    }

    [Fact]
    public void Digest_output_requires_a_summary()
    {
        Assert.Null(AiOutputs.Validate(AiFeature.EntryDigest, "{\"stack\":[\"C#\"]}", out var error));
        Assert.NotNull(error);

        var ok = AiOutputs.Validate(AiFeature.EntryDigest,
            "{\"summary\":\"A CRUD app.\",\"stack\":[\"C#\",42],\"reviewFocus\":[\"src/Program.cs\"]}", out _);
        using var doc = JsonDocument.Parse(ok!);
        Assert.Equal(1, doc.RootElement.GetProperty("stack").GetArrayLength()); // non-strings dropped
    }

    [Fact]
    public void Seo_output_needs_both_lines_and_stays_head_sized()
    {
        Assert.Null(AiOutputs.Validate(AiFeature.SeoMetadata, "{\"title\":\"only one\"}", out _));

        var ok = AiOutputs.Validate(AiFeature.SeoMetadata,
            $"{{\"title\":\"{new string('t', 300)}\",\"description\":\"d\"}}", out _);
        using var doc = JsonDocument.Parse(ok!);
        // A hard cut with no marker — the draft lands verbatim in an 80-char field.
        Assert.Equal(80, doc.RootElement.GetProperty("title").GetString()!.Length);
    }

    [Fact]
    public void Requirements_output_keeps_whole_rows_said_once_and_cut_to_the_table()
    {
        var canonical = AiOutputs.Validate(AiFeature.RequirementsSuggestion,
            "{\"requirements\":[{\"title\":\"Language\",\"detail\":\"Python 3.11+\"}," +
            "{\"title\":\"language\",\"detail\":\"said again\"}," +
            "{\"title\":\"Half\"}," +
            $"{{\"title\":\"Data\",\"detail\":\"{new string('d', 400)}\"}}]}}", out var error);
        Assert.Null(error);
        using var doc = JsonDocument.Parse(canonical!);
        var rows = doc.RootElement.GetProperty("requirements");
        Assert.Equal(2, rows.GetArrayLength()); // the repeat and the half-row dropped
        Assert.Equal("Language", rows[0].GetProperty("title").GetString());
        // A hard cut with no marker — the row lands in the table verbatim.
        Assert.Equal(240, rows[1].GetProperty("detail").GetString()!.Length);

        Assert.Null(AiOutputs.Validate(AiFeature.RequirementsSuggestion, "{\"requirements\":[{\"title\":\"x\"}]}", out var none));
        Assert.NotNull(none);
    }

    [Fact]
    public void Criteria_output_keeps_titled_pointed_lines_and_adds_up_to_a_hundred()
    {
        var canonical = AiOutputs.Validate(AiFeature.CriteriaSuggestion,
            "{\"criteria\":[{\"title\":\"Functionality\",\"points\":45,\"description\":\"Works\"}," +
            "{\"title\":\"Craft\",\"points\":30}," +
            "{\"title\":\"Docs\",\"points\":15.4}," +
            "{\"title\":\"Nothing\",\"points\":0}," +
            "{\"points\":10}]}", out var error);
        Assert.Null(error);
        using var doc = JsonDocument.Parse(canonical!);
        var rows = doc.RootElement.GetProperty("criteria").EnumerateArray().ToArray();
        Assert.Equal(3, rows.Length); // no points and no title are not lines
        // 45 + 30 + 15 = 90, rescaled to a hundred in the same proportions.
        Assert.Equal(100, rows.Sum(r => r.GetProperty("points").GetInt32()));
        Assert.Equal(50, rows[0].GetProperty("points").GetInt32());
        Assert.Equal(JsonValueKind.Null, rows[1].GetProperty("description").ValueKind);

        Assert.Null(AiOutputs.Validate(AiFeature.CriteriaSuggestion, "{\"criteria\":[]}", out var none));
        Assert.NotNull(none);
    }

    [Theory]
    [InlineData(new[] { 35, 25, 20, 10, 10 }, new[] { 35, 25, 20, 10, 10 })] // already a hundred: untouched
    [InlineData(new[] { 45, 30, 15 }, new[] { 50, 33, 17 })]                  // 90 up to 100, the remainder to the largest fractions
    [InlineData(new[] { 1, 1, 1 }, new[] { 34, 33, 33 })]                     // equal shares, remainder to the first
    [InlineData(new[] { 1000, 1, 1, 1 }, new[] { 97, 1, 1, 1 })]              // no line rounded down to nothing
    [InlineData(new[] { 60, 60 }, new[] { 50, 50 })]                          // 120 down to 100
    public void Points_are_rescaled_to_a_hundred_in_whole_numbers(int[] given, int[] expected)
    {
        Assert.Equal(expected, AiOutputs.ToHundred(given));
    }

    [Fact]
    public void Garbage_answers_fail_with_words_a_person_can_read()
    {
        Assert.Null(AiOutputs.Validate(AiFeature.ProgressNarrative, "I refuse.", out var error));
        Assert.Contains("no JSON", error);
        Assert.Null(AiOutputs.Validate(AiFeature.ProgressNarrative, "{broken}", out error));
        Assert.Contains("not valid JSON", error);
    }

    // ---------------------------------------------------- provider wire

    [Fact]
    public void Gemini_requests_carry_the_key_in_a_header_never_the_url()
    {
        var request = AiProviderRequests.Build("gemini", "gemini-3.6-flash", "sk-secret", "sys", "user",
            AiOutputs.Schema(AiFeature.SeoMetadata));
        Assert.Equal(
            "https://generativelanguage.googleapis.com/v1beta/models/gemini-3.6-flash:generateContent",
            request.Url);
        Assert.Equal("sk-secret", request.Headers["x-goog-api-key"]);
        Assert.DoesNotContain("sk-secret", request.Url);
        using var doc = JsonDocument.Parse(request.BodyJson);
        Assert.Equal("sys", doc.RootElement.GetProperty("system_instruction")
            .GetProperty("parts")[0].GetProperty("text").GetString());
        var config = doc.RootElement.GetProperty("generationConfig");
        Assert.Equal("application/json", config.GetProperty("responseMimeType").GetString());
        // The answer's shape rides in the request, and the budget is set —
        // there was none before, so a runaway answer had no stop.
        Assert.Equal("object", config.GetProperty("responseJsonSchema").GetProperty("type").GetString());
        Assert.True(config.GetProperty("responseJsonSchema").GetProperty("properties").TryGetProperty("title", out _));
        Assert.Equal(AiProviderRequests.AnswerTokens, config.GetProperty("maxOutputTokens").GetInt32());
    }

    [Fact]
    public void Anthropic_requests_carry_the_version_header_and_system_prompt()
    {
        var request = AiProviderRequests.Build("anthropic", "claude-sonnet-5", "sk-ant", "sys", "user",
            AiOutputs.Schema(AiFeature.SeoMetadata));
        Assert.Equal("https://api.anthropic.com/v1/messages", request.Url);
        Assert.Equal("sk-ant", request.Headers["x-api-key"]);
        Assert.Equal("2023-06-01", request.Headers["anthropic-version"]);
        using var doc = JsonDocument.Parse(request.BodyJson);
        Assert.Equal("claude-sonnet-5", doc.RootElement.GetProperty("model").GetString());
        Assert.Equal("sys", doc.RootElement.GetProperty("system").GetString());
        Assert.Equal("user", doc.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
        // The current Claude models answer 400 to any non-default sampling
        // parameter, so the body must carry none — and the budget has to
        // cover the model's own thinking as well as a forty-row draft.
        Assert.False(doc.RootElement.TryGetProperty("temperature", out _));
        Assert.False(doc.RootElement.TryGetProperty("top_p", out _));
        Assert.True(doc.RootElement.GetProperty("max_tokens").GetInt32() >= 8192);
        // The shape travels as the native structured output — no tool, no beta header.
        var format = doc.RootElement.GetProperty("output_config").GetProperty("format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.Equal("object", format.GetProperty("schema").GetProperty("type").GetString());
        Assert.False(request.Headers.ContainsKey("anthropic-beta"));
    }

    // ------------------------------------------------------ standing notes

    [Fact]
    public void Standing_notes_keep_only_rows_with_a_real_entry_id_and_a_note()
    {
        var real = Guid.NewGuid();
        var canonical = AiOutputs.Validate(AiFeature.StandingNotes,
            "{\"summary\":\"Two rows moving, one quiet.\",\"entrants\":[" +
            $"{{\"entryId\":\"{real}\",\"note\":\"Claimed m1 on time; nothing since.\",\"check\":[\"m2\",\"last push\",\"files\",\"extra\"]}}," +
            "{\"entryId\":\"not-a-guid\",\"note\":\"Invented row.\"}," +
            $"{{\"entryId\":\"{Guid.NewGuid()}\",\"note\":\"\"}}]}}", out var error);
        Assert.Null(error);
        using var doc = JsonDocument.Parse(canonical!);
        var entrants = doc.RootElement.GetProperty("entrants");
        Assert.Equal(1, entrants.GetArrayLength());
        Assert.Equal(real, entrants[0].GetProperty("entryId").GetGuid());
        Assert.Equal(3, entrants[0].GetProperty("check").GetArrayLength()); // capped at three
        Assert.Equal("Two rows moving, one quiet.", doc.RootElement.GetProperty("summary").GetString());
    }

    [Fact]
    public void Standing_notes_with_nothing_readable_are_an_error()
    {
        Assert.Null(AiOutputs.Validate(AiFeature.StandingNotes, "{\"entrants\":[{\"entryId\":\"x\"}]}", out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Standing_input_is_ranked_and_capped_and_never_carries_code()
    {
        var entrants = Enumerable.Range(1, 50).Select(i => new AiInputs.StandingEntrant(
            Guid.NewGuid(), $"Entrant {i}", 51 - i, 50, 100 - i, "on_track",
            DateTimeOffset.UtcNow, null, ["on_time"],
            [new AiInputs.StandingPart("Milestones on time", 20, 20, "1 of 1 met on time.")],
            null)).ToList();

        var (json, _) = AiInputs.Standing("Opportunity", "open", "repository", null, [], entrants, DateTimeOffset.UtcNow);

        using var doc = JsonDocument.Parse(json);
        var rows = doc.RootElement.GetProperty("entrants");
        Assert.Equal(AiInputs.MaxStandingEntrants, rows.GetArrayLength());
        Assert.Equal(1, rows[0].GetProperty("rank").GetInt32()); // best first, whatever order they arrived in
        Assert.Equal(10, doc.RootElement.GetProperty("entrantsOmitted").GetInt32());
        Assert.DoesNotContain("fileTree", json);
        Assert.DoesNotContain("readme", json);
    }

    [Fact]
    public void Openai_requests_carry_a_bearer_header_json_mode_and_no_sampling()
    {
        var request = AiProviderRequests.Build("openai", "gpt-5.5", "sk-proj-secret", "sys", "user",
            AiOutputs.Schema(AiFeature.SeoMetadata));
        Assert.Equal("https://api.openai.com/v1/chat/completions", request.Url);
        Assert.Equal("Bearer sk-proj-secret", request.Headers["Authorization"]);
        Assert.DoesNotContain("sk-proj-secret", request.Url);
        using var doc = JsonDocument.Parse(request.BodyJson);
        Assert.Equal("gpt-5.5", doc.RootElement.GetProperty("model").GetString());
        var messages = doc.RootElement.GetProperty("messages");
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("sys", messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("content").GetString());
        // Strict schema mode: the shape is enforced by the provider, not asked for.
        var format = doc.RootElement.GetProperty("response_format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.True(format.GetProperty("json_schema").GetProperty("strict").GetBoolean());
        Assert.Equal("object", format.GetProperty("json_schema").GetProperty("schema").GetProperty("type").GetString());
        // The gpt-5 family answers 400 to a temperature and to max_tokens;
        // the budget travels under the newer name.
        Assert.False(doc.RootElement.TryGetProperty("temperature", out _));
        Assert.False(doc.RootElement.TryGetProperty("max_tokens", out _));
        Assert.True(doc.RootElement.GetProperty("max_completion_tokens").GetInt32() >= 8192);
    }

    [Fact]
    public void Json_mode_is_honest_because_the_house_rules_ask_for_json_by_name()
    {
        // OpenAI refuses response_format json_object on a prompt that never
        // says "JSON"; every system prompt here starts with the house rules.
        Assert.Contains("JSON", AiPrompts.HouseRules);
    }

    [Fact]
    public void The_settings_test_asks_for_json_by_name_too()
    {
        // It skips the house rules, so it must say the word itself: the
        // Test button on an OpenAI setup answered 400 while it asked for "OK".
        var (system, _) = AiPrompts.Ping;
        Assert.Contains("JSON", system);
    }

    [Theory]
    [InlineData("gemini", "gemini-3.6-flash")]
    [InlineData("anthropic", "claude-sonnet-5-5")]
    [InlineData("openai", "gpt-5.5")]
    public void A_blank_model_falls_back_to_the_provider_default(string provider, string expected)
    {
        Assert.Equal(expected, AiProviderRequests.DefaultModel(provider));
    }

    [Fact]
    public void The_dropdown_and_the_wire_format_agree_on_which_providers_exist()
    {
        foreach (var (key, _) in AiProviders.Choices)
        {
            Assert.NotNull(AiProviders.Find(key));
            Assert.Equal(AiProviders.Find(key)!.DefaultModel, AiProviderRequests.DefaultModel(key));
            Assert.NotNull(AiProviderRequests.Build(key, "m", "k", "s", "u", AiOutputs.PingSchema).Url);
        }
        Assert.Equal(3, AiProviders.Choices.Count);
    }

    [Fact]
    public void An_unknown_provider_is_a_named_error_not_a_mystery()
    {
        var e = Assert.Throws<AiProviderException>(() => AiProviderRequests.DefaultModel("cohere"));
        Assert.Contains("cohere", e.Message);
        Assert.Contains("\"openai\"", e.Message); // the line names what is supported
        Assert.Throws<AiProviderException>(() => AiProviderRequests.Build("cohere", "m", "k", "s", "u", AiOutputs.PingSchema));
    }

    [Fact]
    public void Each_providers_answer_shape_yields_the_text()
    {
        Assert.Equal("hello", AiProviderRequests.ExtractText("gemini",
            "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"hello\"}]}}]}"));
        Assert.Equal("hello", AiProviderRequests.ExtractText("anthropic",
            "{\"content\":[{\"type\":\"text\",\"text\":\"hello\"}]}"));
        Assert.Equal("hello", AiProviderRequests.ExtractText("openai",
            "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"hello\"}}]}"));
    }

    [Fact]
    public void An_empty_completion_is_a_named_error()
    {
        Assert.Throws<AiProviderException>(() =>
            AiProviderRequests.ExtractText("gemini", "{\"candidates\":[]}"));
        // An OpenAI refusal arrives with content null and a refusal beside it.
        Assert.Throws<AiProviderException>(() =>
            AiProviderRequests.ExtractText("openai",
                "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":null,\"refusal\":\"no\"}}]}"));
    }

    [Theory]
    [InlineData("gemini", "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"{\\\"title\\\":\\\"cut\"}]},\"finishReason\":\"MAX_TOKENS\"}]}", AiFailure.Truncated)]
    [InlineData("gemini", "{\"candidates\":[{\"content\":{\"parts\":[]},\"finishReason\":\"SAFETY\"}]}", AiFailure.Safety)]
    [InlineData("gemini", "{\"promptFeedback\":{\"blockReason\":\"PROHIBITED_CONTENT\"}}", AiFailure.Safety)]
    [InlineData("anthropic", "{\"content\":[{\"type\":\"text\",\"text\":\"{\\\"title\\\":\\\"cut\"}],\"stop_reason\":\"max_tokens\"}", AiFailure.Truncated)]
    [InlineData("anthropic", "{\"content\":[],\"stop_reason\":\"refusal\"}", AiFailure.Refused)]
    [InlineData("openai", "{\"choices\":[{\"message\":{\"content\":\"{\\\"title\\\":\\\"cut\"},\"finish_reason\":\"length\"}]}", AiFailure.Truncated)]
    [InlineData("openai", "{\"choices\":[{\"message\":{\"content\":null},\"finish_reason\":\"content_filter\"}]}", AiFailure.Safety)]
    [InlineData("openai", "{\"choices\":[{\"message\":{\"content\":null,\"refusal\":\"I can't help with that.\"},\"finish_reason\":\"stop\"}]}", AiFailure.Refused)]
    [InlineData("gemini", "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"\"}]},\"finishReason\":\"STOP\"}]}", AiFailure.Empty)]
    public void A_200_that_carries_no_answer_is_the_named_failure_it_is(string provider, string body, AiFailure expected)
    {
        // A cut-off answer is refused whole rather than parsed as far as it
        // got: the text above is partial JSON that ExtractJson would happily
        // hand to the validator.
        var e = Assert.Throws<AiProviderException>(() => AiProviderRequests.ExtractText(provider, body));
        Assert.Equal(expected, e.Failure);
        Assert.Equal(0, e.StatusCode);
    }

    [Fact]
    public void Text_blocks_are_joined_and_a_thinking_models_reasoning_is_not_text()
    {
        Assert.Equal("{\"a\":1}", AiProviderRequests.ExtractText("anthropic",
            "{\"content\":[{\"type\":\"thinking\",\"thinking\":\"hmm\"},{\"type\":\"text\",\"text\":\"{\\\"a\\\"\"},{\"type\":\"text\",\"text\":\":1}\"}],\"stop_reason\":\"end_turn\"}"));
        Assert.Equal("{\"a\":1}", AiProviderRequests.ExtractText("gemini",
            "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"planning…\",\"thought\":true},{\"text\":\"{\\\"a\\\"\"},{\"text\":\":1}\"}]},\"finishReason\":\"STOP\"}]}"));
    }

    [Fact]
    public void A_named_failure_reaches_a_person_as_a_line_they_can_act_on_and_decides_the_retry()
    {
        var cut = new AiProviderException(0, "finishReason MAX_TOKENS", AiFailure.Truncated);
        Assert.Contains("cut short", cut.Friendly);
        Assert.DoesNotContain("MAX_TOKENS", cut.Friendly);
        Assert.True(AiRules.Retryable(cut)); // worth another go

        var declined = new AiProviderException(0, "stop_reason refusal", AiFailure.Refused);
        Assert.Contains("declined", declined.Friendly);
        Assert.False(AiRules.Retryable(declined)); // the same input gets the same answer
        Assert.False(AiRules.Retryable(new AiProviderException(0, "SAFETY", AiFailure.Safety)));
        Assert.True(AiRules.Retryable(new AiProviderException(503, "The provider answered 503.")));
        Assert.Equal(AiProviderRequests.Unavailable(0), new AiProviderException(0, "empty", AiFailure.Empty).Friendly);

        // The two the pipeline names: a call that timed out may have run and is not given back; a paused one never left.
        var late = new AiProviderException(0, "No attempt answered", AiFailure.Timeout);
        Assert.Contains("did not answer in time", late.Friendly);
        Assert.True(AiRules.Retryable(late));
        Assert.False(AiRules.NothingRan(late));
        var paused = new AiProviderException(0, "Nothing was sent", AiFailure.Paused);
        Assert.Contains("short rest", paused.Friendly);
        Assert.True(AiRules.Retryable(paused));
        Assert.True(AiRules.NothingRan(paused));
        Assert.True(AiRules.NothingRan(new AiProviderException(429, "quota")));
        Assert.True(AiRules.NothingRan(new AiProviderException(502, "bad gateway")));
        Assert.False(AiRules.NothingRan(new AiProviderException(401, "key")));
    }

    [Fact]
    public void Only_a_failure_the_next_attempt_may_not_meet_earns_one()
    {
        // A bad minute, a lost connection, a cut-off or empty answer: yes.
        Assert.True(AiRules.Retryable(new AiProviderException(429, "quota")));
        Assert.True(AiRules.Retryable(new HttpRequestException("no route")));
        Assert.True(AiRules.Retryable(new TaskCanceledException()));
        Assert.True(AiRules.Retryable(new AiProviderException(0, "empty", AiFailure.Empty)));
        // The request's own fault, a wrong shape, a subject that is gone: no.
        Assert.False(AiRules.Retryable(new AiProviderException(401, "key")));
        Assert.False(AiRules.Retryable(new AiProviderException(404, "model")));
        Assert.False(AiRules.Retryable(new AiProviderException(400, "schema")));
        Assert.False(AiRules.Retryable(new InvalidOperationException("The answer failed validation.")));
    }

    // ---------------------------------------------------- answer schemas

    [Fact]
    public void Every_provider_feature_has_a_schema_closed_at_every_level()
    {
        // The dialect all three providers' structured output accepts: every
        // object says additionalProperties false and requires every property
        // it names; nothing a provider lacks (lengths, counts, formats).
        foreach (var feature in Enum.GetValues<AiFeature>().Where(AiOptions.RequiresProvider))
        {
            var json = JsonSerializer.Serialize(AiOutputs.Schema(feature));
            using var doc = JsonDocument.Parse(json);
            Assert.Equal("object", doc.RootElement.GetProperty("type").GetString());
            AssertClosed(doc.RootElement, feature.ToString());
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => AiOutputs.Schema(AiFeature.SpamFilter));

        static void AssertClosed(JsonElement node, string path)
        {
            if (node.ValueKind != JsonValueKind.Object) return;
            foreach (var p in node.EnumerateObject())
                Assert.DoesNotContain(p.Name, new[] { "minItems", "maxItems", "minLength", "maxLength", "format", "pattern", "minimum", "maximum" });
            var isObject = node.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String && t.GetString() == "object";
            if (isObject)
            {
                Assert.True(node.TryGetProperty("additionalProperties", out var extra) && extra.ValueKind == JsonValueKind.False, path + " is open");
                var names = node.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToHashSet();
                var required = node.GetProperty("required").EnumerateArray().Select(r => r.GetString()!).ToHashSet();
                Assert.True(names.SetEquals(required), path + " leaves a property optional");
                foreach (var p in node.GetProperty("properties").EnumerateObject())
                    AssertClosed(p.Value, path + "." + p.Name);
            }
            if (node.TryGetProperty("items", out var items)) AssertClosed(items, path + "[]");
        }
    }

    [Fact]
    public void The_schemas_say_what_the_validators_read()
    {
        using var milestones = JsonDocument.Parse(JsonSerializer.Serialize(AiOutputs.Schema(AiFeature.MilestoneExtraction)));
        var item = milestones.RootElement.GetProperty("properties").GetProperty("milestones").GetProperty("items").GetProperty("properties");
        Assert.Equal(new[] { "number", "null" }, item.GetProperty("amount").GetProperty("type").EnumerateArray().Select(t => t.GetString()));

        // A closed list is an enum, so a key cannot be invented.
        using var categorise = JsonDocument.Parse(JsonSerializer.Serialize(AiOutputs.Schema(AiFeature.CategorySuggestion)));
        var keys = categorise.RootElement.GetProperty("properties").GetProperty("category").GetProperty("enum")
            .EnumerateArray().Select(k => k.GetString()!).ToHashSet();
        Assert.True(keys.SetEquals(OpportunityCategories.Keys));

        using var coach = JsonDocument.Parse(JsonSerializer.Serialize(AiOutputs.Schema(AiFeature.BriefCoach)));
        var severity = coach.RootElement.GetProperty("properties").GetProperty("flags").GetProperty("items")
            .GetProperty("properties").GetProperty("severity").GetProperty("enum").EnumerateArray().Select(s => s.GetString());
        Assert.Equal(new[] { "info", "warn" }, severity);

        using var ping = JsonDocument.Parse(JsonSerializer.Serialize(AiOutputs.PingSchema));
        Assert.Equal("boolean", ping.RootElement.GetProperty("properties").GetProperty("ok").GetProperty("type").GetString());
    }

    [Fact]
    public void Provider_error_bodies_surface_their_message()
    {
        Assert.Equal("The provider answered 400: API key not valid.",
            AiProviderRequests.ErrorDetail(400, "{\"error\":{\"message\":\"API key not valid.\"}}"));
        Assert.Equal("The provider answered 502.",
            AiProviderRequests.ErrorDetail(502, "<html>bad gateway</html>"));
    }

    [Theory]
    [InlineData(429, "quota")]
    [InlineData(503, "not available")]
    [InlineData(401, "key")]
    [InlineData(403, "key")]
    [InlineData(404, "model")]
    [InlineData(400, "try again")]
    [InlineData(0, "try again")]
    public void A_refusal_reaches_a_person_as_a_line_they_can_act_on(int status, string says)
    {
        var e = new AiProviderException(status,
            "The provider answered " + status + ": You exceeded your current quota, please check your plan and billing details.");
        Assert.Contains(says, e.Friendly);
        // Never the provider's words, never its status code.
        Assert.DoesNotContain("billing", e.Friendly);
        Assert.DoesNotContain("provider answered", e.Friendly);
        if (status > 0) Assert.DoesNotContain(status.ToString(), e.Friendly);
    }

    [Fact]
    public void A_failed_draft_notes_the_provider_in_a_persons_words_and_the_portal_in_its_own()
    {
        Assert.Equal(AiProviderRequests.Unavailable(429),
            AiRules.FailureNote(new AiProviderException(429, "The provider answered 429: quota exceeded")));
        Assert.Contains("could not be reached",
            AiRules.FailureNote(new HttpRequestException("No such host is known.")));
        Assert.Equal("That profile is gone.",
            AiRules.FailureNote(new InvalidOperationException("That profile is gone.")));
    }

    // ------------------------------------------------------------ prompts

    [Fact]
    public void Every_prompt_forbids_judging_and_demands_json()
    {
        var prompts = new[]
        {
            AiPrompts.Milestones("{}"),
            AiPrompts.Coach("{}"),
            AiPrompts.Requirements("{}"),
            AiPrompts.Criteria("{}"),
            AiPrompts.Digest("{}"),
            AiPrompts.Narrative("{}"),
            AiPrompts.Seo("{}"),
            AiPrompts.Standing("{}"),
            AiPrompts.Categorise("[]", "{}"),
            AiPrompts.WorkKinds("[]", "{}"),
            AiPrompts.ProjectApproach("{}"),
            AiPrompts.ApplicationEvaluation("{}"),
        };
        foreach (var (system, _) in prompts)
        {
            Assert.Contains("never judge, score, rank", system);
            Assert.Contains("JSON", system);
        }
    }


    // ------------------------------- the two readings of the taxonomy

    [Fact]
    public void A_category_suggestion_is_kept_only_inside_the_portal_taxonomy()
    {
        var canonical = AiOutputs.Validate(AiFeature.CategorySuggestion,
            "{\"category\":\"WEB\",\"subcategory\":\"frontend\",\"because\":\"a React dashboard\",\"confident\":true}",
            out var error);
        Assert.Null(error);
        using var doc = JsonDocument.Parse(canonical!);
        Assert.Equal("web", doc.RootElement.GetProperty("category").GetString());
        Assert.Equal("frontend", doc.RootElement.GetProperty("subcategory").GetString());
        Assert.Equal("a React dashboard", doc.RootElement.GetProperty("because").GetString());
        Assert.True(doc.RootElement.GetProperty("confident").GetBoolean());
    }

    [Fact]
    public void An_invented_category_is_refused_rather_than_mapped_to_the_nearest()
    {
        Assert.Null(AiOutputs.Validate(AiFeature.CategorySuggestion,
            "{\"category\":\"machine-learning\"}", out var error));
        Assert.Contains("does not have", error);
    }

    [Fact]
    public void A_subcategory_under_the_wrong_parent_is_dropped_and_the_category_kept()
    {
        var canonical = AiOutputs.Validate(AiFeature.CategorySuggestion,
            "{\"category\":\"web\",\"subcategory\":\"android\",\"confident\":false}", out var error);
        Assert.Null(error);
        using var doc = JsonDocument.Parse(canonical!);
        Assert.Equal("web", doc.RootElement.GetProperty("category").GetString());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("subcategory").ValueKind);
        // A model that says it is unsure is believed; the form says so too.
        Assert.False(doc.RootElement.GetProperty("confident").GetBoolean());
    }

    [Fact]
    public void A_work_reading_drops_what_the_portal_does_not_have_and_keeps_the_rest()
    {
        var canonical = AiOutputs.Validate(AiFeature.RecommendedMatching,
            "{\"categories\":[\"web\",\"Design\",\"web\",\"cobol\"]}", out var error);
        Assert.Null(error);
        using var doc = JsonDocument.Parse(canonical!);
        var keys = doc.RootElement.GetProperty("categories").EnumerateArray()
            .Select(v => v.GetString()).ToList();
        Assert.Equal(["web", "design"], keys);
    }

    [Fact]
    public void A_profile_that_covers_nothing_reads_as_an_empty_list_not_a_failure()
    {
        var canonical = AiOutputs.Validate(AiFeature.RecommendedMatching,
            "{\"categories\":[]}", out var error);
        Assert.Null(error);
        Assert.Equal("{\"categories\":[]}", canonical);

        // No list at all is a different thing: that is a broken answer.
        Assert.Null(AiOutputs.Validate(AiFeature.RecommendedMatching,
            "{\"kinds\":[\"web\"]}", out var missing));
        Assert.NotNull(missing);
    }

    [Fact]
    public void The_taxonomy_travels_with_both_readings_so_a_new_category_expires_the_cache()
    {
        var taxonomy = AiInputs.Taxonomy();
        Assert.Contains("frontend", taxonomy);
        Assert.Contains("Web development", taxonomy);
        // Keys and labels, and nothing about anybody.
        Assert.DoesNotContain("react", taxonomy);

        var (_, categoriseUser) = AiPrompts.Categorise(taxonomy, "{}");
        Assert.Contains(taxonomy, categoriseUser);
        var (_, workUser) = AiPrompts.WorkKinds(taxonomy, "{}");
        Assert.Contains(taxonomy, workUser);
    }

    [Fact]
    public void A_work_reading_carries_the_skills_and_nothing_that_could_rank_anybody()
    {
        var json = AiInputs.WorkKinds("Django specialist", [("React", "Expert", 6), ("Figma", "Working", 2)]);
        Assert.Contains("Django specialist", json);
        Assert.Contains("React", json);
        Assert.Contains("Expert", json);
        // Not their merit score, not their record, not their name.
        Assert.DoesNotContain("merit", json, StringComparison.OrdinalIgnoreCase);

        var blank = AiInputs.WorkKinds("   ", []);
        Assert.Contains("\"headline\":null", blank);
    }




    [Fact]
    public void The_inline_cache_answers_the_same_form_within_the_hour_and_forgets_it_after()
    {
        var cache = new AiInlineCache();
        var me = Guid.NewGuid();
        var t = DateTimeOffset.Parse("2026-10-01T10:00:00Z");
        cache.Put(me, AiFeature.CategorySuggestion, "h1", "{\"category\":\"design\"}", t);

        Assert.Equal("{\"category\":\"design\"}", cache.Get(me, AiFeature.CategorySuggestion, "h1", t.AddMinutes(59)));
        Assert.Null(cache.Get(me, AiFeature.CategorySuggestion, "h1", t.AddMinutes(60)));
        Assert.Null(cache.Get(me, AiFeature.CategorySuggestion, "h2", t)); // a changed form
        Assert.Null(cache.Get(me, AiFeature.BriefCoach, "h1", t));         // another tool
        Assert.Null(cache.Get(Guid.NewGuid(), AiFeature.CategorySuggestion, "h1", t)); // another member
    }

    [Fact]
    public void The_inline_cache_stays_bounded_dropping_the_expired_then_the_oldest()
    {
        var cache = new AiInlineCache();
        var t = DateTimeOffset.Parse("2026-10-01T10:00:00Z");
        for (var i = 0; i < AiInlineCache.MaxEntries + 1; i++)
            cache.Put(Guid.NewGuid(), AiFeature.SeoMetadata, "h", "{}", t.AddSeconds(i));
        Assert.Equal(AiInlineCache.MaxEntries, cache.Count);
    }

    // ------------------------------------------- the profile summary

    private static ProfileSummaryRequest SomeProfile() => new(
        DisplayName: "Anwar", Headline: "Django specialist — reporting, invoicing, PDFs",
        Bio: null, PrimaryCategory: "web", SecondaryCategories: ["data"], WorkType: "parttime",
        Location: "Karachi", YearsExperience: 7, HoursPerWeek: 20,
        Skills: [new("Django", 6, "expert"), new("PostgreSQL", 5, "intermediate")],
        Languages: [new("English", "professional"), new("Urdu", "native")],
        Projects: [new("Wholesale invoicing", "GST invoices and monthly reports.", Outcome, "Lead developer", "web", "Django, Celery", 2024)]);

    private const string Outcome = "Month-end close cut from three days to one.";

    [Fact]
    public void The_summary_input_carries_the_work_and_not_the_person()
    {
        var (json, substance) = AiInputs.ProfileSummary(SomeProfile(), "freelancer");
        Assert.Contains("Django", json);
        Assert.Contains("Wholesale invoicing", json);
        Assert.Contains(Outcome, json);
        Assert.Contains("\"role\":\"freelancer\"", json);
        // The draft is in the first person and needs no name; the email
        // and the payment details are not even in the request shape.
        Assert.DoesNotContain("Anwar", json);
        Assert.DoesNotContain("payments", json);
        Assert.True(substance >= 30);
    }

    [Fact]
    public void A_project_s_outcome_counts_towards_what_there_is_to_read()
    {
        // The form adds it to the same floor, so the button and the
        // endpoint still agree on what is too thin to draft from.
        var bare = SomeProfile() with { Projects = [new("Wholesale invoicing", null, null, null, null, null, null)] };
        var told = SomeProfile() with { Projects = [new("Wholesale invoicing", null, Outcome, null, null, null, null)] };

        Assert.Equal(
            AiInputs.ProfileSummary(bare, "freelancer").Substance + Outcome.Length,
            AiInputs.ProfileSummary(told, "freelancer").Substance);
    }

    [Fact]
    public void The_summary_input_names_the_kinds_of_work_rather_than_their_keys()
    {
        // The model is being asked to write a sentence, and nobody writes
        // "devops". Keys go in, labels come out — and a key the taxonomy
        // does not know goes nowhere at all.
        var (json, _) = AiInputs.ProfileSummary(
            SomeProfile() with { SecondaryCategories = ["devops", "alchemy"] }, "freelancer");
        Assert.Contains("Web development", json);
        // The serialiser escapes the ampersand; the label is still the label.
        Assert.Contains("Cloud, DevOps", json);
        Assert.Contains("Part-time freelancer", json);
        Assert.DoesNotContain("alchemy", json);
        Assert.DoesNotContain("\"parttime\"", json);
    }

    [Fact]
    public void A_profile_with_nothing_on_it_is_not_worth_a_call()
    {
        var empty = new ProfileSummaryRequest(
            null, null, null, null, null, null, null, null, null, null, null, null);
        Assert.Equal(0, AiInputs.ProfileSummary(empty, "freelancer").Substance);
        var thin = empty with { Headline = "Developer" };
        Assert.True(AiInputs.ProfileSummary(thin, "freelancer").Substance < 30);
    }

    [Fact]
    public void The_summary_prompt_asks_for_the_first_person_and_forbids_invention()
    {
        var (system, user) = AiPrompts.ProfileSummary("{\"role\":\"client\"}");
        Assert.Contains("never judge, score, rank", system);
        Assert.Contains("first person", system);
        Assert.Contains("nothing they do not", system);
        Assert.Contains("\"role\":\"client\"", user);
    }

    [Fact]
    public void A_summary_draft_is_cut_to_the_field_it_fills_with_no_marker()
    {
        var answer = "{\"summary\":\"" + new string('x', 2500) + "\"}";
        var canonical = AiOutputs.Validate(AiFeature.ProfileSummary, answer, out var error);
        Assert.Null(error);
        using var doc = JsonDocument.Parse(canonical!);
        var summary = doc.RootElement.GetProperty("summary").GetString()!;
        Assert.Equal(2000, summary.Length);
        Assert.DoesNotContain("clipped", summary);
    }

    [Theory]
    [InlineData("{\"summary\":\"\"}")]
    [InlineData("{\"summary\":\"   \"}")]
    [InlineData("{\"text\":\"wrong key\"}")]
    public void An_empty_summary_is_refused_rather_than_offered(string answer)
    {
        Assert.Null(AiOutputs.Validate(AiFeature.ProfileSummary, answer, out var error));
        Assert.NotNull(error);
    }

    // -------------------------------------------- the profile review

    private static ProfileReview.Facts SomeReview() => new(
        Headline: "Flutter developer", HasAbout: true, PrimaryCategory: "Mobile apps",
        OtherCategories: ["Web development"],
        Skills: [("Flutter", "expert", 4), ("Dart", "advanced", 4)],
        Projects: 1, ProjectsWithLinks: 0, ProjectKinds: ["Mobile apps"],
        HasAvailability: true, HoursPerWeek: 30, YearsExperience: 5, HasPayout: true,
        StrengthPercent: 100, StepsNotDone: [],
        OpenOpportunities: 8, OpenOpportunitiesEnterable: 3,
        Candidates:
        [
            new("skill:rest apis", "Add REST APIs to your skills", "4 open opportunities require it", 50),
            new("projects", "Add 2 more portfolio projects", "worth 4 merit points, enough for 2 more open opportunities", 25),
            new("skill:firebase", "Add Firebase to your skills", "1 open opportunity requires it", 13),
            new("about", "Write an About You", "worth 4 merit points, enough for 1 more open opportunity", 13),
        ]);

    [Fact]
    public void The_review_input_carries_the_decided_lines_and_not_the_person()
    {
        var json = AiInputs.ProfileReview(SomeReview());
        // The lines travel decided, figure and all — and only the three
        // the box will show, so the model cannot word a fourth.
        Assert.Contains("\"thisProfileCanEnter\":3", json);
        Assert.Contains("\"id\":\"skill:rest apis\"", json);
        Assert.Contains("\"gainPercent\":50", json);
        Assert.DoesNotContain("\"id\":\"about\"", json);
        // Of the payout, only that there is one — the facts cannot even
        // carry an account number, an email or a name.
        Assert.Contains("\"payoutSet\":true", json);
        Assert.DoesNotContain("payments", json);
        Assert.DoesNotContain("email", json);
        Assert.DoesNotContain("displayName", json);
    }

    [Fact]
    public void The_review_input_is_the_cache_key_so_a_changed_figure_is_a_new_answer()
    {
        var before = AiRules.InputHash(AiInputs.ProfileReview(SomeReview()));
        var opened = SomeReview() with
        {
            OpenOpportunities = 9,
            Candidates =
            [
                new("skill:rest apis", "Add REST APIs to your skills", "5 open opportunities require it", 56),
            ],
        };
        Assert.NotEqual(before, AiRules.InputHash(AiInputs.ProfileReview(opened)));
        Assert.Equal(before, AiRules.InputHash(AiInputs.ProfileReview(SomeReview())));
    }

    // ------------------------------------------- the application's two

    [Fact]
    public void The_approach_input_carries_the_brief_and_only_the_fitting_past_work()
    {
        var json = AiInputs.ProjectApproach(
            "Realtime chat", "Build a chat.", "Web development", ["Django", "Redis"],
            ["Auth", "Rooms"], [("Language", "Python 3.12")], 4,
            "Django specialist", [("Django", "Expert", 6)],
            [("Wholesale invoicing", "Django, Celery", "Month-end close cut from three days to one.")],
            draft: "  My draft.  ");
        Assert.Contains("\"kindOfWork\":\"Web development\"", json);
        Assert.Contains("\"weeks\":4", json);
        Assert.Contains("\"level\":\"expert\"", json);
        Assert.Contains("Wholesale invoicing", json);
        Assert.Contains("\"draft\":\"My draft.\"", json);
        Assert.Contains("\"draft\":null", AiInputs.ProjectApproach(
            "T", "B", null, [], [], [], null, null, [], [], draft: " "));
    }

    [Fact]
    public void An_approach_answer_is_one_text_cut_to_the_field()
    {
        var canonical = AiOutputs.Validate(AiFeature.ProjectApproach,
            "{\"approach\":\"" + new string('p', 1200) + "\"}", out var error);
        Assert.Null(error);
        using var doc = JsonDocument.Parse(canonical!);
        Assert.Equal(ApplicationRules.MaxApproach, doc.RootElement.GetProperty("approach").GetString()!.Length);
        Assert.Null(AiOutputs.Validate(AiFeature.ProjectApproach, "{\"approach\":\"  \"}", out var empty));
        Assert.Equal("The draft came back empty.", empty);
    }

    [Fact]
    public void The_evaluation_prompt_asks_for_words_per_id_and_no_verdict()
    {
        var (system, user) = AiPrompts.ApplicationEvaluation("{\"evaluation\":{}}");
        Assert.Contains("never judge, score, rank", system);
        Assert.Contains("\"strengths\"", system);
        Assert.Contains("\"note\":string", system);
        Assert.Contains("never write a figure", system);
        Assert.Contains("Never say whether to select", system);
        Assert.Contains("\"evaluation\":{}", user);
    }

    [Fact]
    public void An_evaluation_answer_carries_ids_and_phrases_and_no_figure_of_its_own()
    {
        var answer = "{\"strengths\":["
            + "{\"id\":\"skills\",\"score\":99,\"text\":\"Six years of Django, as the brief asks.\"},"
            + "{\"id\":\"skills\",\"text\":\"Said twice: the first stands.\"},"
            + "{\"text\":\"No id, no line.\"}],"
            + "\"risks\":[{\"id\":\"projects\",\"text\":\"\"},{\"id\":\"availability\",\"text\":\"Twenty hours against a two-week build.\"}],"
            + "\"note\":\" Open the invoicing project first. \"}";
        var canonical = AiOutputs.Validate(AiFeature.ApplicationEvaluation, answer, out var error);
        Assert.Null(error);
        using var doc = JsonDocument.Parse(canonical!);
        var strengths = doc.RootElement.GetProperty("strengths").EnumerateArray().ToList();
        Assert.Single(strengths);
        Assert.Equal("skills", strengths[0].GetProperty("id").GetString());
        var risks = doc.RootElement.GetProperty("risks").EnumerateArray().ToList();
        Assert.Equal(["availability"], risks.Select(r => r.GetProperty("id").GetString()));
        Assert.Equal("Open the invoicing project first.", doc.RootElement.GetProperty("note").GetString());
        Assert.DoesNotContain("score", canonical);

        // Nothing to word is an honest answer, not an error.
        var bare = AiOutputs.Validate(AiFeature.ApplicationEvaluation, "{\"strengths\":[],\"risks\":[]}", out var bareError);
        Assert.Null(bareError);
        Assert.Contains("\"note\":null", bare);
    }

    [Fact]
    public void The_review_prompt_asks_for_words_only_and_forbids_ranking()
    {
        var (system, user) = AiPrompts.ProfileReview("{\"openOpportunities\":{}}");
        Assert.Contains("never judge, score, rank", system);
        Assert.Contains("\"strongFor\"", system);
        Assert.Contains("\"id\":string", system);
        Assert.Contains("never write a figure", system);
        Assert.Contains("never rank the member", system);
        Assert.Contains("never invent a client or an opportunity", system);
        Assert.Contains("\"openOpportunities\":{}", user);
    }

    [Fact]
    public void A_review_answer_carries_ids_and_words_and_no_figure_of_its_own()
    {
        var answer = "{\"strongFor\":\"  mobile development \",\"improvements\":["
            + "{\"id\":\"skill:rest apis\",\"gain\":99,\"text\":\"Add REST APIs — four open opportunities ask for it.\"},"
            + "{\"id\":\"skill:rest apis\",\"text\":\"Said twice: the first sentence stands.\"},"
            + "{\"text\":\"No id, no line.\"},"
            + "{\"id\":\"projects\",\"text\":\"\"},"
            + "{\"id\":\"about\",\"text\":\"Write an About.\"},"
            + "{\"id\":\"links\",\"text\":\"Link a project.\"},"
            + "{\"id\":\"github\",\"text\":\"A fourth line never shows.\"}]}";
        var canonical = AiOutputs.Validate(AiFeature.ProfileReview, answer, out var error);
        Assert.Null(error);
        using var doc = JsonDocument.Parse(canonical!);
        Assert.Equal("mobile development", doc.RootElement.GetProperty("strongFor").GetString());
        var lines = doc.RootElement.GetProperty("improvements").EnumerateArray().ToList();
        Assert.Equal(["skill:rest apis", "about", "links"], lines.Select(l => l.GetProperty("id").GetString()));
        Assert.Equal("Add REST APIs — four open opportunities ask for it.", lines[0].GetProperty("text").GetString());
        Assert.DoesNotContain("gain", canonical);
    }

    [Theory]
    [InlineData("{\"strongFor\":\"mobile development\",\"improvements\":[]}")]
    [InlineData("{\"strongFor\":\"\"}")]
    public void A_review_with_nothing_to_word_is_an_honest_answer_not_an_error(string answer)
    {
        var canonical = AiOutputs.Validate(AiFeature.ProfileReview, answer, out var error);
        Assert.Null(error);
        using var doc = JsonDocument.Parse(canonical!);
        Assert.Equal(0, doc.RootElement.GetProperty("improvements").GetArrayLength());
    }

    [Fact]
    public void A_review_line_is_cut_to_its_box_with_no_marker()
    {
        var answer = "{\"improvements\":[{\"id\":\"about\",\"text\":\"" + new string('y', 300) + "\"}]}";
        var canonical = AiOutputs.Validate(AiFeature.ProfileReview, answer, out _);
        using var doc = JsonDocument.Parse(canonical!);
        var text = doc.RootElement.GetProperty("improvements")[0].GetProperty("text").GetString()!;
        Assert.Equal(140, text.Length);
        Assert.DoesNotContain("clipped", text);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("strongFor").ValueKind);
    }

    [Fact]
    public void The_page_gets_the_live_figures_with_the_models_words_where_they_answer_to_a_line()
    {
        // Words for a line that is still there, words for a line that is
        // gone, and a line with no words yet: the figures are the
        // arithmetic's in every case, and the words follow. Read as the page
        // reads it: through the API's web defaults, camel-cased.
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var words = "{\"strongFor\":\"mobile development\",\"improvements\":["
            + "{\"id\":\"skill:rest apis\",\"text\":\"Add REST APIs — four open opportunities ask for it.\"},"
            + "{\"id\":\"skill:graphql\",\"text\":\"A line for a change that no longer opens anything.\"}]}";
        var json = JsonSerializer.Serialize(ProfileReview.Resolve(words, SomeReview()), web);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("mobile development", doc.RootElement.GetProperty("strongFor").GetString());
        var lines = doc.RootElement.GetProperty("improvements").EnumerateArray().ToList();
        Assert.Equal(3, lines.Count);
        Assert.Equal([50, 25, 13], lines.Select(l => l.GetProperty("gain").GetInt32()));
        Assert.Equal("Add REST APIs — four open opportunities ask for it.", lines[0].GetProperty("text").GetString());
        Assert.Equal("Add 2 more portfolio projects — worth 4 merit points, enough for 2 more open opportunities.",
            lines[1].GetProperty("text").GetString());
        Assert.DoesNotContain("graphql", json);

        // No answer yet: the same lines, in the portal's own words.
        using var plain = JsonDocument.Parse(JsonSerializer.Serialize(ProfileReview.Resolve(null, SomeReview()), web));
        var first = plain.RootElement.GetProperty("improvements")[0];
        Assert.Equal(50, first.GetProperty("gain").GetInt32());
        Assert.Equal("Add REST APIs to your skills — 4 open opportunities require it.", first.GetProperty("text").GetString());
        Assert.Equal(JsonValueKind.Null, plain.RootElement.GetProperty("strongFor").ValueKind);
    }

    [Fact]
    public void A_review_that_is_not_an_object_is_refused()
    {
        Assert.Null(AiOutputs.Validate(AiFeature.ProfileReview, "just words", out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Prompts_clip_the_brief_before_it_travels()
    {
        var form = ShopForm with { BriefMarkdown = new string('x', 100_000) };
        var (_, user) = AiPrompts.ForForm(AiFeature.BriefCoach, form);
        Assert.True(user.Length < AiRules.MaxBriefChars + 600);
        Assert.Contains("[…clipped]", user);
    }
}
