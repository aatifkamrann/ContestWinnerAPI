using WinnersPortal.Services.Ai;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using WinnersPortal.Domain;
using WinnersPortal.Services.Email;
using WinnersPortal.Services.Notifications;
using WinnersPortal.Services.Settings;
using Xunit;

namespace WinnersPortal.Api.Tests;

/// <summary>
/// The opt-in broadcast layer's pure parts: the VAPID pair and the checks a
/// browser's subscription passes, the push wording and its payload, the
/// rule that retires a dead device, the unsubscribe token, and the email
/// footer that carries it. The worker and the fan-out are thin around
/// these, like the email worker around <see cref="EmailTests"/>.
/// </summary>
public class NotificationTests
{
    // -------------------------------------------------------------- keys

    [Fact]
    public void A_generated_pair_has_the_shapes_push_services_expect()
    {
        var (publicKey, privateKey) = Vapid.Generate();

        // RFC 8292: the public key is the uncompressed P-256 point (0x04 + X + Y),
        // the private key the 32-byte scalar, both base64url without padding.
        var point = Vapid.FromBase64Url(publicKey)!;
        Assert.Equal(65, point.Length);
        Assert.Equal(0x04, point[0]);
        Assert.Equal(32, Vapid.FromBase64Url(privateKey)!.Length);
        foreach (var key in new[] { publicKey, privateKey })
            Assert.Matches("^[A-Za-z0-9_-]+$", key);

        // A browser's own key has the same shape, so the door check agrees.
        Assert.True(Vapid.IsValidP256dh(publicKey));
    }

    [Fact]
    public void Two_pairs_never_agree() =>
        Assert.NotEqual(Vapid.Generate().PublicKey, Vapid.Generate().PublicKey);

    [Fact]
    public void Base64url_round_trips_every_padding_case()
    {
        for (var length = 1; length <= 9; length++)
        {
            var bytes = Enumerable.Range(0, length).Select(i => (byte)(250 + i)).ToArray();
            Assert.Equal(bytes, Vapid.FromBase64Url(Vapid.Base64Url(bytes)));
        }
        Assert.Null(Vapid.FromBase64Url("not base64!"));
        // Nothing is not a key: empty decodes to null, not to zero bytes.
        Assert.Null(Vapid.FromBase64Url(""));
        Assert.Null(Vapid.FromBase64Url(null));
    }

    [Fact]
    public void A_subscription_is_checked_at_the_door()
    {
        Assert.True(Vapid.IsValidAuth(Vapid.Base64Url(new byte[16])));
        Assert.False(Vapid.IsValidAuth(Vapid.Base64Url(new byte[15])));
        Assert.False(Vapid.IsValidAuth(null));

        var compressed = new byte[33];
        compressed[0] = 0x02;
        Assert.False(Vapid.IsValidP256dh(Vapid.Base64Url(compressed)));

        Assert.True(Vapid.IsValidEndpoint("https://fcm.googleapis.com/fcm/send/abc:def"));
        Assert.False(Vapid.IsValidEndpoint("http://fcm.googleapis.com/fcm/send/abc")); // push services speak https
        Assert.False(Vapid.IsValidEndpoint("not a url"));
        Assert.False(Vapid.IsValidEndpoint(null));
        Assert.False(Vapid.IsValidEndpoint("https://x.example/" + new string('a', Vapid.MaxEndpointLength)));
    }

    [Fact]
    public void The_pair_lives_as_generated_system_settings()
    {
        // Never shown, never typed: the settings screen skips the system
        // group, and the private half is encrypted like every secret.
        var pub = Assert.Single(SettingsRegistry.All, s => s.Key == PushKeys.PublicKeySetting);
        var priv = Assert.Single(SettingsRegistry.All, s => s.Key == PushKeys.PrivateKeySetting);
        Assert.Equal(SettingsRegistry.SystemGroup, pub.Group);
        Assert.Equal(SettingsRegistry.SystemGroup, priv.Group);
        Assert.True(priv.IsSecret);
    }

    // ----------------------------------------------------------- wording

