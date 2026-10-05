using System.Text;
using System.Text.Json;
using WinnersPortal.Domain;
using WinnersPortal.Services.Activity;
using WinnersPortal.Services.Identity;
using WinnersPortal.Services.Settings;
using Xunit;

namespace WinnersPortal.Api.Tests;

/// <summary>
/// Shufti Pro, the second verification provider: its wire format, its
/// events folded to the portal's statuses, its callback signature, and the
/// proof and log rows it leaves — all pinned without a network, since no
/// real Shufti Pro account has been used.
/// </summary>
public class ShuftiProTests
{
    private static readonly IdentityProviderConfig Shufti = new("shuftipro", "secret-key-1", "", null, "Backup", "client-9");

    // -------------------------------------------------------------- the wire

    [Fact]
    public void A_request_signs_in_with_basic_auth_and_names_its_callback_and_way_back()
    {
        var userId = Guid.NewGuid();
        var reference = IdentityProviderRequests.NewReference(userId);
        var request = IdentityProviderRequests.CreateSession(Shufti, new(
            userId, reference, "https://portal.example/verify/done", "https://api.example/api/webhooks/identity/shufti", "a@b.c", null));

        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.shuftipro.com/", request.Url);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("client-9:secret-key-1")), request.Headers["Authorization"]);
        Assert.DoesNotContain("secret-key-1", request.Url);
        Assert.DoesNotContain("secret-key-1", request.BodyJson);

        using var doc = JsonDocument.Parse(request.BodyJson!);
        var root = doc.RootElement;
        Assert.Equal(reference, root.GetProperty("reference").GetString());
        Assert.Equal("https://api.example/api/webhooks/identity/shufti", root.GetProperty("callback_url").GetString());
        Assert.Equal("https://portal.example/verify/done", root.GetProperty("redirect_url").GetString());
        Assert.Equal("a@b.c", root.GetProperty("email").GetString());
        Assert.Equal("EN", root.GetProperty("language").GetString());
        // A document read by OCR and a selfie; nothing off the profile travels.
        Assert.Equal("", root.GetProperty("document").GetProperty("name").GetString());
        Assert.Equal(3, root.GetProperty("document").GetProperty("supported_types").GetArrayLength());
        Assert.True(root.TryGetProperty("face", out _));
    }

    [Fact]
    public void A_status_is_read_by_reference_with_the_same_sign_in()
    {
        var request = IdentityProviderRequests.ReadDecision(Shufti, "wp-ref");
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.shuftipro.com/status", request.Url);
        Assert.StartsWith("Basic ", request.Headers["Authorization"]);
        using var doc = JsonDocument.Parse(request.BodyJson!);
        Assert.Equal("wp-ref", doc.RootElement.GetProperty("reference").GetString());
    }

    [Fact]
    public void A_reference_names_its_member_and_is_never_reused()
    {
        var userId = Guid.NewGuid();
        var a = IdentityProviderRequests.NewReference(userId);
        var b = IdentityProviderRequests.NewReference(userId);
        Assert.NotEqual(a, b);
        Assert.Equal(44, a.Length);
        Assert.StartsWith("wp-", a);
        Assert.Equal(userId, IdentityProviderRequests.UserOfReference(a));
        Assert.Null(IdentityProviderRequests.UserOfReference("17374217"));
        Assert.Null(IdentityProviderRequests.UserOfReference(null));
    }

