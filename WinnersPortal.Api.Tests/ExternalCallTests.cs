using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using WinnersPortal.Api.Activity;
using WinnersPortal.Domain;
using WinnersPortal.Services.Activity;
using WinnersPortal.Services.Email;
using Xunit;

namespace WinnersPortal.Api.Tests;

/// <summary>
/// Every call to a third-party API becomes an activity row with what was
/// sent and what came back. These pin the half that matters most: what the
/// row is never allowed to keep, and that recording a call never changes it.
/// </summary>
public class ExternalCallTests
{
    private static readonly string[] None = [];

    // ------------------------------------------------------------ masking

    [Theory]
    [InlineData("Authorization", true)]
    [InlineData("x-api-key", true)]
    [InlineData("x-goog-api-key", true)]
    [InlineData("client_secret", true)]
    [InlineData("access_token", true)]
    [InlineData("X-Amz-Signature", true)]
    [InlineData("X-Amz-Credential", true)]
    [InlineData("password", true)]
    [InlineData("key", true)]
    [InlineData("Content-Type", false)]
    [InlineData("anthropic-version", false)]
    [InlineData("client_id", false)]
    [InlineData("X-Amz-Date", false)]
    public void A_name_that_says_secret_is_masked(string name, bool secret) =>
        Assert.Equal(secret, ExternalExchange.SecretName(name));

    [Fact]
    public void A_request_keeps_its_line_and_headers_but_not_its_key()
    {
        var text = ExternalExchange.RequestText("ai", "POST", new Uri("https://api.anthropic.com/v1/messages"),
            [
                new("x-api-key", ["sk-ant-secret-value"]),
                new("anthropic-version", ["2023-06-01"]),
                new("Content-Type", ["application/json; charset=utf-8"]),
            ],
            "{}", None);

        Assert.StartsWith("POST https://api.anthropic.com/v1/messages\n", text);
        Assert.Contains("x-api-key: [redacted]", text);
        Assert.Contains("anthropic-version: 2023-06-01", text);
        Assert.DoesNotContain("sk-ant-secret-value", text);
    }

    [Fact]
    public void Counts_named_like_tokens_stay_but_secret_strings_go()
    {
        var body = ExternalExchange.Body("ai", "application/json",
            """{"model":"claude","max_tokens":8192,"usage":{"output_tokens":17},"session_token":"abc123456"}""", None, fromProvider: false);

        Assert.Contains("\"max_tokens\": 8192", body);
        Assert.Contains("\"output_tokens\": 17", body);
        Assert.Contains("\"session_token\": \"[redacted]\"", body);
        Assert.DoesNotContain("abc123456", body);
    }

    [Fact]
    public void The_github_code_exchange_keeps_neither_the_secret_nor_the_code_nor_the_token()
    {
        var sent = ExternalExchange.Body("github", "application/json",
            """{"client_id":"Iv1.abc","client_secret":"shh","code":"one-time-code"}""", None, fromProvider: false);
        var answered = ExternalExchange.Body("github", "application/json",
            """{"access_token":"gho_live","scope":"","token_type":"bearer"}""", None, fromProvider: true);

        Assert.Contains("Iv1.abc", sent);
        Assert.DoesNotContain("shh", sent);
        Assert.DoesNotContain("one-time-code", sent);
        Assert.DoesNotContain("gho_live", answered);
    }

    [Fact]
    public void The_captcha_form_keeps_the_address_but_not_the_secret_or_the_widgets_answer()
    {
        var body = ExternalExchange.Body("captcha", "application/x-www-form-urlencoded",
            "secret=0x4AAA-secret&response=widget-token&remoteip=203.0.113.9", None, fromProvider: false);

        Assert.Equal("secret=[redacted]&response=[redacted]&remoteip=203.0.113.9", body);
    }

