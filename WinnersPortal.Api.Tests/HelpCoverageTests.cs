using WinnersPortal.Services.Help;
using WinnersPortal.Services.Settings;
using Xunit;

namespace WinnersPortal.Api.Tests;

/// <summary>
/// Keeps field-level help honest as the portal grows. These are the tests that
/// make "every valuable field has help" a property of the build rather than
/// something everyone has to remember.
/// </summary>
public class HelpCoverageTests
{
    [Fact]
    public void Every_setting_marked_HelpRequired_has_a_help_topic()
    {
        var missing = SettingsRegistry.All
            .Where(s => s.HelpRequired)
            .Select(s => HelpRegistry.TopicIdForSetting(s.Key))
            .Where(id => HelpRegistry.Find(id) is null)
            .ToList();

        Assert.True(missing.Count == 0,
            "These settings are marked HelpRequired but have no topic in HelpRegistry:\n  "
            + string.Join("\n  ", missing));
    }

    [Fact]
    public void Every_settings_help_topic_points_at_a_real_setting()
    {
        var keys = SettingsRegistry.All.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);

        var orphans = HelpRegistry.All
            .Where(t => t.Id.StartsWith("settings.", StringComparison.Ordinal))
            .Where(t => !keys.Contains(t.Id["settings.".Length..]))
            .Select(t => t.Id)
            .ToList();

        Assert.True(orphans.Count == 0,
            "These help topics describe settings that no longer exist:\n  " + string.Join("\n  ", orphans));
    }

    [Fact]
    public void Help_topic_ids_are_unique()
    {
        var duplicates = HelpRegistry.All
            .GroupBy(t => t.Id, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.True(duplicates.Count == 0, "Duplicate help topic ids: " + string.Join(", ", duplicates));
    }

    [Fact]
    public void Short_help_stays_short_enough_for_a_tooltip()
    {
        var tooLong = HelpRegistry.All
            .Where(t => t.Short.Length > 120)
            .Select(t => $"{t.Id} ({t.Short.Length} chars)")
            .ToList();

        Assert.True(tooLong.Count == 0,
            "Tooltip text must stay under 120 characters; move the detail into Detail:\n  "
            + string.Join("\n  ", tooLong));
    }

    [Fact]
    public void Secrets_explain_their_blast_radius()
    {
        // Someone pasting a credential deserves to know what it unlocks, so
        // every secret's topic must say what goes wrong when it is misused.
        var secretsWithoutWhy = SettingsRegistry.All
            .Where(s => s.IsSecret)
            .Select(s => HelpRegistry.Find(HelpRegistry.TopicIdForSetting(s.Key)))
            .Where(t => t is not null && string.IsNullOrWhiteSpace(t.Why))
            .Select(t => t!.Id)
            .ToList();

        Assert.True(secretsWithoutWhy.Count == 0,
            "Every secret's help topic needs a Why explaining the consequences:\n  "
            + string.Join("\n  ", secretsWithoutWhy));
    }

    [Fact]
    public void Ai_settings_all_carry_help_because_they_govern_data_leaving_the_server()
    {
        var aiWithoutHelp = SettingsRegistry.All
            .Where(s => s.Group == "ai" && !s.HelpRequired)
            .Select(s => s.Key)
            .ToList();

        Assert.True(aiWithoutHelp.Count == 0,
            "Every AI setting must be HelpRequired:\n  " + string.Join("\n  ", aiWithoutHelp));
    }

    [Fact]
    public void Ai_is_off_by_default_and_so_is_sharing_code()
    {
        // A fresh deployment must never send anything anywhere until someone
        // chooses to. If these defaults change, that decision should be
        // deliberate enough to update this test.
        Assert.Equal("false", SettingsRegistry.Find("ai.enabled")!.Default);
        Assert.Equal("false", SettingsRegistry.Find("ai.sendCodeToProvider")!.Default);
    }

    [Fact]
    public void Every_required_form_field_has_a_help_topic()
    {
        // Phase-two forms: award, deadline, brief, milestones, publish, the
        // GitHub username, withdrawal, and the role choice all pass the
        // coverage rule, so losing any of their topics fails the build.
        var missing = HelpRegistry.RequiredFormTopicIds
            .Where(id => HelpRegistry.Find(id) is null)
            .ToList();

        Assert.True(missing.Count == 0,
            "These form fields require help but have no topic in HelpRegistry:\n  "
            + string.Join("\n  ", missing));
    }

    [Fact]
    public void Irreversible_form_actions_explain_their_blast_radius()
    {
        // Publish, withdraw, remove, cancel, and the role choice are one-way
        // doors; a topic without a Why on one of those is decoration, not help.
        foreach (var id in new[]
                 {
                     "opportunity.publish", "entry.withdraw", "entry.remove",
                     "opportunity.cancel", "register.role", "admin.newUser.type", "opportunity.delivery",
                 })
            Assert.False(string.IsNullOrWhiteSpace(HelpRegistry.Find(id)!.Why),
                $"Help topic '{id}' is for an irreversible action and must say why it matters.");
    }

    [Fact]
    public void Every_dashboard_metric_that_needs_help_has_a_topic()
    {
        // The dashboards restate the domain in numbers — claimed, provisioned,
        // handover, outstanding. A metric someone could misread into a wrong
        // decision must explain itself, and losing that topic must fail here
        // rather than quietly shipping a bare number.
        var missing = HelpRegistry.RequiredDashboardTopicIds
            .Where(id => HelpRegistry.Find(id) is null)
            .ToList();

        Assert.True(missing.Count == 0,
            "These dashboard metrics require help but have no topic in HelpRegistry:\n  "
            + string.Join("\n  ", missing));
    }

    [Fact]
    public void Dashboard_metrics_with_a_counterparty_explain_what_goes_wrong()
    {
        // Each of these has a person waiting behind it — an unpaid entrant, an
        // entrant who cannot start, a transfer that never landed, a board that
        // quietly stopped recording. A number without that context is decoration.
        foreach (var id in new[]
                 {
                     "dashboard.awardOutstanding", "dashboard.handoverStuck",
                     "dashboard.repoHealth", "dashboard.webhooks", "dashboard.topClients",
                 })
            Assert.False(string.IsNullOrWhiteSpace(HelpRegistry.Find(id)!.Why),
                $"Help topic '{id}' describes a metric with a counterparty and must say why it matters.");
    }

    [Fact]
    public void Every_admin_console_action_has_a_topic_that_says_why()
    {
        // Each console action restarts machinery acting on somebody else's
        // opportunity — a retry that emails an entrant, a transfer of paid-for
        // code, a replay that moves a public board. An unexplained button
        // there is how operators break things confidently.
        foreach (var id in HelpRegistry.RequiredAdminTopicIds)
        {
            var topic = HelpRegistry.Find(id);
            Assert.True(topic is not null,
                $"Admin console action '{id}' requires help but has no topic in HelpRegistry.");
            Assert.False(string.IsNullOrWhiteSpace(topic!.Why),
                $"Help topic '{id}' is for an admin action with consequences and must say why it matters.");
        }
    }
}
