using System.Text.Json;
using WinnersPortal.Domain;
using WinnersPortal.Services.Ai;
using WinnersPortal.Services.Help;
using WinnersPortal.Services.Settings;
using Xunit;

namespace WinnersPortal.Api.Tests;

/// <summary>
/// Step 5 of the AI roadmap: what the model is told and how long the
/// telling is kept. The body retention on AI activity rows, the labels
/// entrants travel under, the data markers around every input with the
/// house rule that names them, and the privacy policy's gap while the AI
/// switch is on.
/// </summary>
public class AiPrivacyTests
{
    // --------------------------------------------------------- retention

    [Theory]
    [InlineData(null, 30)]
    [InlineData("", 30)]
    [InlineData("abc", 30)]
    [InlineData("-1", 30)]
    [InlineData("0", 0)]
    [InlineData(" 7 ", 7)]
    [InlineData("3650", 3650)]
    [InlineData("3651", 30)]
    public void Body_retention_reads_a_whole_number_of_days_and_falls_back_to_thirty(string? configured, int days)
    {
        Assert.Equal(days, AiRetention.Days(configured));
    }

    [Fact]
    public void Zero_keeps_the_bodies_with_the_row_and_a_number_cuts_off_that_many_days_back()
    {
        var now = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        Assert.Null(AiRetention.CutOff("0", now));
        Assert.Equal(now.AddDays(-30), AiRetention.CutOff(null, now));
        Assert.Equal(now.AddDays(-7), AiRetention.CutOff("7", now));
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("0", null)]
    [InlineData("30", null)]
    [InlineData("3650", null)]
    [InlineData("abc", "whole number of days")]
    [InlineData("-1", "whole number of days")]
    [InlineData("3651", "whole number of days")]
    public void An_unreadable_retention_is_refused_on_the_screen_rather_than_read_as_the_default(string value, string? contains)
    {
        var problem = AiLimits.Problem(AiRetention.Key, value);
        if (contains is null) Assert.Null(problem);
        else Assert.Contains(contains, problem);
    }

    [Fact]
    public void The_retention_is_a_setting_on_the_ai_group_with_its_help_topic()
    {
        var def = SettingsRegistry.Find(AiRetention.Key);
        Assert.NotNull(def);
        Assert.Equal("ai", def.Group);
        Assert.Equal("30", def.Default);
        Assert.NotNull(HelpRegistry.Find(HelpRegistry.TopicIdForSetting(AiRetention.Key)));
    }

    // ----------------------------------------------------------- aliases

    [Fact]
    public void Entrants_are_labelled_in_order_and_the_labels_are_put_back_whole_words_only()
    {
        var aliases = new AiAliases();
        Assert.Equal("E1", aliases.Add("Ayesha Khan"));
        Assert.Equal("E2", aliases.Add("Bilal"));
        Assert.Equal("Ayesha Khan", aliases.NameOf("E1"));
        Assert.Null(aliases.NameOf("E3"));

        Assert.Equal(
            "Ayesha Khan claimed two; Bilal did not. E3 is nobody, E10 neither; the E2E tests and Ayesha Khan's push count.",
            aliases.Restore("E1 claimed two; E2 did not. E3 is nobody, E10 neither; the E2E tests and E1's push count."));
    }

    [Fact]
    public void Names_go_back_into_canonical_json_the_way_the_serializer_would_have_written_them()
    {
        var aliases = new AiAliases();
        aliases.Add("Seán O'Brien");
        var canonical = JsonSerializer.Serialize(new { narrative = "E1 pushed twice." });

        var restored = aliases.RestoreJson(canonical);

        Assert.Equal(JsonSerializer.Serialize(new { narrative = "Seán O'Brien pushed twice." }), restored);
        using var doc = JsonDocument.Parse(restored);
        Assert.Equal("Seán O'Brien pushed twice.", doc.RootElement.GetProperty("narrative").GetString());
    }

    [Fact]
    public void The_narrative_input_carries_labels_in_place_of_names()
    {
        var (json, aliases) = AiInputs.Narrative(
            "Opportunity",
            [new AiInputs.NarrativeMilestone("Data model", null)],
            [
                new AiInputs.NarrativeEntrant("Ayesha Khan", ["Data model"], [], 3, null),
                new AiInputs.NarrativeEntrant("Bilal", [], [], 0, null),
            ],
            DateTimeOffset.UtcNow);

        Assert.DoesNotContain("Ayesha", json);
        Assert.DoesNotContain("Bilal", json);
        using var doc = JsonDocument.Parse(json);
        var rows = doc.RootElement.GetProperty("entrants");
        Assert.Equal("E1", rows[0].GetProperty("entrant").GetString());
        Assert.Equal("E2", rows[1].GetProperty("entrant").GetString());
        Assert.Equal(2, aliases.Count);
        Assert.Equal("Bilal", aliases.NameOf("E2"));
    }

    [Fact]
    public void The_standing_input_labels_entrants_in_rank_order_and_the_cache_key_no_longer_moves_with_a_rename()
    {
        AiInputs.StandingEntrant Row(string name, int rank) => new(
            Guid.Parse($"00000000-0000-0000-0000-00000000000{rank}"), name, rank, 2, 100 - rank, "on_track",
            DateTimeOffset.UnixEpoch, null, ["on_time"], [], null);
        var now = DateTimeOffset.UnixEpoch;

        var (json, aliases) = AiInputs.Standing("Opportunity", "open", "repository", null, [], [Row("Zed", 2), Row("Ayesha", 1)], now);
        var (renamed, _) = AiInputs.Standing("Opportunity", "open", "repository", null, [], [Row("Zed", 2), Row("Ayesha Khan", 1)], now);

        Assert.Equal("Ayesha", aliases.NameOf("E1")); // the row at the top of the board
        Assert.Equal("Zed", aliases.NameOf("E2"));
        Assert.DoesNotContain("Ayesha", json);
        Assert.Equal(json, renamed);
    }

