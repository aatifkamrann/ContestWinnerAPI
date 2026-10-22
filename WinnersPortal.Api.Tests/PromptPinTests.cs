using System.Text.Json;
using System.Text.RegularExpressions;
using WinnersPortal.AiEvals;
using WinnersPortal.Domain;
using WinnersPortal.Services.Ai;
using WinnersPortal.Services.Opportunities;
using Xunit;

namespace WinnersPortal.Api.Tests;

/// <summary>
/// Every prompt's wording, pinned. A committed copy of each prompt sits
/// under src/WinnersPortal.Services/Ai/Prompts/, headed by its version and
/// rendered around «input»; this test renders them again and compares. The
/// version is folded into every cached answer's key, so a reworded prompt
/// whose number stayed put would keep serving answers to the old wording —
/// here it fails the build instead, naming the number to raise.
/// </summary>
public class PromptPinTests
{
    /// <summary>Set to "write" to refresh the copies whose version was raised, and to add missing ones.</summary>
    public const string WriteVariable = "WP_PROMPT_SNAPSHOTS";

    private static readonly JsonSerializerOptions SchemaJson = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static IEnumerable<AiFeature> Prompted => Enum.GetValues<AiFeature>().Where(f => f != AiFeature.SpamFilter);

    public static string PinsDir => Path.Combine(EvalCases.RepoRoot(), "src", "WinnersPortal.Services", "Ai", "Prompts");

    /// <summary>The pinned copy as a file holds it: the version line, then the system message, the user message and the schema sent with them.</summary>
    public static string Render(AiFeature feature)
    {
        var (system, user) = AiPrompts.Template(feature);
        return $"{AiPrompts.FeatureName(feature)} · prompt v{AiPrompts.Version(feature)}\n"
            + $"The wording sent for this feature, with {AiPrompts.InputPlaceholder} where the data goes. Rebuilt and compared by\n"
            + $"PromptPinTests: reword the prompt, raise AiPrompts.Version, then refresh this file with {WriteVariable}=write dotnet test.\n"
            + "\n=== system ===\n" + system
            + "\n\n=== user ===\n" + user
            + "\n\n=== schema ===\n" + JsonSerializer.Serialize(AiOutputs.Schema(feature, Services.Opportunities.Taxonomy.Default), SchemaJson).Replace("\r\n", "\n")
            + "\n";
    }

    /// <summary>What a pinned copy says about the code beside it.</summary>
    public enum Pin { Same, Missing, WordingChanged, VersionRaised, VersionLowered }

    /// <summary>
    /// The verdict, pure: the copy matches, or is missing, or the wording
    /// moved under the same number (the case this exists for), or the
    /// number moved — up, when the copy only needs refreshing; down, never.
    /// </summary>
    public static (Pin Pin, int? PinnedVersion) Compare(string rendered, string? saved, int version)
    {
        if (saved is null) return (Pin.Missing, null);
        saved = saved.Replace("\r\n", "\n");
        var head = Regex.Match(saved, @"^\S+ · prompt v(\d+)\n");
        int? pinned = head.Success ? int.Parse(head.Groups[1].Value) : null;
        if (saved == rendered) return (Pin.Same, pinned);
        return pinned switch
        {
            null => (Pin.Missing, null),
            _ when pinned < version => (Pin.VersionRaised, pinned),
            _ when pinned > version => (Pin.VersionLowered, pinned),
            _ => (Pin.WordingChanged, pinned),
        };
    }

