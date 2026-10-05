using WinnersPortal.Services.Ai;
using WinnersPortal.Services.Settings;
using Xunit;

namespace WinnersPortal.Api.Tests;

/// <summary>
/// The registry is two lists that have to agree: the groups the settings
/// screen renders as tabs, and the settings that fill them. Nothing at
/// runtime notices when they drift — a setting in an undeclared group simply
/// never appears, and a group nothing belongs to renders as an empty tab —
/// so the disagreement is caught here instead.
/// </summary>
public class SettingsRegistryTests
{
    [Fact]
    public void Every_setting_belongs_to_a_declared_group()
    {
        var groups = SettingsRegistry.Groups.Select(g => g.Name)
            .Append(SettingsRegistry.SystemGroup)
            .ToHashSet(StringComparer.Ordinal);

        var stranded = SettingsRegistry.All
            .Where(s => !groups.Contains(s.Group))
            .Select(s => $"{s.Key} (group '{s.Group}')")
            .ToList();

        Assert.True(stranded.Count == 0,
            "These settings name a group the screen does not render, so they are invisible:\n  "
            + string.Join("\n  ", stranded));
    }

    [Fact]
    public void Every_declared_group_has_at_least_one_setting()
    {
        // An empty group is a tab an operator opens expecting something.
        // Deleting the last setting out of a group means deleting the group.
        var used = SettingsRegistry.All.Select(s => s.Group).ToHashSet(StringComparer.Ordinal);
        var empty = SettingsRegistry.Groups.Select(g => g.Name).Where(n => !used.Contains(n)).ToList();

        Assert.True(empty.Count == 0,
            "These groups have no settings left and would render as empty tabs:\n  "
            + string.Join("\n  ", empty));
    }

    [Fact]
    public void Setting_keys_are_unique()
    {
        var duplicates = SettingsRegistry.All
            .GroupBy(s => s.Key, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.True(duplicates.Count == 0, "Duplicate setting keys: " + string.Join(", ", duplicates));
    }

    [Fact]
    public void A_multiline_setting_is_a_plain_document()
    {
        // The screen renders a secret as a masked one-line box and a boolean
        // as a switch, and checks those flags first — so a multiline secret
        // would silently come out masked and one line tall. The one document
        // the portal holds today is the terms of service.
        var contradictory = SettingsRegistry.All
            .Where(s => s.IsMultiline && (s.IsSecret || s.IsBoolean))
            .Select(s => s.Key)
            .ToList();

        Assert.True(contradictory.Count == 0,
            "Multiline settings cannot also be secret or boolean: " + string.Join(", ", contradictory));
        Assert.Contains(SettingsRegistry.All, s => s.Key == "legal.termsMarkdown" && s.IsMultiline);
    }

    [Fact]
    public void The_push_pair_is_not_an_operator_field()
    {
        // Two VAPID fields once sat in an operator-facing group with nothing
        // sending, and were deleted for it. The pair came back with the
        // sender — as system settings the portal generates itself, because
        // a pair an operator could edit is a pair an operator could break,
        // orphaning every subscription. NotificationTests pins the rest.
        Assert.DoesNotContain(SettingsRegistry.All, s => s.Key.StartsWith("push.", StringComparison.Ordinal));
        Assert.All(SettingsRegistry.All.Where(s => s.Key.Contains("push", StringComparison.OrdinalIgnoreCase)),
            s => Assert.Equal(SettingsRegistry.SystemGroup, s.Group));
    }

    // ------------------------------------------------ what is saved wins

    [Fact]
    public void What_is_saved_wins_over_the_environment_for_every_setting()
    {
        foreach (var def in SettingsRegistry.All)
        {
            Assert.Equal("saved", SettingsService.Resolve(def, env: "from-the-deployment", stored: "saved"));
            Assert.Equal(SettingSource.Saved, SettingsService.SourceOf("from-the-deployment", "saved"));
        }
    }

    [Fact]
    public void The_variable_fills_in_only_while_nothing_is_saved_and_then_the_default()
    {
        var def = SettingsRegistry.Find("ai.provider")!;
        Assert.Equal("openai", SettingsService.Resolve(def, env: "openai", stored: null));
        Assert.Equal("openai", SettingsService.Resolve(def, env: "openai", stored: "")); // cleared on the screen
        Assert.Equal(def.Default, SettingsService.Resolve(def, env: null, stored: null));
        Assert.Equal("", SettingsService.Resolve(def, env: "", stored: null)); // a deployment may give a blank
        Assert.Equal(SettingSource.Environment, SettingsService.SourceOf("openai", ""));
        Assert.Equal(SettingSource.Default, SettingsService.SourceOf(null, null));
    }

    [Fact]
    public void The_setup_wizard_leaves_an_echoed_variable_to_the_environment()
    {
        // Compose gives the SMTP host; the wizard shows it and sends it back.
        Assert.True(Services.Setup.SetupService.EchoesEnvironment("mail", "mail", SettingSource.Environment));
        // Changed in the wizard, or already saved: it is saved, and wins.
        Assert.False(Services.Setup.SetupService.EchoesEnvironment("mail", "smtp.example.com", SettingSource.Environment));
        Assert.False(Services.Setup.SetupService.EchoesEnvironment("mail", "mail", SettingSource.Saved));
        Assert.False(Services.Setup.SetupService.EchoesEnvironment(null, "mail", SettingSource.Default));
    }

    [Fact]
    public void The_eval_pair_is_its_own_group_named_as_the_tool_names_it()
    {
        var eval = SettingsRegistry.All.Where(s => s.Group == EvalSettings.Group).Select(s => s.Key).ToList();
        Assert.Equal([EvalSettings.EnabledKey, EvalSettings.ProviderKey, EvalSettings.ApiKeyKey, EvalSettings.MaxCallsKey, EvalSettings.TimeoutKey], eval);
        Assert.Equal(EvalSettings.ProviderEnv, SettingsRegistry.EnvVarName(EvalSettings.ProviderKey));
        Assert.True(SettingsRegistry.Find(EvalSettings.ApiKeyKey)!.IsSecret);
    }
}
