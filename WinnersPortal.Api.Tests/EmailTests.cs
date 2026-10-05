using WinnersPortal.Services.Ai;
using WinnersPortal.Services.Auth;
using WinnersPortal.Services.Profiles;
using WinnersPortal.Domain;
using WinnersPortal.Services.Email;
using Xunit;

namespace WinnersPortal.Api.Tests;

/// <summary>
/// The email layer's pure parts: wording, rendering, and the digest clock.
/// The worker is deliberately thin around these — what can go subtly wrong
/// (a congratulation to the loser, HTML injection via an opportunity title, a
/// digest sent twice or at 3am) lives here where it can be pinned down.
/// </summary>
public class EmailTests
{
    // ----------------------------------------------------------- wording

    [Theory]
    [InlineData(1, "1st")]
    [InlineData(2, "2nd")]
    [InlineData(3, "3rd")]
    [InlineData(4, "4th")]
    [InlineData(11, "11th")]
    [InlineData(12, "12th")]
    [InlineData(13, "13th")]
    [InlineData(21, "21st")]
    [InlineData(102, "102nd")]
    [InlineData(111, "111th")]
    public void Ordinals_survive_the_teens(int n, string expected) =>
        Assert.Equal(expected, Emails.Ordinal(n));

    [Fact]
    public void Money_groups_thousands_and_keeps_the_currency_code()
    {
        Assert.Equal("1,500 USD", Emails.Money(1500m, "USD"));
        Assert.Equal("2,500.5 EUR", Emails.Money(2500.50m, "EUR"));
    }

    [Fact]
    public void Winner_and_loser_read_nothing_alike()
    {
        var won = Emails.AwardWon("Realtime Chat", "realtime-chat", 1500m, "USD");
        var lost = Emails.AwardLost("Realtime Chat");

        Assert.Contains("You won", won.Subject);
        Assert.Contains("1,500 USD", won.TextBody);
        // The winner is told the one thing that protects them: nothing
        // transfers before they are paid.
        Assert.Contains("nothing changes hands before you are paid", won.TextBody);

        Assert.DoesNotContain("won", lost.Subject, StringComparison.OrdinalIgnoreCase);
        // The loser is told the one thing that matters to them: the work
        // stays theirs.
        Assert.Contains("stays yours", lost.TextBody);
    }

