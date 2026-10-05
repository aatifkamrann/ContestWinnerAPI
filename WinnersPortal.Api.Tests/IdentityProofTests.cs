using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WinnersPortal.Api.Activity;
using WinnersPortal.Domain;
using WinnersPortal.Services.Activity;
using WinnersPortal.Services.Identity;
using Xunit;

namespace WinnersPortal.Api.Tests;

/// <summary>
/// The proof kept behind an identity verdict — which images a decision
/// links to, what they are called, where they are stored, what an
/// administrator reads first, when a copy is queued — and the incoming
/// webhooks recorded beside the outgoing calls.
/// </summary>
public class IdentityProofTests
{
    // A decision shaped the way the provider answers: the document check,
    // the liveness check and the face match, each with its own images and
    // a video that is left as a link.
    private const string Decision = """
        {
          "session_id": "11111111-2222-3333-4444-555555555555",
          "status": "Approved",
          "vendor_data": "7d3c1e7a-0000-4000-8000-000000000001",
          "id_verification": {
            "status": "Approved",
            "document_type": "Identity Card",
            "document_number": "X1234567",
            "first_name": "Jane",
            "last_name": "Doe",
            "date_of_birth": "1990-01-01",
            "nationality": "PAK",
            "expiration_date": "2031-05-01",
            "portrait_image": "https://media.example.test/portrait.jpg?sig=a",
            "front_image": "https://media.example.test/front.jpg?sig=b",
            "back_image": "https://media.example.test/back.jpg?sig=c",
            "front_video": "https://media.example.test/front.mp4?sig=d",
            "extra_images": ["https://media.example.test/extra.jpg?sig=e"]
          },
          "liveness": {
            "status": "Approved",
            "score": 92.5,
            "reference_image": "https://media.example.test/selfie.jpg?sig=f",
            "video_url": "https://media.example.test/selfie.mp4?sig=g"
          },
          "face_match": {
            "status": "Approved",
            "score": 97,
            "source_image": "https://media.example.test/selfie.jpg?sig=f",
            "target_image": "https://media.example.test/portrait-2.jpg?sig=h"
          }
        }
        """;

    [Fact]
    public void Every_image_in_a_decision_is_found_once_and_videos_are_left_as_links()
    {
        var images = IdentityProof.Images(Decision);

        Assert.Equal(
            [
                "id_verification.portrait_image",
                "id_verification.front_image",
                "id_verification.back_image",
                "id_verification.extra_images[0]",
                "liveness.reference_image",
                // face_match.source_image is the same selfie as the liveness one, copied once
                "face_match.target_image",
            ],
            images.Select(i => i.Name));
        Assert.DoesNotContain(images, i => i.Url.Contains(".mp4"));
    }

    [Fact]
    public void A_decision_that_is_not_json_or_has_no_images_gives_none()
    {
        Assert.Empty(IdentityProof.Images("not json"));
        Assert.Empty(IdentityProof.Images("""{"status":"Declined","id_verification":{"status":"Declined"}}"""));
        Assert.Empty(IdentityProof.Images(null));
    }

    [Theory]
    [InlineData("id_verification.front_image", "ID verification · Front image")]
    [InlineData("liveness.reference_image", "Liveness · Reference image")]
    [InlineData("id_verifications[0].back_image", "ID verifications · Back image")]
    [InlineData("id_verification.extra_images[0]", "ID verification · Extra images")]
    public void An_image_reads_by_where_it_sat(string name, string label) =>
        Assert.Equal(label, IdentityProof.Label(name));

    [Fact]
    public void A_copy_is_stored_per_member_and_verification_numbered_in_order()
    {
        var user = Guid.Parse("7d3c1e7a-0000-4000-8000-000000000001");
        var verification = Guid.Parse("0f000000-0000-4000-8000-00000000000a");
        Assert.Equal(
            "identity/7d3c1e7a000040008000000000000001/0f00000000004000800000000000000a/03-id-verification-back-image.png",
            IdentityProof.StorageKey(user, verification, 3, "id_verification.back_image", "image/png"));
        Assert.EndsWith(".jpg", IdentityProof.StorageKey(user, verification, 1, "x", "image/jpeg"));
        Assert.EndsWith(".bin", IdentityProof.StorageKey(user, verification, 1, "x", null));
    }

