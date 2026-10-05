using System.Text;
using System.Text.Json;
using WinnersPortal.Domain;
using WinnersPortal.Services.Opportunities;
using WinnersPortal.Services.Identity;
using WinnersPortal.Services.Settings;
using Xunit;

namespace WinnersPortal.Api.Tests;

/// <summary>
/// Identity verification's pure half: the signature the webhook is trusted
/// on, the provider's words folded to the portal's statuses, and the three
/// doors' rules — so the form's eligibility line and the refusal can never
/// disagree.
/// </summary>
public class IdentityTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    private static string Stamp(DateTimeOffset at) => at.ToUnixTimeSeconds().ToString();

    private static readonly IdentityProviderConfig Didit = new("didit", "sk-secret", "wf-1", "shh");

    // ------------------------------------------------------------ signature

    [Fact]
    public void A_delivery_signed_with_the_secret_and_stamped_now_verifies()
    {
        var body = Encoding.UTF8.GetBytes("{\"event_id\":\"e1\",\"status\":\"Approved\"}");
        var signature = IdentityWebhookSignature.Compute("shh", body);
        Assert.True(IdentityWebhookSignature.Verify("shh", body, signature, Stamp(Now), Now));
    }

    [Fact]
    public void A_wrong_secret_a_changed_body_or_a_malformed_header_is_refused_not_crashed()
    {
        var body = Encoding.UTF8.GetBytes("{\"status\":\"Approved\"}");
        var signature = IdentityWebhookSignature.Compute("shh", body);
        Assert.False(IdentityWebhookSignature.Verify("other", body, signature, Stamp(Now), Now));
        Assert.False(IdentityWebhookSignature.Verify("shh", Encoding.UTF8.GetBytes("{\"status\":\"Declined\"}"), signature, Stamp(Now), Now));
        Assert.False(IdentityWebhookSignature.Verify("shh", body, "not-hex-at-all", Stamp(Now), Now));
        Assert.False(IdentityWebhookSignature.Verify("shh", body, null, Stamp(Now), Now));
        Assert.False(IdentityWebhookSignature.Verify("", body, signature, Stamp(Now), Now));
    }

    [Fact]
    public void A_stale_or_missing_timestamp_refuses_a_correctly_signed_delivery()
    {
        // The provider's own replay rule: five minutes either way.
        var body = Encoding.UTF8.GetBytes("{}");
        var signature = IdentityWebhookSignature.Compute("shh", body);
        Assert.True(IdentityWebhookSignature.Verify("shh", body, signature, Stamp(Now.AddMinutes(-4)), Now));
        Assert.True(IdentityWebhookSignature.Verify("shh", body, signature, Stamp(Now.AddMinutes(4)), Now));
        Assert.False(IdentityWebhookSignature.Verify("shh", body, signature, Stamp(Now.AddMinutes(-6)), Now));
        Assert.False(IdentityWebhookSignature.Verify("shh", body, signature, Stamp(Now.AddMinutes(6)), Now));
        Assert.False(IdentityWebhookSignature.Verify("shh", body, signature, null, Now));
        Assert.False(IdentityWebhookSignature.Verify("shh", body, signature, "yesterday", Now));
    }

    // -------------------------------------------------------------- statuses

    [Theory]
    [InlineData("Not Started", IdentityStatus.NotStarted)]
    [InlineData("In Progress", IdentityStatus.InProgress)]
    [InlineData("Resubmitted", IdentityStatus.InProgress)]
    [InlineData("Awaiting User", IdentityStatus.InProgress)]
    [InlineData("In Review", IdentityStatus.InReview)]
    [InlineData("Approved", IdentityStatus.Approved)]
    [InlineData("Declined", IdentityStatus.Declined)]
    [InlineData("Abandoned", IdentityStatus.Abandoned)]
    [InlineData("Expired", IdentityStatus.Expired)]
    [InlineData("Kyc Expired", IdentityStatus.Expired)]
    [InlineData("Something New", IdentityStatus.InProgress)]
    public void The_providers_words_fold_to_the_portals_statuses(string word, IdentityStatus expected) =>
        Assert.Equal(expected, IdentityRules.MapStatus(word));

    [Fact]
    public void Only_a_verdict_is_final_and_only_a_non_approval_may_be_started_over()
    {
        Assert.True(IdentityRules.IsFinal(IdentityStatus.Approved));
        Assert.True(IdentityRules.IsFinal(IdentityStatus.Declined));
        Assert.False(IdentityRules.IsFinal(IdentityStatus.InReview));
        Assert.False(IdentityRules.IsFinal(IdentityStatus.InProgress));

        Assert.True(IdentityRules.MayStart(null, canResume: false));
        Assert.True(IdentityRules.MayStart(IdentityStatus.Declined, canResume: false));
        Assert.True(IdentityRules.MayStart(IdentityStatus.Expired, canResume: false));
        Assert.True(IdentityRules.MayStart(IdentityStatus.Abandoned, canResume: false));
        Assert.False(IdentityRules.MayStart(IdentityStatus.Approved, canResume: false));
        Assert.False(IdentityRules.MayStart(IdentityStatus.InReview, canResume: false));
    }

    [Fact]
    public void An_open_session_is_picked_up_by_its_link_and_started_over_only_without_one()
    {
        Assert.True(IdentityRules.IsOpen(IdentityStatus.NotStarted));
        Assert.True(IdentityRules.IsOpen(IdentityStatus.InProgress));
        Assert.False(IdentityRules.IsOpen(IdentityStatus.InReview));
        Assert.False(IdentityRules.IsOpen(IdentityStatus.Expired));
        Assert.False(IdentityRules.IsOpen(IdentityStatus.Abandoned));
        Assert.False(IdentityRules.IsOpen(IdentityStatus.Approved));
        Assert.False(IdentityRules.IsOpen(null));

        // With its link kept, an open session is handed back, never replaced;
        // one opened before links were kept is replaced, never refused.
        Assert.False(IdentityRules.MayStart(IdentityStatus.InProgress, canResume: true));
        Assert.False(IdentityRules.MayStart(IdentityStatus.NotStarted, canResume: true));
        Assert.True(IdentityRules.MayStart(IdentityStatus.InProgress, canResume: false));
    }

    [Fact]
    public void Only_an_open_session_is_asked_about_again_and_not_more_than_every_quarter_hour()
    {
        Assert.False(IdentityRules.RecheckDue(IdentityStatus.InProgress, Now.AddMinutes(-14), Now));
        Assert.True(IdentityRules.RecheckDue(IdentityStatus.InProgress, Now.AddMinutes(-15), Now));
        Assert.True(IdentityRules.RecheckDue(IdentityStatus.NotStarted, Now.AddDays(-2), Now));
        Assert.False(IdentityRules.RecheckDue(IdentityStatus.InReview, Now.AddDays(-2), Now));
        Assert.False(IdentityRules.RecheckDue(IdentityStatus.Approved, Now.AddDays(-2), Now));
    }

    [Theory]
    [InlineData("/opportunities/logo-refresh/apply?step=eligibility", "/opportunities/logo-refresh/apply?step=eligibility")]
    [InlineData("  /profile  ", "/profile")]
    [InlineData("/", "/")]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("profile", null)]
    [InlineData("//evil.example/x", null)]
    [InlineData("/\\evil.example", null)]
    [InlineData("https://evil.example/", null)]
    [InlineData("/a\nb", null)]
    public void The_way_back_is_a_path_on_this_portal_or_nothing(string? given, string? kept) =>
        Assert.Equal(kept, IdentityRules.SafeReturn(given));

    [Fact]
    public void A_way_back_longer_than_its_column_is_no_way_back()
    {
        Assert.Null(IdentityRules.SafeReturn("/" + new string('a', 512)));
        Assert.NotNull(IdentityRules.SafeReturn("/" + new string('a', 511)));
    }

    [Fact]
    public void The_web_reads_five_words_and_a_lapsed_session_reads_as_none()
    {
        Assert.Equal("none", IdentityRules.StatusName(null));
        Assert.Equal("none", IdentityRules.StatusName(IdentityStatus.Abandoned));
        Assert.Equal("pending", IdentityRules.StatusName(IdentityStatus.InProgress));
        Assert.Equal("in_review", IdentityRules.StatusName(IdentityStatus.InReview));
        Assert.Equal("approved", IdentityRules.StatusName(IdentityStatus.Approved));
        Assert.Equal("declined", IdentityRules.StatusName(IdentityStatus.Declined));
    }

    // ----------------------------------------------------------------- doors

    [Fact]
    public void A_door_asks_only_when_the_feature_and_its_switch_are_on_and_the_member_is_not_verified()
    {
        Assert.True(IdentityRules.PublishOwed(enabled: true, required: true, verified: false));
        Assert.False(IdentityRules.PublishOwed(enabled: false, required: true, verified: false));
        Assert.False(IdentityRules.PublishOwed(enabled: true, required: false, verified: false));
        Assert.False(IdentityRules.PublishOwed(enabled: true, required: true, verified: true));
    }

    [Fact]
    public void Payment_details_are_withheld_from_a_client_alone_and_only_until_the_member_verifies()
    {
        // The member, an administrator and another freelancer are never
        // "withheld": the first two always read, the third never did.
        Assert.True(IdentityRules.PaymentsWithheld(true, true, own: false, Roles.Client, ownerVerified: false));
        Assert.False(IdentityRules.PaymentsWithheld(true, true, own: false, Roles.Client, ownerVerified: true));
        Assert.False(IdentityRules.PaymentsWithheld(true, true, own: true, Roles.Freelancer, ownerVerified: false));
        Assert.False(IdentityRules.PaymentsWithheld(true, true, own: false, Roles.Admin, ownerVerified: false));
        Assert.False(IdentityRules.PaymentsWithheld(true, true, own: false, Roles.Freelancer, ownerVerified: false));
        Assert.False(IdentityRules.PaymentsWithheld(false, true, own: false, Roles.Client, ownerVerified: false));
        Assert.False(IdentityRules.PaymentsWithheld(true, false, own: false, Roles.Client, ownerVerified: false));
    }

    [Fact]
    public void The_apply_door_shuts_before_the_clients_terms_and_says_where_to_go()
    {
        // An opportunity that is not open still says so first; past that, the
        // portal's door comes before the merit floor.
        Assert.Equal(IdentityRules.ApplyProblem,
            ApplicationRules.SubmitProblem(OpportunityStatus.Open, true, null, false, false, false, identityOwed: true));
        Assert.Contains("no longer open",
            ApplicationRules.SubmitProblem(OpportunityStatus.Reviewing, true, null, false, false, false, identityOwed: true));
        Assert.Null(ApplicationRules.SubmitProblem(OpportunityStatus.Open, true, null, false, false, false, identityOwed: false));
        Assert.Contains("two minutes", IdentityRules.ApplyProblem);
        Assert.Contains("two minutes", IdentityRules.PublishProblem);
    }

    // -------------------------------------------------------------- the wire

    [Fact]
    public void A_session_is_opened_with_the_key_in_a_header_and_the_member_named_by_id()
    {
        var userId = Guid.NewGuid();
        var request = IdentityProviderRequests.CreateSession(
            Didit, new(userId, "wp-ref", "https://portal.example/verify/done", "https://api.example/hook", "a@b.c", null));
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://verification.didit.me/v3/session/", request.Url);
        Assert.Equal("sk-secret", request.Headers["x-api-key"]);
        Assert.DoesNotContain("sk-secret", request.Url);
        using var doc = JsonDocument.Parse(request.BodyJson!);
        Assert.Equal("wf-1", doc.RootElement.GetProperty("workflow_id").GetString());
        Assert.Equal(userId.ToString(), doc.RootElement.GetProperty("vendor_data").GetString());
        Assert.Equal("https://portal.example/verify/done", doc.RootElement.GetProperty("callback").GetString());
        // The browser the member started in is the one sent back.
        Assert.Equal("initiator", doc.RootElement.GetProperty("callback_method").GetString());
        Assert.Equal("a@b.c", doc.RootElement.GetProperty("contact_details").GetProperty("email").GetString());
        // Nothing off the profile travels: no name, no phone, no document.
        Assert.False(doc.RootElement.TryGetProperty("expected_details", out _));
    }

    [Fact]
    public void A_decision_is_read_by_session_with_the_key_in_a_header()
    {
        var request = IdentityProviderRequests.ReadDecision(Didit, "s-1");
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://verification.didit.me/v3/session/s-1/decision/", request.Url);
        Assert.Equal("sk-secret", request.Headers["x-api-key"]);
        Assert.Null(request.BodyJson);
    }

    [Fact]
    public void The_providers_answers_parse_and_a_decline_keeps_only_the_reason_category()
    {
        var session = IdentityProviderRequests.ParseSession("didit",
            "{\"session_id\":\"s-1\",\"url\":\"https://verify.didit.me/s-1\",\"status\":\"Not Started\"}");
        Assert.Equal(("s-1", "https://verify.didit.me/s-1", "Not Started"), (session.SessionId, session.Url, session.Status));
        Assert.Throws<IdentityProviderException>(() => IdentityProviderRequests.ParseSession("didit", "{\"status\":\"x\"}"));

        var declined = IdentityProviderRequests.ParseDecision("didit",
            "{\"status\":\"Declined\",\"decision\":{\"id_verification\":{\"status\":\"Declined\",\"first_name\":\"Ada\",\"document_number\":\"X1\"},\"liveness\":{\"status\":\"Approved\"}}}");
        Assert.Equal("Declined", declined.Status);
        Assert.Equal("id_verification: Declined", declined.Reason);
        Assert.DoesNotContain("Ada", declined.Reason);

        var approved = IdentityProviderRequests.ParseDecision("didit",
            "{\"status\":\"Approved\",\"decision\":{\"id_verification\":{\"status\":\"Approved\"}}}");
        Assert.Null(approved.Reason);
    }

    [Fact]
    public void An_unknown_provider_is_a_named_error()
    {
        var e = Assert.Throws<IdentityProviderException>(() =>
            IdentityProviderRequests.ReadDecision(Didit with { Provider = "onfido" }, "s"));
        Assert.Contains("onfido", e.Message);
        Assert.Contains("didit", e.Message);
        Assert.Contains("shuftipro", e.Message);
    }

    [Theory]
    [InlineData(401, "key")]
    [InlineData(404, "workflow")]
    [InlineData(429, "few minutes")]
    [InlineData(503, "not available")]
    [InlineData(0, "try again")]
    public void A_refusal_reaches_a_member_as_a_line_they_can_act_on(int status, string says) =>
        Assert.Contains(says, new IdentityProviderException(status, "provider words").Friendly);

    // -------------------------------------------------------------- settings

    [Fact]
    public void An_identity_setup_without_a_key_is_not_a_provider_and_a_blank_provider_is_didit()
    {
        Assert.Null(IdentityOptions.Config(new SetupValues("s", "Main", true,
            new Dictionary<string, string?> { [IdentityKeys.Provider] = "didit", [IdentityKeys.WorkflowId] = "w" })));
        var config = IdentityOptions.Config(new SetupValues("s", "Backup", true, new Dictionary<string, string?>
        {
            [IdentityKeys.ApiKey] = "k", [IdentityKeys.WorkflowId] = " w ", [IdentityKeys.WebhookSecret] = "shh",
        }));
        Assert.NotNull(config);
        Assert.Equal(("didit", "k", "w", "shh", "Backup"),
            (config!.Provider, config.ApiKey, config.WorkflowId, config.WebhookSecret, config.Setup));
    }

    [Fact]
    public void The_identity_group_is_a_setup_kind_whose_fields_are_all_registered_and_helped()
    {
        Assert.Contains(Setups.Kinds, k => k == Setups.Identity);
        foreach (var field in Setups.Identity.Fields)
        {
            var def = SettingsRegistry.Find(field);
            Assert.NotNull(def);
            Assert.Equal("identity", def!.Group);
            Assert.True(def.HelpRequired);
        }
        Assert.Equal("false", SettingsRegistry.Find(IdentityKeys.Enabled)!.Default);
        Assert.Equal("true", SettingsRegistry.Find(IdentityKeys.RequireForPublish)!.Default);
    }
}