    [Fact]
    public void An_application_closed_undecided_is_not_called_a_no()
    {
        var deadline = Emails.ApplicationClosedUndecided("T", cancelled: false);
        var cancelled = Emails.ApplicationClosedUndecided("T", cancelled: true);

        Assert.Contains("deadline", deadline.TextBody);
        Assert.Contains("cancelled", cancelled.TextBody);
        // Nobody decided, so the email must not read like a decision.
        Assert.Contains("not turned down", deadline.TextBody);
        Assert.DoesNotContain("not selected", deadline.Subject, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Paid_without_a_transfer_says_so_instead_of_pretending()
    {
        var clean = Emails.AwardPaid("T", "t", 100m, "USD", handoverNote: null);
        var stuck = Emails.AwardPaid("T", "t", 100m, "USD",
            handoverNote: "GitHub is not configured; no repository to transfer.");

        Assert.Contains("has been requested", clean.TextBody);
        Assert.Contains("No repository transfer was started", stuck.TextBody);
        Assert.Contains("GitHub is not configured", stuck.TextBody);
    }

    [Fact]
    public void Every_email_with_an_action_uses_a_relative_path()
    {
        // Paths are absolutized against branding.publicUrl at send time; a
        // template that bakes in a host would dodge that and go stale.
        foreach (var content in new[]
                 {
                     Emails.ApplicationReceived("T", "t", "N", 87, 1),
                     Emails.ApplicationSubmitted("T", "t", "C"),
                     Emails.ApplicationNotSelected("T", "t"),
                     Emails.SelectionTakenBack("T", "t", hadRepository: true),
                     Emails.ApplicationClosedUndecided("T", cancelled: false),
                     Emails.ApplicationClosedUndecided("T", cancelled: true),
                     Emails.ApplicationDecidedByAdmin("T", "t", "N", selected: false, takenBack: true),
                     Emails.ApplicationDecidedByAdmin("T", "t", "N", selected: true),
                     Emails.ApplicationDecidedByAdmin("T", "t", "N", selected: false),
                     Emails.OpportunityInReviewClient("T", "t", 3, OpportunityDelivery.Repository),
                     Emails.OpportunityInReviewEntrant("T", "t", OpportunityDelivery.Repository),

                     Emails.OpportunityInReviewClient("T", "t", 3, OpportunityDelivery.Both),
                     Emails.OpportunityInReviewEntrant("T", "t", OpportunityDelivery.Upload),
                     Emails.TransferRequested("T", "t", "org/repo", "login"),
                     Emails.RepoReady("T", "t", "org/repo", "gh", null),
                     Emails.RepoFailed("T", "t"),
                     Emails.BuildFailed("T", "t", 2, "npm ci exited 1"),
                     Emails.OpportunityCancelled("T", "t", "the budget moved"),
                     Emails.TestSend("P"),
                     Emails.AwardWon("T", "t", 1m, "USD"),
                     Emails.AwardLost("T"),
                     Emails.AwardPaid("T", "t", 1m, "USD", null),
                     Emails.RepoFailedAdmin("T", "gh", 8, null),
                     Emails.Digest("P", ["one thing"]),
                     Emails.PasswordReset("tok"),
                     Emails.PasswordChanged(),
                     Emails.AccountInvitation("tok", "freelancer", 7),
                     Emails.OpportunityOpened("T", "t", "C", 1m, "USD", null),
                     Emails.WinnerAnnounced("T", "t", "W", 1m, "USD"),
                     Emails.Welcome("P", Roles.Client),
                     Emails.Welcome("P", Roles.Freelancer),
                     Emails.ApplicationSelected("T", "t", "C", 1m, "USD", OpportunityDelivery.Repository,
                         null, null, [], 1, "gh"),
                 })
        {
            Assert.NotNull(content.ActionPath);
            Assert.StartsWith("/", content.ActionPath);
        }
    }

    [Fact]
    public void Welcome_sends_each_role_to_its_own_first_step()
    {
        var client = Emails.Welcome("Winners Portal", Roles.Client);
        var freelancer = Emails.Welcome("Winners Portal", Roles.Freelancer);

        Assert.Equal("Welcome to Winners Portal", client.Subject);
        Assert.Equal("Welcome to Winners Portal", freelancer.Subject);
        // The one thing worth doing first differs by role, and so does the link.
        Assert.Equal("/client/opportunities/new", client.ActionPath);
        Assert.Equal("/profile", freelancer.ActionPath);
        // Both are told the rule that decides whether to trust the place:
        // the money never passes through it.
        Assert.Contains("holds no money", client.TextBody);
        Assert.Contains("paid directly", freelancer.TextBody);
        // And the freelancer hears where the repository invitation goes,
        // because a mistyped username there is the usual first failure.
        Assert.Contains("GitHub username", freelancer.TextBody);
    }

    [Fact]
    public void Selection_carries_the_terms_the_entrant_is_now_committed_to()
    {
        var deadline = new DateTimeOffset(2026, 10, 1, 17, 0, 0, TimeSpan.Zero);
        var closes = new DateTimeOffset(2026, 9, 20, 17, 0, 0, TimeSpan.Zero);
        var mail = Emails.ApplicationSelected(
            "Realtime Chat", "realtime-chat", "Astrik", 1500m, "USD", OpportunityDelivery.Repository,
            deadline, closes,
            [("Auth flow", new DateTimeOffset(2026, 9, 25, 0, 0, 0, TimeSpan.Zero)), ("Message history", null)],
            entrantNumber: 3, githubUsername: "bilal-builds");

        Assert.Contains("Realtime Chat", mail.Subject);
        Assert.Contains("1,500 USD", mail.Subject);
        Assert.Contains("Astrik selected your application", mail.TextBody);
        Assert.Contains("3rd entrant", mail.TextBody);
        Assert.Contains("Deadline: 2026-10-01 17:00 UTC", mail.TextBody);
        // The early cut-off is named only because it differs from the deadline.
        Assert.Contains("Entry closes to newcomers 2026-09-20 17:00 UTC", mail.TextBody);
        Assert.Contains("“bilal-builds”", mail.TextBody);
        // The checklist, numbered, with the date where the client set one.
        Assert.Contains("1. Auth flow — due by 2026-09-25 00:00 UTC", mail.TextBody);
        Assert.Contains("2. Message history\n", mail.TextBody);
        Assert.Equal("/opportunities/realtime-chat", mail.ActionPath);
    }

    [Fact]
    public void Selection_says_only_what_the_delivery_actually_asks_for()
    {
        var upload = Emails.ApplicationSelected("T", "t", "C", 1m, "USD", OpportunityDelivery.Upload,
            null, null, [], 1, "");
        var both = Emails.ApplicationSelected("T", "t", "C", 1m, "USD", OpportunityDelivery.Both,
            null, null, [], 1, "gh");

        // No repository, no username, no tags — an upload opportunity asked for none.
        Assert.DoesNotContain("repository", upload.TextBody.Replace("a repository is archived", ""));
        Assert.Contains("files on the opportunity page", upload.TextBody);
        Assert.DoesNotContain("Deadline:", upload.TextBody);
        Assert.DoesNotContain("Milestones", upload.TextBody);

        Assert.Contains("private repository", both.TextBody);
        Assert.Contains("files on the opportunity page", both.TextBody);
    }

    [Fact]
    public void Reset_email_assumes_nothing_about_who_asked()
    {
        var mail = Emails.PasswordReset("abc123");

        Assert.Contains("password", mail.Subject, StringComparison.OrdinalIgnoreCase);
        // The token is the link; nothing else in the message identifies the account.
        Assert.Equal("/reset-password?token=abc123", mail.ActionPath);
        Assert.Contains("hour", mail.TextBody);
        // The one thing a recipient who did not ask needs to hear.
        Assert.Contains("nothing has changed", mail.TextBody);
    }

    [Fact]
    public void Password_changed_email_says_how_to_take_the_account_back()
    {
        var mail = Emails.PasswordChanged();

        Assert.Contains("password", mail.Subject, StringComparison.OrdinalIgnoreCase);
        // Written for the day it was not them: the way back is a reset,
        // which needs only the inbox, not the password they no longer know.
        Assert.Equal("/forgot-password", mail.ActionPath);
        Assert.Contains("signed out", mail.TextBody);
    }

    [Fact]
    public void Cancellation_carries_the_reason_verbatim_and_says_the_work_survives()
    {
        var mail = Emails.OpportunityCancelled("Realtime Chat", "realtime-chat",
            "The project this opportunity was built around has been shelved.");

        Assert.Contains("Realtime Chat", mail.Subject);
        // Word for word, in quotes — a paraphrased reason is the portal
        // putting words in the client's mouth.
        Assert.Contains("“The project this opportunity was built around has been shelved.”", mail.TextBody);
        Assert.Contains("archived", mail.TextBody);
        Assert.Contains("stays yours", mail.TextBody);
        // And the accountability line: cancelling is not free.
        Assert.Contains("public record", mail.TextBody);
    }

    [Fact]
    public void The_test_email_says_it_is_proof_and_where_spam_blame_lies()
    {
        var mail = Emails.TestSend("Winners Portal");
        Assert.Contains("Test email from Winners Portal", mail.Subject);
        // The claim that makes the test worth anything: same connection,
        // sender, and template as real notifications.
        Assert.Contains("same email setup", mail.TextBody);
        // And the honest caveat — deliverability is the domain's problem.
        Assert.Contains("spam", mail.TextBody);
        Assert.Contains("SPF", mail.TextBody);
    }

    [Fact]
    public void Digest_counts_its_items_in_the_subject()
    {
        Assert.Contains("1 item needs you", Emails.Digest("Winners Portal", ["a"]).Subject);
        Assert.Contains("3 items need you", Emails.Digest("Winners Portal", ["a", "b", "c"]).Subject);
    }

    // --------------------------------------------------------- rendering

    [Fact]
    public void Absolute_urls_survive_a_trailing_slash_in_the_setting()
    {
        Assert.Equal("https://x.example/opportunities/t",
            EmailRender.AbsoluteUrl("https://x.example/", "/opportunities/t"));
        Assert.Equal("https://x.example/opportunities/t",
            EmailRender.AbsoluteUrl("https://x.example", "/opportunities/t"));
    }

    [Fact]
    public void Text_render_carries_greeting_action_and_signature()
    {
        var text = EmailRender.Text("Ayesha", "Body here.", "See it", "https://x.example/t", "Winners Portal");
        Assert.Contains("Hi Ayesha,", text);
        Assert.Contains("See it: https://x.example/t", text);
        Assert.Contains("— Winners Portal", text);
    }

    [Fact]
    public void Html_render_escapes_content_a_user_typed()
    {
        // Opportunity titles and display names are user input; a title like
        // <script> must arrive as text, never as markup.
        var html = EmailRender.Html("<Ayesha>", "About “<script>alert(1)</script>” & more.",
            "Click & go", "https://x.example/t?a=1", "P", "#0f766e");
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("&lt;Ayesha&gt;", html);
        Assert.Contains("Click &amp; go", html);
        Assert.Contains("href=\"https://x.example/t?a=1\"", html);
    }

    [Fact]
    public void Html_render_rejects_a_malformed_accent_instead_of_injecting_it()
    {
        // The accent lands inside a style attribute; anything that is not a
        // hex colour falls back to the default rather than being emitted.
        var html = EmailRender.Html("A", "B", "Go", "https://x.example", "P",
            "red;\"></a><script>alert(1)</script>");
        Assert.DoesNotContain("script", html);
        Assert.Contains("#0f766e", html);
    }

    [Fact]
    public void Bare_urls_in_the_text_become_links_in_the_html()
    {
        var html = EmailRender.Html("A", "Your repo: https://github.com/org/repo-x", null, null, "P", "#0f766e");
        Assert.Contains("<a href=\"https://github.com/org/repo-x\"", html);
    }

    // ------------------------------------------------------ digest clock

    [Fact]
    public void Digest_waits_for_the_hour_and_sends_once_per_day()
    {
        var at0830 = new DateTimeOffset(2026, 8, 31, 8, 30, 0, TimeSpan.Zero);
        var at0900 = new DateTimeOffset(2026, 8, 31, 9, 0, 0, TimeSpan.Zero);
        var at2300 = new DateTimeOffset(2026, 8, 31, 23, 0, 0, TimeSpan.Zero);

        Assert.False(DigestSchedule.IsDue(null, 9, at0830));          // too early
        Assert.True(DigestSchedule.IsDue(null, 9, at0900));           // first ever
        Assert.True(DigestSchedule.IsDue("2026-08-30", 9, at0900));   // new day
        Assert.False(DigestSchedule.IsDue("2026-08-31", 9, at0900));  // already sent
        Assert.False(DigestSchedule.IsDue("2026-08-31", 9, at2300));  // still sent
    }

    [Fact]
    public void Digest_recovers_late_rather_than_skipping_a_day()
    {
        // A worker that was down over the digest hour sends when it returns.
        var afternoon = new DateTimeOffset(2026, 8, 31, 15, 0, 0, TimeSpan.Zero);
        Assert.True(DigestSchedule.IsDue("2026-08-30", 9, afternoon));
    }

    [Fact]
    public void Digest_clamps_a_nonsense_hour_instead_of_never_firing()
    {
        var noon = new DateTimeOffset(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);
        var lateNight = new DateTimeOffset(2026, 8, 31, 23, 30, 0, TimeSpan.Zero);
        Assert.False(DigestSchedule.IsDue(null, 99, noon));      // clamped to 23; noon is early
        Assert.True(DigestSchedule.IsDue(null, 99, lateNight));  // …but 23:30 fires
        Assert.True(DigestSchedule.IsDue(null, -5, noon));       // clamped to 0; always due
    }

    // ------------------------------------------------------ the mail APIs

    private static MimeKit.MimeMessage Mail(string? stop = "https://portal.example/unsubscribe?t=abc") =>
        EmailSender.Compose("Winners Portal", "no-reply@mg.example.com", "Ayesha Khan", "ayesha@example.com",
            "Your repository is ready", "Push your first commit.", "Open the opportunity", "https://portal.example/opportunities/x",
            "#0f766e", stop);

    [Fact]
    public void Mailgun_is_sent_the_parts_every_smtp_email_carries()
    {
        var mime = Mail();
        var form = MailApi.MailgunForm(mime);
        Assert.Equal(["from", "to", "subject", "text", "html", "h:List-Unsubscribe"], form.Select(p => p.Key));
        var field = form.ToDictionary(p => p.Key, p => p.Value);
        Assert.Contains("Winners Portal", field["from"]);
        Assert.Contains("<no-reply@mg.example.com>", field["from"]);
        Assert.Contains("<ayesha@example.com>", field["to"]);
        Assert.Equal(mime.Subject, field["subject"]);
        Assert.Equal(mime.TextBody, field["text"]);
        Assert.Equal(mime.HtmlBody, field["html"]);
        Assert.Equal("<https://portal.example/unsubscribe?t=abc>", field["h:List-Unsubscribe"]);
        // No unsubscribe link, no header.
        Assert.DoesNotContain("h:List-Unsubscribe", MailApi.MailgunForm(Mail(stop: null)).Select(p => p.Key));
    }

    [Fact]
    public void Mailgun_is_asked_at_its_region_for_its_domain_signed_in_as_api()
    {
        using var request = MailApi.Mailgun("https://api.eu.mailgun.net", "mg.example.com", "key-123", Mail());
        Assert.Equal("https://api.eu.mailgun.net/v3/mg.example.com/messages", request.RequestUri!.ToString());
        Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
        Assert.Equal("api:key-123",
            System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(request.Headers.Authorization.Parameter!)));
        Assert.Equal("application/x-www-form-urlencoded", request.Content!.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Brevo_is_sent_the_same_parts_as_json_with_its_key_in_its_own_header()
    {
        var mime = Mail();
        using var request = MailApi.Brevo("xkeysib-123", mime);
        Assert.Equal(EmailProviders.BrevoUrl, request.RequestUri!.ToString());
        Assert.Equal(["xkeysib-123"], request.Headers.GetValues("api-key"));

        using var json = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
        var root = json.RootElement;
        Assert.Equal("no-reply@mg.example.com", root.GetProperty("sender").GetProperty("email").GetString());
        Assert.Equal("Winners Portal", root.GetProperty("sender").GetProperty("name").GetString());
        Assert.Equal("ayesha@example.com", root.GetProperty("to")[0].GetProperty("email").GetString());
        Assert.Equal("Ayesha Khan", root.GetProperty("to")[0].GetProperty("name").GetString());
        Assert.Equal(mime.Subject, root.GetProperty("subject").GetString());
        Assert.Equal(mime.TextBody, root.GetProperty("textContent").GetString());
        Assert.Equal(mime.HtmlBody, root.GetProperty("htmlContent").GetString());
        Assert.Equal("<https://portal.example/unsubscribe?t=abc>",
            root.GetProperty("headers").GetProperty("List-Unsubscribe").GetString());
    }

    [Fact]
    public void Brevo_is_sent_no_name_rather_than_an_empty_one_and_no_headers_it_has_none_of()
    {
        var body = MailApi.BrevoBody(EmailSender.Compose("P", "a@x.example", "", "b@x.example", "S", "B.", null, null, "#000000"));
        Assert.Null(body.To[0].Name);
        Assert.Null(body.Headers);
        var json = System.Text.Json.JsonSerializer.Serialize(body, MailApi.BrevoJson);
        Assert.DoesNotContain("\"headers\"", json);
        Assert.Contains("\"to\":[{\"email\":\"b@x.example\"}]", json);
    }

    [Theory]
    [InlineData("""{"message":"Domain not found: mg.example.com"}""", "Domain not found: mg.example.com")]
    [InlineData("""{"code":"unauthorized","message":"Key not found"}""", "Key not found")]
    [InlineData("Forbidden", "Forbidden")]
    [InlineData("  ", "")]
    public void A_refusal_is_quoted_in_the_services_own_words(string body, string said) =>
        Assert.Equal(said, MailApi.Said(body));

    [Theory]
    [InlineData(401, true)]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(503, true)]
    [InlineData(400, false)]
    [InlineData(403, false)]
    [InlineData(404, false)]
    public void Only_a_refusal_every_message_would_get_is_the_services_fault(int status, bool serviceWide) =>
        Assert.Equal(serviceWide, MailApi.ServiceWide(status));
}