    [Theory]
    [InlineData("image/jpeg", true)]
    [InlineData("image/png", true)]
    [InlineData("application/pdf", true)]
    [InlineData("text/html", false)]
    [InlineData(null, false)]
    public void Only_an_image_or_a_pdf_is_kept(string? type, bool kept) =>
        Assert.Equal(kept, IdentityProof.Keepable(type));

    [Fact]
    public void The_facts_an_administrator_reads_first_come_off_the_document_and_the_checks()
    {
        var facts = IdentityProof.Summary(Decision).ToDictionary(f => f.Label, f => f.Value);

        Assert.Equal("Jane Doe", facts["Name"]);
        Assert.Equal("Identity Card", facts["Document"]);
        Assert.Equal("X1234567", facts["Document number"]);
        Assert.Equal("1990-01-01", facts["Date of birth"]);
        Assert.Equal("2031-05-01", facts["Expires"]);
        Assert.Equal("Approved", facts["Document check"]);
        Assert.Equal("Approved · score 92.5", facts["Liveness"]);
        Assert.Equal("Approved · score 97", facts["Face match"]);
    }

    [Fact]
    public void A_webhook_body_that_nests_the_decision_reads_the_same_facts()
    {
        var facts = IdentityProof.Summary("""{"webhook_type":"status.updated","decision":{"id_verifications":[{"full_name":"Jane Doe","status":"Declined"}]}}""");
        Assert.Contains(facts, f => f is { Label: "Name", Value: "Jane Doe" });
        Assert.Contains(facts, f => f is { Label: "Document check", Value: "Declined" });
    }

    [Theory]
    [InlineData(IdentityStatus.Approved, true, true, true, true)] // a new verdict always fetches its decision
    [InlineData(IdentityStatus.Declined, true, false, false, true)]
    [InlineData(IdentityStatus.InReview, true, false, false, true)]
    [InlineData(IdentityStatus.InProgress, true, false, false, false)] // nothing decided yet
    [InlineData(IdentityStatus.Approved, false, false, false, true)] // never copied, never tried
    [InlineData(IdentityStatus.Approved, false, false, true, false)] // queued or given up: the worker's, or Fetch again
    [InlineData(IdentityStatus.Approved, false, true, false, false)] // already kept
    public void A_verdict_queues_its_proof_when_it_is_new_or_was_never_tried(
        IdentityStatus status, bool changed, bool haveDecision, bool triedBefore, bool due) =>
        Assert.Equal(due, IdentityProof.Due(status, changed, haveDecision, triedBefore));

    [Fact]
    public void A_failed_copy_backs_off_to_an_hour_at_most()
    {
        Assert.Equal(TimeSpan.FromMinutes(2), IdentityProof.Backoff(1));
        Assert.Equal(TimeSpan.FromMinutes(8), IdentityProof.Backoff(3));
        Assert.Equal(TimeSpan.FromMinutes(60), IdentityProof.Backoff(10));
    }

    // ------------------------------------------------------------ webhooks

    [Theory]
    [InlineData("/api/webhooks/github", "github")]
    [InlineData("/api/webhooks/identity", "identity")]
    [InlineData("/api/webhooks/other", null)]
    [InlineData("/api/opportunities", null)]
    public void The_two_webhook_paths_are_third_party_services(string path, string? service) =>
        Assert.Equal(service, ExternalWebhooks.ServiceOf(path));