    [Fact]
    public void Its_answers_parse_and_a_refusal_names_the_event()
    {
        var session = IdentityProviderRequests.ParseSession("shuftipro",
            """{"reference":"wp-1","event":"request.pending","verification_url":"https://app.shuftipro.com/process/verification/abc"}""");
        Assert.Equal(("wp-1", "https://app.shuftipro.com/process/verification/abc", "request.pending"),
            (session.SessionId, session.Url, session.Status));

        var refused = Assert.Throws<IdentityProviderException>(() => IdentityProviderRequests.ParseSession("shuftipro",
            """{"reference":"wp-1","event":"request.invalid","error":{"service":"document","key":"dob","message":"The dob does not match the format Y-m-d."}}"""));
        Assert.Contains("request.invalid", refused.Message);
        Assert.Contains("dob", refused.Message);

        var declined = IdentityProviderRequests.ParseDecision("shuftipro",
            """{"reference":"wp-1","event":"verification.declined","declined_reason":"Document is expired","verification_data":{"document":{"name":{"first_name":"Ada"}}}}""");
        Assert.Equal(("verification.declined", "Document is expired"), (declined.Status, declined.Reason));

        var accepted = IdentityProviderRequests.ParseDecision("shuftipro",
            """{"reference":"wp-1","event":"verification.accepted","declined_reason":null}""");
        Assert.Null(accepted.Reason);
    }

    [Fact]
    public void Its_error_body_is_dug_out_for_the_log()
    {
        Assert.Equal("The provider answered 401: Authorization keys are missing/invalid.",
            IdentityProviderRequests.ErrorDetail(401,
                """{"reference":"","event":"request.unauthorized","error":{"service":"","key":"","message":"Authorization keys are missing/invalid."}}"""));
    }

    // ------------------------------------------------------------ the events

    [Theory]
    [InlineData("request.pending", IdentityStatus.NotStarted)]
    [InlineData("request.received", IdentityStatus.InProgress)]
    [InlineData("review.pending", IdentityStatus.InReview)]
    [InlineData("verification.accepted", IdentityStatus.Approved)]
    [InlineData("verification.declined", IdentityStatus.Declined)]
    [InlineData("verification.cancelled", IdentityStatus.Abandoned)]
    [InlineData("request.timeout", IdentityStatus.Expired)]
    public void Its_events_fold_to_the_portals_statuses(string word, IdentityStatus expected) =>
        Assert.Equal(expected, IdentityRules.MapStatus("shuftipro", word));

    [Fact]
    public void Didits_words_are_still_didits()
    {
        Assert.Equal(IdentityStatus.Approved, IdentityRules.MapStatus("didit", "Approved"));
        // A Shufti Pro word means nothing to Didit: in progress, like any unknown word.
        Assert.Equal(IdentityStatus.InProgress, IdentityRules.MapStatus("didit", "verification.accepted"));
    }

    [Fact]
    public void Events_without_a_verdict_are_read_back_or_left()
    {
        Assert.True(ShuftiEvents.ReadBack("verification.status.changed"));
        Assert.True(ShuftiEvents.ReadBack("request.data.changed"));
        Assert.False(ShuftiEvents.ReadBack("verification.accepted"));
        Assert.True(ShuftiEvents.Ignored("request.deleted"));
        Assert.False(ShuftiEvents.Ignored("verification.declined"));
    }

    // ------------------------------------------------------------ signature

    private static readonly byte[] Body = Encoding.UTF8.GetBytes("""{"reference":"wp-1","event":"verification.accepted"}""");

    [Fact]
    public void A_callback_signed_with_the_secret_keys_hash_verifies()
    {
        var header = ShuftiSignature.Compute("secret-key-1", Body);
        Assert.True(ShuftiSignature.Verify("secret-key-1", Body, header));
        Assert.True(ShuftiSignature.Verify("secret-key-1", Body, header.ToUpperInvariant()));
    }