    [Fact]
    public void Topics_fit_the_header_and_tell_events_apart()
    {
        var opportunity = Guid.NewGuid();
        var open = Pushes.Topic("open-", opportunity);
        var won = Pushes.Topic("won-", opportunity);

        // The Web Push Topic header: at most 32 URL-safe base64 characters.
        Assert.InRange(open.Length, 1, Pushes.MaxTopicLength);
        Assert.Matches("^[A-Za-z0-9_-]+$", open);
        Assert.NotEqual(open, won);
        Assert.Equal(open, Pushes.Topic("open-", opportunity)); // stable, so repeats collapse
        Assert.NotEqual(open, Pushes.Topic("open-", Guid.NewGuid()));
    }

    [Fact]
    public void A_push_says_what_happened_and_the_one_fact_that_decides_a_tap()
    {
        var id = Guid.NewGuid();
        var deadline = new DateTimeOffset(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);
        var opened = Pushes.OpportunityOpened(id, "Realtime Chat", "realtime-chat", "Ayesha Karim", 1500m, "USD", deadline);
        Assert.Equal("New opportunity: Realtime Chat", opened.Title);
        Assert.Contains("1,500 USD", opened.Body);
        Assert.Contains("Ayesha Karim", opened.Body);
        Assert.Contains("Entry closes 17 Sep", opened.Body);
        // A later start day rides along, as information.
        var ahead = Pushes.OpportunityOpened(id, "T", "t", "C", 1m, "USD", deadline,
            new DateTimeOffset(2026, 9, 10, 0, 0, 0, TimeSpan.Zero));
        Assert.Contains("Starts 10 Sep", ahead.Body);
        Assert.Equal("/opportunities/realtime-chat", opened.Path);

        // No deadline yet is possible on paper; the line simply drops.
        Assert.DoesNotContain("Entry closes", Pushes.OpportunityOpened(id, "T", "t", "C", 1m, "USD", null).Body);

        var won = Pushes.WinnerAnnounced(id, "Realtime Chat", "realtime-chat", "Nadia Rahman", 1500m, "USD");
        Assert.Equal("Winner announced: Realtime Chat", won.Title);
        Assert.Contains("Nadia Rahman won the 1,500 USD award", won.Body);
        Assert.Equal("/opportunities/realtime-chat", won.Path);
    }