    [Fact]
    public void A_webhook_names_what_it_was_about_and_a_verdict_whose_member_it_is()
    {
        Assert.Equal("pull_request · opened", ExternalWebhooks.Subject("github", "pull_request", """{"action":"opened"}"""));
        Assert.Equal("push", ExternalWebhooks.Subject("github", "push", """{"ref":"refs/heads/main"}"""));
        Assert.Equal("status.updated · Approved",
            ExternalWebhooks.Subject("identity", null, """{"webhook_type":"status.updated","status":"Approved"}"""));
        Assert.Null(ExternalWebhooks.Subject("identity", null, "not json"));

        Assert.Equal(Guid.Parse("7d3c1e7a-0000-4000-8000-000000000001"), ExternalWebhooks.UserOf("identity", Decision));
        Assert.Null(ExternalWebhooks.UserOf("github", Decision));
    }

    [Fact]
    public async Task A_webhook_is_recorded_with_what_arrived_and_what_was_answered_and_the_controller_still_reads_it()
    {
        var log = new ActivityLog(NullLogger<ActivityLog>.Instance);
        var services = new ServiceCollection().AddSingleton(log).BuildServiceProvider();
        var body = """{"webhook_type":"status.updated","status":"Approved","vendor_data":"7d3c1e7a-0000-4000-8000-000000000001","decision":{"id_verification":{"document_number":"X1234567"}}}""";
        var ctx = new DefaultHttpContext { RequestServices = services };
        ctx.Request.Method = "POST";
        ctx.Request.Scheme = "https";
        ctx.Request.Host = new HostString("portal.example.test");
        ctx.Request.Path = "/api/webhooks/identity";
        ctx.Request.ContentType = "application/json";
        ctx.Request.Headers["X-Signature"] = "abcdef0123456789";
        ctx.Request.Headers["X-Timestamp"] = "1758620000";
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        var original = new MemoryStream();
        ctx.Response.Body = original;
        string? controllerRead = null;

        var middleware = new WebhookLogMiddleware(async c =>
        {
            // The controller reads the raw bytes for the signature check…
            using var reader = new StreamReader(c.Request.Body, leaveOpen: true);
            controllerRead = await reader.ReadToEndAsync();
            c.Response.StatusCode = 200;
            c.Response.ContentType = "application/json";
            await c.Response.WriteAsync("""{"ok":true,"note":"pending → approved"}""");
        });
        await middleware.InvokeAsync(ctx);

        // …and gets every byte, and the provider gets the answer unchanged.
        Assert.Equal(body, controllerRead);
        Assert.Equal("""{"ok":true,"note":"pending → approved"}""", Encoding.UTF8.GetString(original.ToArray()));

        Assert.True(log.Reader.TryRead(out var row));
        Assert.Equal(ActivityKinds.External, row!.Kind);
        Assert.Equal("identity", row.Service);
        Assert.Equal("Webhook from Didit", row.Action);
        Assert.Equal("status.updated · Approved", row.Subject);
        Assert.Equal("/api/webhooks/identity", row.Path);
        Assert.Equal(200, row.Status);
        Assert.Equal(Guid.Parse("7d3c1e7a-0000-4000-8000-000000000001"), row.UserId);
        Assert.Contains("X-Signature: [redacted]", row.Request);
        Assert.DoesNotContain("abcdef0123456789", row.Request);
        Assert.Contains("X1234567", row.Request); // the verdict is kept whole
        Assert.StartsWith("200 OK", row.Response);
        Assert.Contains("pending → approved", row.Response);
    }

    [Fact]
    public async Task Any_other_request_passes_through_unrecorded()
    {
        var log = new ActivityLog(NullLogger<ActivityLog>.Instance);
        var ctx = new DefaultHttpContext { RequestServices = new ServiceCollection().AddSingleton(log).BuildServiceProvider() };
        ctx.Request.Method = "POST";
        ctx.Request.Path = "/api/opportunities";
        var called = false;
        await new WebhookLogMiddleware(_ => { called = true; return Task.CompletedTask; }).InvokeAsync(ctx);
        Assert.True(called);
        Assert.False(log.Reader.TryRead(out _));
    }
}
