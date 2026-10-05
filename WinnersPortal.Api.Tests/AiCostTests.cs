using WinnersPortal.Domain;
using WinnersPortal.Services.Ai;
using WinnersPortal.Services.Settings;
using Xunit;

namespace WinnersPortal.Api.Tests;

/// <summary>
/// Step 4 of the AI roadmap: what a call cost. The token count read off
/// each provider's answer, the price list an operator types and what it
/// turns counts into, the day rows the usage panel adds up, and the budget
/// that stops the day.
/// </summary>
public class AiCostTests
{
    // ------------------------------------------------------------ tokens

    [Fact]
    public void Each_providers_answer_yields_its_token_count_in_the_two_every_price_page_quotes()
    {
        Assert.Equal(new AiTokens(120, 45), AiProviderRequests.ReadTokens("gemini",
            "{\"candidates\":[],\"usageMetadata\":{\"promptTokenCount\":120,\"candidatesTokenCount\":30,\"thoughtsTokenCount\":15,\"totalTokenCount\":165}}"));
        // A cached prefix is still input, counted at full weight.
        Assert.Equal(new AiTokens(1000, 7), AiProviderRequests.ReadTokens("anthropic",
            "{\"content\":[],\"usage\":{\"input_tokens\":100,\"cache_creation_input_tokens\":400,\"cache_read_input_tokens\":500,\"output_tokens\":7}}"));
        Assert.Equal(new AiTokens(80, 20), AiProviderRequests.ReadTokens("openai",
            "{\"choices\":[],\"usage\":{\"prompt_tokens\":80,\"completion_tokens\":20,\"total_tokens\":100}}"));
    }

    [Fact]
    public void An_answer_with_no_count_reads_as_none_not_as_zero()
    {
        Assert.Null(AiProviderRequests.ReadTokens("gemini", "{\"candidates\":[]}"));
        Assert.Null(AiProviderRequests.ReadTokens("openai", "not json"));
        Assert.Null(AiProviderRequests.ReadTokens("other", "{\"usage\":{\"prompt_tokens\":1}}"));
        // A field that is there but not a number counts as nothing.
        Assert.Equal(new AiTokens(0, 3), AiProviderRequests.ReadTokens("openai", "{\"usage\":{\"prompt_tokens\":\"many\",\"completion_tokens\":3}}"));
    }

    [Fact]
    public void Tokens_add_up_and_print_as_the_row_and_the_report_show_them()
    {
        var sum = new AiTokens(1200, 34) + new AiTokens(34, 0);
        Assert.Equal(new AiTokens(1234, 34), sum);
        Assert.Equal(1268, sum.Total);
        Assert.Equal("1,234 in · 34 out", sum.ToString());
    }

    [Fact]
    public void A_feature_is_named_on_its_rows_the_way_its_switch_is()
    {
        Assert.Equal("entryDigest", AiPrompts.FeatureName(AiFeature.EntryDigest));
        Assert.Equal("entryDigest · prompt v2", AiPrompts.CallDetail(AiFeature.EntryDigest));
        Assert.Equal("settingsTest", AiPrompts.SettingsTestFeature);
    }

    // ------------------------------------------------------------ prices

    [Fact]
    public void A_price_list_is_one_model_a_line_however_the_parts_are_separated()
    {
        var prices = AiPrices.Parse("# the provider's page, 1 October\ngemini-3.6-flash 0.30 2.50\nclaude-sonnet-5 = 3, 15\n\ngpt-5.5\t$1.25\t$10\n");
        Assert.Equal(new AiPrice("gemini-3.6-flash", 0.30m, 2.50m), prices.Find("GEMINI-3.6-flash"));
        Assert.Equal(new AiPrice("claude-sonnet-5", 3m, 15m), prices.Find("claude-sonnet-5"));
        Assert.Equal(new AiPrice("gpt-5.5", 1.25m, 10m), prices.Find("gpt-5.5"));
        Assert.Null(prices.Find("gpt-4o"));
        Assert.Null(prices.Find(null));
        Assert.True(prices.Any);
        Assert.False(AiPrices.Parse(null).Any);
        Assert.False(AiPriceList.Empty.Any);
    }