    [Fact]
    public void The_identity_providers_answer_is_kept_whole_document_and_all()
    {
        var body = ExternalExchange.Body("identity", "application/json", """
            {
              "session_id": "11111111-2222-3333-4444-555555555555",
              "status": "Approved",
              "vendor_data": "member-id",
              "decision": {
                "id_verification": {
                  "status": "Approved",
                  "first_name": "Jane",
                  "last_name": "Doe",
                  "document_number": "X1234567",
                  "date_of_birth": "1990-01-01",
                  "age": 35,
                  "is_valid": true,
                  "portrait_image": "https://example.test/face.jpg",
                  "warnings": [{ "risk": "LOW", "short_description": "Blurry photo" }]
                },
                "extra_names": ["Janet"]
              }
            }
            """, None, fromProvider: true);

        // The portal keeps the proof: nothing the provider read off the
        // document is masked, only a field whose name says it is a secret.
        Assert.Contains("\"status\": \"Approved\"", body);
        Assert.Contains("11111111-2222-3333-4444-555555555555", body);
        foreach (var kept in new[] { "Jane", "Doe", "X1234567", "1990-01-01", "\"age\": 35", "face.jpg", "Janet", "Blurry" })
            Assert.Contains(kept, body);
    }

    [Fact]
    public void A_texted_code_is_masked_and_the_number_it_went_to_is_not()
    {
        var body = ExternalExchange.Body("sms", "application/json",
            """{"to":"+923001234567","text":"Your WinnersPortal code is 482913","api":"sk_live_gateway"}""",
            ["sk_live_gateway"], fromProvider: false);

        Assert.Contains("+923001234567", body);
        Assert.DoesNotContain("482913", body);
        // A key a template put under a name nobody could guess, masked because the caller marked it.
        Assert.DoesNotContain("sk_live_gateway", body);
    }

    [Fact]
    public void A_signed_storage_url_loses_its_signature_and_a_push_endpoint_its_subscription()
    {
        var storage = ExternalExchange.Url("storage",
            new Uri("https://minio.example.test/bucket/key.png?X-Amz-Date=20260923T120000Z&X-Amz-Credential=AKIA%2F&X-Amz-Signature=deadbeef"), None);
        Assert.Contains("X-Amz-Date=20260923T120000Z", storage);
        Assert.DoesNotContain("deadbeef", storage);
        Assert.DoesNotContain("AKIA", storage);

        Assert.Equal("fcm.googleapis.com/fcm/send/…",
            ExternalExchange.Path("push", new Uri("https://fcm.googleapis.com/fcm/send/abc-very-long-subscription")));
    }

    [Fact]
    public void A_long_body_is_cut_and_says_so()
    {
        var cut = ExternalExchange.Cut(new string('a', ExternalExchange.MaxBody + 10));
        Assert.EndsWith("[10 more characters not kept]", cut);
    }

    [Theory]
    [InlineData("api.openai.com", "OpenAI")]
    [InlineData("verification.didit.me", "Didit")]
    [InlineData("api.github.com", "GitHub")]
    [InlineData("gateway.example.test", "gateway.example.test")]
    public void A_host_reads_as_the_company_behind_it(string host, string vendor) =>
        Assert.Equal(vendor, ExternalExchange.Vendor(host));

    // ------------------------------------------------------------ recording