    [Fact]
    public void The_payload_is_what_the_service_worker_reads()
    {
        var message = new PushMessage
        {
            Kind = "opportunity_open",
            Title = "New opportunity: T",
            Body = "B",
            Path = "/opportunities/t",
            Topic = "open-abc",
        };
        var json = Pushes.Payload(message, "https://x.example/logo.png");
        Assert.Contains("\"title\":\"New opportunity: T\"", json);
        Assert.Contains("\"path\":\"/opportunities/t\"", json);
        // The tray collapses on the tag the way the push service does on the topic.
        Assert.Contains("\"tag\":\"open-abc\"", json);
        Assert.Contains("\"icon\":\"https://x.example/logo.png\"", json);
        Assert.Contains("\"icon\":null", Pushes.Payload(message, null));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, true)]
    [InlineData(HttpStatusCode.Gone, true)]
    [InlineData(HttpStatusCode.TooManyRequests, false)]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    [InlineData(HttpStatusCode.Created, false)]
    public void Only_a_vanished_subscription_retires_the_device(HttpStatusCode status, bool gone) =>
        Assert.Equal(gone, Pushes.Gone(status));

    [Fact]
    public void Broadcast_emails_say_why_they_arrived()
    {
        var opened = Emails.OpportunityOpened("Realtime Chat", "realtime-chat", "Ayesha Karim", 1500m, "USD",
            new DateTimeOffset(2026, 9, 17, 12, 0, 0, TimeSpan.Zero));
        Assert.Contains("Realtime Chat", opened.Subject);
        Assert.Contains("1,500 USD", opened.Subject);
        Assert.Contains("Ayesha Karim", opened.TextBody);
        Assert.Contains("2026-09-17 12:00 UTC", opened.TextBody);
        Assert.Contains("just opened", opened.TextBody);
        // A later start day is said, and entry is open from now all the same.
        var ahead = Emails.OpportunityOpened("Realtime Chat", "realtime-chat", "Ayesha Karim", 1500m, "USD",
            new DateTimeOffset(2026, 9, 17, 12, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 9, 10, 0, 0, 0, TimeSpan.Zero));
        Assert.Contains("just opened", ahead.TextBody);
        Assert.Contains("Entry is open now, and the work starts 2026-09-10 UTC.", ahead.TextBody);
        // The one thing an opted-in reader needs to know about the mail itself.
        Assert.Contains("You asked to hear", opened.TextBody);
        Assert.Equal("/opportunities/realtime-chat", opened.ActionPath);
        // The recipient is unknown here; the fan-out adds the opt-out.
        Assert.Null(opened.UnsubscribePath);

        var won = Emails.WinnerAnnounced("Realtime Chat", "realtime-chat", "Nadia Rahman", 1500m, "USD");
        Assert.Contains("Nadia Rahman", won.Subject);
        Assert.Contains("1,500 USD", won.TextBody);
        Assert.Contains("You asked to hear", won.TextBody);
    }

    // ------------------------------------------------------------- inbox

    [Fact]
    public void An_email_about_something_the_reader_is_party_to_makes_an_inbox_line_from_the_same_words()
    {
        var user = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var content = Emails.ApplicationReceived("Realtime Chat", "realtime-chat", "Bilal Hossain", 80, 3);
        var line = Inbox.FromEmail(user, "application_received", content, now)!;

        Assert.Equal(user, line.UserId);
        Assert.Equal("application_received", line.Kind);
        Assert.Equal(content.Subject, line.Title);
        // The first paragraph is what happened; the click stands in for the rest.
        Assert.StartsWith("Bilal Hossain applied to compete", line.Body);
        Assert.DoesNotContain("\n", line.Body);
        Assert.DoesNotContain("Nothing happens until you decide", line.Body);
        Assert.Equal("/opportunities/realtime-chat", line.Path);
        Assert.Equal(now, line.CreatedAtUtc);
        Assert.Null(line.ReadAtUtc);
        Assert.NotEqual(Guid.Empty, line.Id);
    }

    [Theory]
    [InlineData("welcome")]
    [InlineData("confirm_code")]
    [InlineData("password_reset")]
    [InlineData("account_invitation")]
    [InlineData("digest")]
    [InlineData("opportunity_open")]
    [InlineData("winner_announced")]
    public void The_accounts_own_plumbing_and_the_broadcasts_make_no_line_from_their_email(string kind)
    {
        // A reset link or an invitation carries a token; a code is a code;
        // the digest repeats the inbox; and the broadcasts are added by
        // the fan-out, so a subscriber without email hears too.
        var content = Emails.PasswordReset("secret-token");
        Assert.Null(Inbox.FromEmail(Guid.NewGuid(), kind, content, DateTimeOffset.UtcNow));
        Assert.Contains(kind, Inbox.Silent);
    }

    [Fact]
    public void Every_email_whose_link_carries_a_secret_is_silent()
    {
        // Held by kind, since the kind is what the call sites say; the two
        // links that carry a token are the reset and the invitation.
        Assert.Contains("token=", Emails.PasswordReset("t").ActionPath);
        Assert.Contains("token=", Emails.AccountInvitation("t", Roles.Freelancer, 7).ActionPath);
        Assert.Contains("password_reset", Inbox.Silent);
        Assert.Contains("account_invitation", Inbox.Silent);
        Assert.Contains("confirm_code", Inbox.Silent);
    }

    [Fact]
    public void A_broadcast_line_says_what_the_push_says()
    {
        var id = Guid.NewGuid();
        var push = Pushes.OpportunityOpened(id, "Realtime Chat", "realtime-chat", "Ayesha Karim", 1500m, "USD", null);
        var line = Inbox.Line(Guid.NewGuid(), "opportunity_open", push.Title, push.Body, push.Path, DateTimeOffset.UtcNow);
        Assert.Equal("New opportunity: Realtime Chat", line.Title);
        Assert.Contains("1,500 USD", line.Body);
        Assert.Equal("/opportunities/realtime-chat", line.Path);
    }

    [Fact]
    public void A_line_fits_its_columns_and_ends_on_a_word()
    {
        var longWords = string.Join(' ', Enumerable.Repeat("notification", 60));
        var line = Inbox.Line(Guid.NewGuid(), "k", longWords, longWords, null, DateTimeOffset.UtcNow);
        Assert.True(line.Title.Length <= Notification.MaxTitleLength);
        Assert.True(line.Body.Length <= Notification.MaxBodyLength);
        Assert.EndsWith("notification…", line.Title);
        Assert.EndsWith("notification…", line.Body);
        Assert.Null(line.Path);
        // Short text is kept as it is.
        Assert.Equal("Won.", Inbox.Fit("Won.", 10));
    }

    [Fact]
    public void The_first_paragraph_is_the_line_and_its_breaks_are_spaces()
    {
        Assert.Equal("One line two.", Inbox.FirstParagraph("  One line\ntwo.\n\nThe rest.\n"));
        Assert.Equal("Only.", Inbox.FirstParagraph("Only."));
    }

    [Fact]
    public void Inbox_lines_are_kept_ninety_days_unless_the_setting_says_otherwise()
    {
        var now = new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(90, InboxRetention.Days(null));
        Assert.Equal(90, InboxRetention.Days("nonsense"));
        Assert.Equal(90, InboxRetention.Days("-1"));
        Assert.Equal(30, InboxRetention.Days(" 30 "));
        Assert.Equal(now.AddDays(-90), InboxRetention.CutOff(null, now));
        Assert.Null(InboxRetention.CutOff("0", now));
        var setting = Assert.Single(SettingsRegistry.All, s => s.Key == InboxRetention.Key);
        Assert.Equal("limits", setting.Group);
        Assert.Equal("90", setting.Default);
    }

    [Fact]
    public void The_inbox_page_is_one_more_than_it_shows_and_never_more_than_fifty()
    {
        Assert.Equal(20, NotificationService.PageSize);
        Assert.Equal(50, NotificationService.MaxPageSize);
    }

    // ------------------------------------------------------- unsubscribe

    [Fact]
    public void The_unsubscribe_token_names_its_user_and_nobody_else()
    {
        var tokens = new UnsubscribeTokens(new EphemeralDataProtectionProvider());
        var user = Guid.NewGuid();
        var token = tokens.Create(user);

        Assert.Equal(user, tokens.Read(token));
        Assert.Equal(user, tokens.Read(" " + token + " ")); // an email client's whitespace
        // The path is what the email carries; it has to survive a URL untouched.
        Assert.StartsWith("/notifications/unsubscribe?token=", tokens.Path(user));
        Assert.Matches("^[A-Za-z0-9_-]+$", token);

        Assert.Null(tokens.Read(token[..^3] + "abc")); // tampered
        Assert.Null(tokens.Read(token[..10]));         // truncated
        Assert.Null(tokens.Read(user.ToString("N")));  // the bare id is not a token
        Assert.Null(tokens.Read(""));
        Assert.Null(tokens.Read(null));
    }

    [Fact]
    public void The_footer_appears_only_on_mail_that_was_opted_into()
    {
        const string stop = "https://x.example/notifications/unsubscribe?token=abc";

        var text = EmailRender.Text("A", "Body.", "Go", "https://x.example/t", "P", stop);
        Assert.Contains("One click stops these emails: " + stop, text);
        Assert.DoesNotContain("stops these emails",
            EmailRender.Text("A", "Body.", "Go", "https://x.example/t", "P"));

        var html = EmailRender.Html("A", "Body.", "Go", "https://x.example/t", "P", "#0f766e", stop + "&x=<1>");
        Assert.Contains("href=\"" + stop + "&amp;x=&lt;1&gt;\"", html);
        Assert.Contains("stops these emails", html);
        Assert.DoesNotContain("stops these emails",
            EmailRender.Html("A", "Body.", "Go", "https://x.example/t", "P", "#0f766e"));

        // The header mail clients turn into their own unsubscribe button.
        var mime = EmailSender.Compose("P", "no-reply@x.example", "A", "a@x.example", "S", "Body.", null, null,
            "#0f766e", stop);
        Assert.Equal("<" + stop + ">", mime.Headers["List-Unsubscribe"]);
        Assert.Null(EmailSender.Compose("P", "no-reply@x.example", "A", "a@x.example", "S", "Body.", null, null,
            "#0f766e").Headers["List-Unsubscribe"]);
    }

    [Fact]
    public void A_queued_broadcast_keeps_its_opt_out_with_it()
    {
        // Notify copies the path onto the row; the worker absolutizes it at
        // send time like the action, so a corrected public URL fixes both.
        var content = Emails.OpportunityOpened("T", "t", "C", 1m, "USD", null) with
        {
            UnsubscribePath = "/notifications/unsubscribe?token=abc",
        };
        Assert.Equal("/notifications/unsubscribe?token=abc", content.UnsubscribePath);
        Assert.Matches(new Regex("^/"), content.UnsubscribePath);
    }
}