    [Fact]
    public void A_count_times_a_price_is_dollars_per_million_and_an_unpriced_model_is_null_never_free()
    {
        var prices = AiPrices.Parse("gemini-3.6-flash 0.30 2.50");
        // 1,000,000 in at $0.30 and 100,000 out at $2.50.
        Assert.Equal(0.30m + 0.25m, prices.Cost("gemini-3.6-flash", new AiTokens(1_000_000, 100_000)));
        Assert.Equal(0m, prices.Cost("gemini-3.6-flash", AiTokens.None));
        Assert.Null(prices.Cost("gpt-5.5", new AiTokens(1, 1)));
        Assert.Equal("$0.55", AiPrices.Dollars(0.55m));
        Assert.Equal("$0.0003", AiPrices.Dollars(0.0003m));
        Assert.Equal("$1,234.50", AiPrices.Dollars(1234.5m));
    }

    [Theory]
    [InlineData(AiPrices.Key, "gemini-3.6-flash 0.30 2.50", null)]
    [InlineData(AiPrices.Key, "# notes only\n", null)]
    [InlineData(AiPrices.Key, "gemini-3.6-flash 0.30", "Line 1")]
    [InlineData(AiPrices.Key, "ok 1 2\nbad one two", "Line 2")]
    [InlineData(AiPrices.Key, "gemini-3.6-flash -1 2", "Line 1")]
    [InlineData(AiPrices.BudgetKey, "5", null)]
    [InlineData(AiPrices.BudgetKey, "$12.50", null)]
    [InlineData(AiPrices.BudgetKey, "0", null)]
    [InlineData(AiPrices.BudgetKey, "five", "spend budget")]
    [InlineData(AiPrices.BudgetKey, "-1", "spend budget")]
    public void A_price_line_that_cannot_be_read_is_refused_on_the_screen_rather_than_skipped(string key, string value, string? problem)
    {
        var def = SettingsRegistry.Find(key)!;
        var line = SettingsService.ValueProblem(def, value);
        if (problem is null) Assert.Null(line);
        else Assert.Contains(problem, line);
        Assert.Null(SettingsService.ValueProblem(def, ""));
    }

    [Fact]
    public void A_budget_is_dollars_and_blank_zero_or_nonsense_is_no_budget()
    {
        Assert.Equal(12.5m, AiPrices.ParseBudget(" $12.50 "));
        Assert.Null(AiPrices.ParseBudget(null));
        Assert.Null(AiPrices.ParseBudget("0"));
        Assert.Null(AiPrices.ParseBudget("lots"));
        Assert.Null(AiPrices.ParseBudget("-3"));
    }

    [Fact]
    public void The_prices_and_the_budget_sit_on_the_ai_tab_and_the_eval_switch_on_its_own_tab_off_by_default()
    {
        var prices = SettingsRegistry.Find(AiPrices.Key)!;
        Assert.Equal(("ai", true, null), (prices.Group, prices.IsMultiline, prices.Default));
        var budget = SettingsRegistry.Find(AiPrices.BudgetKey)!;
        Assert.Equal(("ai", null), (budget.Group, budget.Default));
        var enabled = SettingsRegistry.Find(EvalSettings.EnabledKey)!;
        Assert.Equal((EvalSettings.Group, true, "false"), (enabled.Group, enabled.IsBoolean, enabled.Default));
    }

    [Fact]
    public void The_budget_has_a_refusal_of_its_own()
    {
        Assert.Contains("spend budget", AiQuotaRules.Refusal(AiQuotaVerdict.Budget));
        Assert.NotEqual(AiQuotaRules.Refusal(AiQuotaVerdict.Portal), AiQuotaRules.Refusal(AiQuotaVerdict.Budget));
    }

    // ---------------------------------------------------------- the panel