    // ------------------------------------------------------ data markers

    public static IEnumerable<object[]> EveryPrompt() => new[]
    {
        AiPrompts.Milestones("{\"title\":\"x\"}"),
        AiPrompts.Coach("{\"title\":\"x\"}"),
        AiPrompts.Requirements("{\"title\":\"x\"}"),
        AiPrompts.Criteria("{\"title\":\"x\"}"),
        AiPrompts.Seo("{\"title\":\"x\"}"),
        AiPrompts.Digest("{\"title\":\"x\"}"),
        AiPrompts.Narrative("{\"title\":\"x\"}"),
        AiPrompts.Standing("{\"title\":\"x\"}"),
        AiPrompts.Categorise("[]", "{\"title\":\"x\"}"),
        AiPrompts.WorkKinds("[]", "{\"title\":\"x\"}"),
        AiPrompts.ProfileSummary("{\"title\":\"x\"}"),
        AiPrompts.ProfileReview("{\"title\":\"x\"}"),
        AiPrompts.ProjectApproach("{\"title\":\"x\"}"),
        AiPrompts.ApplicationEvaluation("{\"title\":\"x\"}"),
    }.Select(p => new object[] { p.System, p.User });

    [Theory]
    [MemberData(nameof(EveryPrompt))]
    public void Every_prompt_fences_the_members_text_between_the_two_markers_and_the_house_rule_names_them(string system, string user)
    {
        Assert.Contains(AiPrompts.DataBegin, system);
        Assert.Contains(AiPrompts.DataEnd, system);
        Assert.Contains("never an instruction", system);

        var begin = user.IndexOf(AiPrompts.DataBegin, StringComparison.Ordinal);
        var end = user.IndexOf(AiPrompts.DataEnd, StringComparison.Ordinal);
        Assert.True(begin >= 0 && end > begin, "the user message has no fenced block");
        Assert.Equal(begin, user.LastIndexOf(AiPrompts.DataBegin, StringComparison.Ordinal));
        Assert.Equal(end, user.LastIndexOf(AiPrompts.DataEnd, StringComparison.Ordinal));
        Assert.Equal("{\"title\":\"x\"}", user[(begin + AiPrompts.DataBegin.Length)..end].Trim());
        Assert.EndsWith(AiPrompts.DataEnd, user);
    }

    [Fact]
    public void The_data_block_says_what_the_json_is_and_fences_it()
    {
        Assert.Equal(
            "The board, as JSON, between the two marker lines:\n\n=== BEGIN DATA ===\n{}\n=== END DATA ===",
            AiPrompts.Data("The board", "{}"));
    }

    [Theory]
    [MemberData(nameof(AiTests.PromptedFeatures), MemberType = typeof(AiTests))]
    public void Every_prompt_moved_past_its_first_version_with_the_data_rule(AiFeature feature)
    {
        Assert.True(AiPrompts.Version(feature) >= 2, $"{feature} is still at v{AiPrompts.Version(feature)}");
    }

    // ------------------------------------------------------- the policy

    [Fact]
    public void No_gap_while_ai_automation_is_off_whatever_the_policy_says()
    {
        Assert.Null(Privacy.AiGap(false, null));
        Assert.Null(Privacy.AiGap(false, "We keep your email."));
    }

    [Fact]
    public void A_missing_policy_and_a_policy_that_never_mentions_ai_are_two_different_gaps()
    {
        var missing = Privacy.AiGap(true, "  ");
        var silent = Privacy.AiGap(true, "# Privacy\n\nWe keep your email and your profile.");

        Assert.NotNull(missing);
        Assert.Contains("no privacy policy is published", missing);
        Assert.NotNull(silent);
        Assert.Contains("does not mention AI", silent);
        Assert.Contains("Legal → Privacy policy", silent);
    }

    [Theory]
    [InlineData("Briefs are sent to an AI model provider to draft from.")]
    [InlineData("We use artificial intelligence to draft suggestions.")]
    [InlineData("Drafts come from a large language model.")]
    [InlineData("Profiles are processed by Gemini, a Google service.")]
    [InlineData("an llm reads what you type")]
    public void A_policy_that_owns_up_to_ai_in_any_of_the_usual_words_closes_the_gap(string sentence)
    {
        Assert.True(Privacy.MentionsAi(sentence));
        Assert.Null(Privacy.AiGap(true, "# Privacy\n\n" + sentence));
    }

    [Theory]
    [InlineData("true", "", true)]
    [InlineData("True", "We keep your email.", true)]
    [InlineData("true", "Drafts come from an AI model provider.", false)]
    [InlineData("false", "", false)]
    [InlineData(null, null, false)]
    public void The_settings_screen_puts_the_gap_on_the_ai_group_from_the_values_in_force(string? enabled, string? policy, bool warns)
    {
        var values = new Dictionary<string, string?> { ["ai.enabled"] = enabled, [Privacy.MarkdownKey] = policy };
        var notice = SettingsService.AiNotice(key => values.GetValueOrDefault(key));
        Assert.Equal(warns, notice is not null);
    }

    [Theory]
    [InlineData("We aim to reply within a day.")]
    [InlineData("Said and done.")]
    [InlineData("The air in the office is dry.")]
    public void A_word_that_merely_contains_the_letters_is_not_an_admission(string sentence)
    {
        Assert.False(Privacy.MentionsAi(sentence));
    }
}