    private sealed class Answer(Func<HttpRequestMessage, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        public string? Received { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Content is not null) Received = await request.Content.ReadAsStringAsync(ct);
            return await answer(request);
        }
    }

    private static (HttpClient Client, ActivityLog Log, Answer Inner) Recorded(string service, Func<HttpRequestMessage, Task<HttpResponseMessage>> answer)
    {
        var log = new ActivityLog(NullLogger<ActivityLog>.Instance);
        var inner = new Answer(answer);
        var recorder = new ExternalCallRecorder(service, log, new HttpContextAccessor()) { InnerHandler = inner };
        return (new HttpClient(recorder), log, inner);
    }

    private static ActivityEvent Only(ActivityLog log)
    {
        Assert.True(log.Reader.TryRead(out var row));
        Assert.False(log.Reader.TryRead(out _));
        return row!;
    }

    [Fact]
    public async Task A_call_becomes_a_third_party_row_and_the_caller_still_reads_the_answer()
    {
        var (client, log, inner) = Recorded("ai", _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"id":"msg_1","content":[{"type":"text","text":"hello"}]}""", Encoding.UTF8, "application/json"),
        }));
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
        {
            Content = JsonContent.Create(new { model = "gpt", messages = new[] { new { role = "user", content = "hi" } } }),
        };
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer sk-live");

        using var response = await client.SendAsync(request);

        // The call is untouched: the provider got the whole body, the caller gets the whole answer.
        Assert.Contains("\"messages\"", inner.Received);
        Assert.Contains("hello", await response.Content.ReadAsStringAsync());

        var row = Only(log);
        Assert.Equal(ActivityKinds.External, row.Kind);
        Assert.Equal("ai", row.Service);
        Assert.Equal("Called OpenAI", row.Action);
        Assert.Equal("POST", row.Method);
        Assert.Equal("api.openai.com/v1/chat/completions", row.Path);
        Assert.Equal(200, row.Status);
        Assert.NotNull(row.DurationMs);
        Assert.Null(row.UserId); // no request in flight: a worker's call
        Assert.Contains("Authorization: [redacted]", row.Request);
        Assert.DoesNotContain("sk-live", row.Request);
        Assert.Contains("\"role\": \"user\"", row.Request);
        Assert.StartsWith("200 OK", row.Response);
        Assert.Contains("hello", row.Response);
    }

    [Fact]
    public async Task A_call_that_gets_no_answer_is_recorded_and_still_fails()
    {
        var (client, log, _) = Recorded("sms", _ => throw new HttpRequestException("No such host is known."));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.PostAsync("https://gateway.example.test/send", new StringContent("{}", Encoding.UTF8, "application/json")));

        var row = Only(log);
        Assert.Equal(0, row.Status);
        Assert.StartsWith("No answer", row.Response);
        Assert.Contains("No such host is known.", row.Response);
    }

    [Fact]
    public async Task A_file_sent_to_storage_is_described_never_read()
    {
        var (client, log, inner) = Recorded("storage", _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var file = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes("{\"a file\":\"that happens to be JSON\"}")));
        file.Headers.ContentType = new("application/json");

        using var _ = await client.PutAsync("https://minio.example.test/bucket/data.json?X-Amz-Signature=abc", file);

        // The stream reached storage whole; the row says a file went, not what was in it.
        Assert.Contains("that happens to be JSON", inner.Received);
        var row = Only(log);
        Assert.Contains("file contents are not recorded", row.Request);
        Assert.DoesNotContain("that happens to be JSON", row.Request);
        Assert.DoesNotContain("X-Amz-Signature=abc", row.Request);
    }

    // ------------------------------------------------------ the mail APIs

    /// <summary>A sign-in code in the subject and the body, a reset token in the button, an unsubscribe token in the header.</summary>
    private static MimeKit.MimeMessage CodeMail() =>
        EmailSender.Compose("Winners Portal", "no-reply@mg.example.com", "Ayesha", "ayesha@example.com",
            "482913 is your Winners Portal code", "Enter 482913 to confirm your address.", "Reset your password",
            "https://portal.example/reset-password?token=resettoken99", "#0f766e",
            "https://portal.example/unsubscribe?t=stoptoken77");

    private static readonly string[] MailSecrets = ["482913", "resettoken99", "stoptoken77", "Enter", "key-abcdef123", "xkeysib-abcdef123"];

    private static Func<HttpRequestMessage, Task<HttpResponseMessage>> Answers(HttpStatusCode status, string json) =>
        _ => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") });

    [Fact]
    public async Task A_mailgun_row_keeps_who_and_the_answer_but_never_the_message_or_the_key()
    {
        var (client, log, inner) = Recorded(ExternalServices.Email,
            Answers(HttpStatusCode.OK, """{"id":"<1@mg.example.com>","message":"Queued. Thank you."}"""));
        await using var transport = new ApiTransport(client, EmailProviders.Mailgun, "Mailgun (mg.example.com)", "key-abcdef123",
            m => MailApi.Mailgun("https://api.mailgun.net", "mg.example.com", "key-abcdef123", m));

        await transport.SendAsync(CodeMail(), CancellationToken.None);

        Assert.Contains("resettoken99", inner.Received); // Mailgun got the whole message…
        var row = Only(log);
        Assert.Equal("email", row.Service);
        Assert.Equal("Called Mailgun", row.Action);
        Assert.Equal("api.mailgun.net/v3/mg.example.com/messages", row.Path);
        Assert.Contains("ayesha%40example.com", row.Request); // …the row keeps who it went to…
        Assert.Contains("subject=[redacted]", row.Request);
        Assert.All(MailSecrets, s => Assert.DoesNotContain(s, row.Request)); // …and nothing it said.
        Assert.Contains("Queued. Thank you.", row.Response);
    }

    [Fact]
    public async Task A_brevo_row_keeps_who_and_the_answer_but_never_the_message_or_the_key()
    {
        var (client, log, _) = Recorded(ExternalServices.Email, Answers(HttpStatusCode.Created, """{"messageId":"<2@smtp-relay.mailin.fr>"}"""));
        await using var transport = new ApiTransport(client, EmailProviders.Brevo, "Brevo", "xkeysib-abcdef123",
            m => MailApi.Brevo("xkeysib-abcdef123", m));

        await transport.SendAsync(CodeMail(), CancellationToken.None);

        var row = Only(log);
        Assert.Equal("Called Brevo", row.Action);
        Assert.Contains("ayesha@example.com", row.Request);
        Assert.Contains("no-reply@mg.example.com", row.Request);
        Assert.All(MailSecrets, s => Assert.DoesNotContain(s, row.Request));
        Assert.Contains("messageId", row.Response);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, true)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.BadGateway, true)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.Forbidden, false)]
    public async Task A_refusal_says_who_answered_and_whether_every_message_would_get_it(HttpStatusCode status, bool serviceWide)
    {
        var (client, _, _) = Recorded(ExternalServices.Email, Answers(status, """{"code":"x","message":"Nope"}"""));
        await using var transport = new ApiTransport(client, EmailProviders.Brevo, "Brevo", "xkeysib-1", m => MailApi.Brevo("xkeysib-1", m));

        var e = await Assert.ThrowsAnyAsync<Exception>(() => transport.SendAsync(CodeMail(), CancellationToken.None));

        Assert.Equal(serviceWide, e is MailServiceDown);
        Assert.StartsWith($"Brevo answered {(int)status}: Nope", e.Message);
    }

    [Fact]
    public async Task A_refused_brevo_key_says_where_to_look_and_an_unreachable_service_is_the_services_fault()
    {
        var (refusing, _, _) = Recorded(ExternalServices.Email, Answers(HttpStatusCode.Unauthorized, """{"message":"Key not found"}"""));
        await using var brevo = new ApiTransport(refusing, EmailProviders.Brevo, "Brevo", "xkeysib-1", m => MailApi.Brevo("xkeysib-1", m));
        var refused = await Assert.ThrowsAsync<MailServiceDown>(() => brevo.SendAsync(CodeMail(), CancellationToken.None));
        Assert.Contains("Authorised IPs", refused.Message);

        var (away, log, _) = Recorded(ExternalServices.Email, _ => throw new HttpRequestException("No such host is known."));
        await using var mailgun = new ApiTransport(away, EmailProviders.Mailgun, "Mailgun (mg.example.com)", "key-1",
            m => MailApi.Mailgun("https://api.mailgun.net", "mg.example.com", "key-1", m));
        var down = await Assert.ThrowsAsync<MailServiceDown>(() => mailgun.SendAsync(CodeMail(), CancellationToken.None));
        Assert.Equal("Mailgun (mg.example.com) could not be reached: No such host is known.", down.Message);
        Assert.StartsWith("No answer", Only(log).Response);
    }
}