    private static AiSpend Row(string day, string feature, string model, int calls, long input, long output, string provider = "gemini") =>
        new() { Day = day, Feature = feature, Provider = provider, Model = model, Calls = calls, InputTokens = input, OutputTokens = output };

    [Fact]
    public void The_panel_adds_a_month_of_day_rows_up_by_feature_and_by_model_and_prices_only_what_has_a_price()
    {
        var now = new DateTimeOffset(2026, 10, 1, 15, 0, 0, TimeSpan.Zero);
        var rows = new List<AiSpend>
        {
            Row("2026-10-01", "entryDigest", "gemini-3.6-flash", 2, 1_000_000, 100_000),
            Row("2026-10-01", "categorySuggestion", "gemini-3.6-flash", 5, 50_000, 5_000),
            Row("2026-09-30", "entryDigest", "gpt-5.5", 1, 10_000, 1_000, provider: "openai"),
            Row("2026-09-02", "settingsTest", "gemini-3.6-flash", 1, 10, 5),
        };
        var prices = AiPrices.Parse("gemini-3.6-flash 0.30 2.50");

        var panel = AiUsageReport.Build(rows, now, prices, dailyBudgetUsd: 5m, dailyCallLimit: 200);

        Assert.Equal("2026-09-02", panel.FromDay);
        Assert.Equal(new AiUsageTotals(7, 1_050_000, 105_000, 0.55m + 0.015m + 0.0125m), panel.Today);
        // The window's estimate leaves the unpriced model out, and says which.
        Assert.Equal(9, panel.Window.Calls);
        Assert.Equal(0.55m + 0.015m + 0.0125m + 0.000003m + 0.0000125m, panel.Window.CostUsd);
        Assert.Equal(["gpt-5.5"], panel.Unpriced);
        // By feature, busiest first; a feature answered by an unpriced model has no number.
        Assert.Equal(["categorySuggestion", "entryDigest", "settingsTest"], panel.ByFeature.Select(l => l.Name));
        Assert.Null(panel.ByFeature.Single(l => l.Name == "entryDigest").CostUsd);
        Assert.Equal(0.015m + 0.0125m, panel.ByFeature.Single(l => l.Name == "categorySuggestion").CostUsd);
        Assert.Equal(["gemini/gemini-3.6-flash", "openai/gpt-5.5"], panel.ByModel.Select(l => l.Name));
        Assert.Null(panel.ByModel.Single(l => l.Name == "openai/gpt-5.5").CostUsd);
        Assert.Equal((true, 5m, 200), (panel.PricesSet, panel.DailyBudgetUsd, panel.DailyCallLimit));

        // With nothing priced there is no estimate at all, rather than zero.
        var unpriced = AiUsageReport.Build(rows, now, AiPriceList.Empty, null, 200);
        Assert.Null(unpriced.Today.CostUsd);
        Assert.Null(unpriced.Window.CostUsd);
        Assert.False(unpriced.PricesSet);
    }

    [Fact]
    public void Todays_estimate_is_what_the_budget_is_measured_against()
    {
        var rows = new List<AiSpend>
        {
            Row("2026-10-01", "entryDigest", "gemini-3.6-flash", 1, 1_000_000, 0),
            Row("2026-09-30", "entryDigest", "gemini-3.6-flash", 1, 1_000_000, 0),
            Row("2026-10-01", "entryDigest", "gpt-5.5", 1, 1_000_000, 0, provider: "openai"),
        };
        Assert.Equal(0.30m, AiUsageReport.CostToday(rows, "2026-10-01", AiPrices.Parse("gemini-3.6-flash 0.30 2.50")));
        Assert.Equal(0m, AiUsageReport.CostToday(rows, "2026-10-01", AiPriceList.Empty));
    }

    [Fact]
    public void A_rows_day_window_starts_twenty_nine_days_back()
    {
        Assert.Equal("2026-09-02", AiUsageReport.FromDay(new DateTimeOffset(2026, 10, 1, 0, 30, 0, TimeSpan.Zero)));
    }
}