    [Fact]
    public void Every_prompt_matches_its_pinned_copy_or_names_the_version_to_raise()
    {
        var write = string.Equals(Environment.GetEnvironmentVariable(WriteVariable), "write", StringComparison.OrdinalIgnoreCase);
        var problems = new List<string>();
        foreach (var feature in Prompted)
        {
            var name = AiPrompts.FeatureName(feature);
            var file = Path.Combine(PinsDir, name + ".txt");
            var rendered = Render(feature);
            var version = AiPrompts.Version(feature);
            var (pin, pinned) = Compare(rendered, File.Exists(file) ? File.ReadAllText(file) : null, version);
            switch (pin)
            {
                case Pin.Same:
                    break;
                case Pin.Missing or Pin.VersionRaised when write:
                    Directory.CreateDirectory(PinsDir);
                    File.WriteAllText(file, rendered);
                    break;
                case Pin.Missing:
                    problems.Add($"{name}: no pinned copy at Ai/Prompts/{name}.txt; run {WriteVariable}=write dotnet test to add it.");
                    break;
                case Pin.VersionRaised:
                    problems.Add($"{name}: raised from v{pinned} to v{version}; run {WriteVariable}=write dotnet test to refresh Ai/Prompts/{name}.txt.");
                    break;
                case Pin.VersionLowered:
                    problems.Add($"{name}: the pinned copy says v{pinned} and AiPrompts.Version says v{version}; a version never goes down.");
                    break;
                case Pin.WordingChanged:
                    problems.Add($"{name}: the wording changed and the version is still v{version}. Raise AiPrompts.Version({nameof(AiFeature)}.{feature}) "
                        + $"to {version + 1} — cached answers to the old wording would otherwise be served as new — then run {WriteVariable}=write dotnet test. "
                        + $"A typo fix that cannot change an answer: edit Ai/Prompts/{name}.txt by hand to match.");
                    break;
            }
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void No_pinned_copy_is_left_for_a_feature_without_a_prompt()
    {
        var known = Prompted.Select(f => AiPrompts.FeatureName(f) + ".txt").ToHashSet(StringComparer.Ordinal);
        var stray = Directory.Exists(PinsDir)
            ? Directory.GetFiles(PinsDir).Select(Path.GetFileName).Where(f => !known.Contains(f!)).ToList()
            : [];
        Assert.Empty(stray);
    }

    [Fact]
    public void Every_feature_with_a_version_has_a_template_and_the_spam_scan_has_neither()
    {
        foreach (var feature in Prompted)
        {
            var (system, user) = AiPrompts.Template(feature);
            Assert.StartsWith(AiPrompts.HouseRules, system);
            Assert.Contains(AiPrompts.DataBegin + "\n" + AiPrompts.InputPlaceholder + "\n" + AiPrompts.DataEnd, user);
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => AiPrompts.Template(AiFeature.SpamFilter));
    }

    [Theory]
    [InlineData(AiFeature.CategorySuggestion)]
    [InlineData(AiFeature.BriefCoach)]
    [InlineData(AiFeature.RequirementsSuggestion)]
    [InlineData(AiFeature.MilestoneExtraction)]
    [InlineData(AiFeature.CriteriaSuggestion)]
    [InlineData(AiFeature.SeoMetadata)]
    public void The_template_is_the_wording_the_form_tools_send(AiFeature feature)
    {
        var form = new OpportunityFormSnapshot("A shop", "Build me a storefront with a cart, a checkout and an order list for staff.",
            null, null, ["React"], [], "repository", true, [], []);
        var json = AiInputs.OpportunityForm(form, AiFormReads.Of(feature));
        var (system, user) = AiPrompts.Template(feature);

        var sent = AiPrompts.ForForm(feature, form);

        Assert.Equal(system, sent.System);
        Assert.Equal(user.Replace(AiPrompts.InputPlaceholder, json).Replace(AiPrompts.TaxonomyPlaceholder, AiInputs.Taxonomy()), sent.User);
    }

    [Fact]
    public void A_reword_under_the_same_number_fails_and_a_raised_number_only_asks_for_a_refresh()
    {
        var saved = "categorySuggestion · prompt v2\nnotes\n\n=== system ===\nPick a category.\n";

        Assert.Equal((Pin.Same, 2), Compare(saved, saved.Replace("\n", "\r\n"), 2));
        Assert.Equal((Pin.WordingChanged, 2), Compare(saved.Replace("Pick", "Choose"), saved, 2));
        Assert.Equal((Pin.VersionRaised, 2), Compare(saved.Replace("v2", "v3"), saved, 3));
        Assert.Equal((Pin.VersionLowered, 2), Compare(saved.Replace("v2", "v1"), saved, 1));
        Assert.Equal((Pin.Missing, null), Compare(saved, null, 2));
        Assert.Equal((Pin.Missing, null), Compare(saved, "something else", 2));
    }
}