    [Fact]
    public void A_callback_signed_the_pre_2023_way_verifies_too()
    {
        // sha256(body + secret key), for accounts that kept an older key.
        var legacy = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            [.. Body, .. Encoding.UTF8.GetBytes("secret-key-1")])).ToLowerInvariant();
        Assert.True(ShuftiSignature.Verify("secret-key-1", Body, legacy));
    }

    [Fact]
    public void A_forged_or_altered_callback_is_refused()
    {
        var header = ShuftiSignature.Compute("secret-key-1", Body);
        Assert.False(ShuftiSignature.Verify("another-key", Body, header));
        Assert.False(ShuftiSignature.Verify("secret-key-1", Encoding.UTF8.GetBytes("""{"reference":"wp-1","event":"verification.declined"}"""), header));
        Assert.False(ShuftiSignature.Verify("secret-key-1", Body, "not-hex-at-all-" + new string('z', 49)));
        Assert.False(ShuftiSignature.Verify("secret-key-1", Body, null));
        Assert.False(ShuftiSignature.Verify("", Body, header));
    }

    [Fact]
    public void A_callback_is_one_delivery_however_often_it_comes()
    {
        var id = ShuftiSignature.DeliveryId(Body);
        Assert.Equal(id, ShuftiSignature.DeliveryId(Body));
        Assert.StartsWith("shufti:", id);
        Assert.True(id.Length <= 64);
        Assert.NotEqual(id, ShuftiSignature.DeliveryId(Encoding.UTF8.GetBytes("""{"reference":"wp-1","event":"request.pending"}""")));
    }

    // ------------------------------------------------------------- the test

    [Theory]
    [InlineData("shuftipro", 401, "{\"event\":\"request.unauthorized\"}", false, "client ID and secret key")]
    [InlineData("shuftipro", 200, "{\"event\":\"request.unauthorized\"}", false, "rejected")]
    [InlineData("shuftipro", 400, "{\"event\":\"request.invalid\"}", true, "accepted")]
    [InlineData("shuftipro", 404, "{}", true, "no such request")]
    [InlineData("shuftipro", 429, "{}", false, "rate-limiting")]
    [InlineData("shuftipro", 503, "", false, "not available")]
    [InlineData("didit", 404, "{}", true, "accepted the key")]
    [InlineData("didit", 401, "{}", false, "rejected the key")]
    [InlineData("didit", 400, "{}", false, "answered 400")]
    public void The_settings_test_reads_each_providers_probe(string provider, int status, string body, bool ok, string says)
    {
        var (passed, detail) = IdentityRules.ProbeAnswer(provider, IdentityProviders.LabelOf(provider), "Main", status, body);
        Assert.Equal(ok, passed);
        Assert.Contains(says, detail);
    }

    // ------------------------------------------------------------- settings

    [Fact]
    public void A_setup_carries_its_client_id_and_says_what_it_still_needs()
    {
        var config = IdentityOptions.Config(new SetupValues("s1", "Backup", true, new Dictionary<string, string?>
        {
            [IdentityKeys.Provider] = "ShuftiPro", [IdentityKeys.ApiKey] = "secret", [IdentityKeys.ClientId] = " client-9 ",
        }));
        Assert.NotNull(config);
        Assert.Equal(("shuftipro", "secret", "client-9"), (config!.Provider, config.ApiKey, config.ClientId));
        Assert.Null(IdentityOptions.MissingForStart(config));
        Assert.Equal("client ID", IdentityOptions.MissingForStart(config with { ClientId = "" }));
        Assert.Equal("workflow ID", IdentityOptions.MissingForStart(new IdentityProviderConfig("didit", "k", "", "s")));
    }

    [Fact]
    public void Each_provider_shows_the_fields_it_reads_and_every_one_is_a_registered_identity_field()
    {
        Assert.Equal(["didit", "shuftipro"], IdentityProviders.All.Select(p => p.Key));
        foreach (var (provider, fields) in IdentityProviders.FieldsOf)
        {
            Assert.NotNull(IdentityProviders.Find(provider));
            foreach (var field in fields) Assert.Contains(field, Setups.Identity.Fields);
        }
        Assert.DoesNotContain(IdentityKeys.WebhookSecret, IdentityProviders.FieldsOf["shuftipro"]);
        Assert.DoesNotContain(IdentityKeys.ClientId, IdentityProviders.FieldsOf["didit"]);
        // Added after identity setups were tested, so passed tests stay passed.
        Assert.Contains(IdentityKeys.ClientId, Setups.Identity.Later!);
        Assert.Contains(SettingsRegistry.Find(IdentityKeys.Provider)!.Choices!, c => c.Value == "shuftipro");
    }

    // ---------------------------------------------------------------- proof

    private const string Decision = """
        {
          "reference": "wp-1", "event": "verification.accepted", "country": "GB",
          "verification_result": { "document": { "document": 1, "document_visibility": 1 }, "face": 1 },
          "verification_data": { "document": {
            "name": { "first_name": "Ada", "last_name": "Lovelace" },
            "dob": "1990-01-02", "document_number": "X1234", "expiry_date": "2030-01-01",
            "selected_type": ["passport"] } },
          "proofs": {
            "document": { "proof": "https://ns.shuftipro.com/api/pea/doc1", "proof_heatmap": "https://ns.shuftipro.com/api/pea/heat" },
            "face": { "proof": "https://ns.shuftipro.com/api/pea/face1", "verification_video": "https://ns.shuftipro.com/api/pea/vid" },
            "access_token": "tok-123",
            "verification_report": "https://ns.shuftipro.com/api/pea/report"
          }
        }
        """;

    [Fact]
    public void The_proof_images_are_the_proofs_with_the_access_token_and_nothing_else()
    {
        var images = IdentityProof.Images("shuftipro", Decision);
        Assert.Equal(
            [("proofs.document.proof", "https://ns.shuftipro.com/api/pea/doc1"), ("proofs.face.proof", "https://ns.shuftipro.com/api/pea/face1")],
            images.Select(i => (i.Name, i.Url)));
        Assert.All(images, i => Assert.Equal("tok-123", i.AccessToken));
        Assert.Equal("Document · Proof", IdentityProof.Label(images[0].Name));
        // Didit's finder reads names that say image, and finds none here.
        Assert.Empty(IdentityProof.Images("didit", Decision));
        Assert.Empty(IdentityProof.Images("shuftipro", "not json"));
    }

    [Fact]
    public void The_summary_reads_the_document_and_the_verdicts()
    {
        var facts = IdentityProof.Summary("shuftipro", Decision).ToDictionary(f => f.Label, f => f.Value);
        Assert.Equal("Ada Lovelace", facts["Name"]);
        Assert.Equal("passport", facts["Document"]);
        Assert.Equal("X1234", facts["Document number"]);
        Assert.Equal("1990-01-02", facts["Date of birth"]);
        Assert.Equal("GB", facts["Issued by"]);
        Assert.Equal("Accepted", facts["Document check"]);
        Assert.Equal("Accepted", facts["Face match"]);
        Assert.False(facts.ContainsKey("Declined because"));
    }

    // ------------------------------------------------------------ the log

    [Fact]
    public void Its_callbacks_and_calls_are_named_in_the_activity_log()
    {
        const string path = "/api/webhooks/identity/shufti";
        Assert.Equal(ExternalServices.Identity, ExternalWebhooks.ServiceOf(path));
        Assert.Equal("Webhook from Shufti Pro", ExternalWebhooks.Action(ExternalServices.Identity, path));
        Assert.Equal("Webhook from Didit", ExternalWebhooks.Action(ExternalServices.Identity, "/api/webhooks/identity"));
        Assert.Equal("verification.accepted", ExternalWebhooks.Subject(ExternalServices.Identity, null, Decision));

        var userId = Guid.NewGuid();
        var body = $$"""{"reference":"{{IdentityProviderRequests.NewReference(userId)}}","event":"request.pending"}""";
        Assert.Equal(userId, ExternalWebhooks.UserOf(ExternalServices.Identity, body));
        Assert.Equal("Shufti Pro", ExternalExchange.Vendor("api.shuftipro.com"));
        Assert.Equal("Shufti Pro", ExternalExchange.Vendor("ns.shuftipro.com"));
    }
}
