using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Services.Email;
using WinnersPortal.Services.GitHub;
using WinnersPortal.Services.Phone;
using WinnersPortal.Services.Settings;
using WinnersPortal.Services.Storage;
using Xunit;

namespace WinnersPortal.Api.Tests;

/// <summary>
/// Several setups per connection, one active (Settings/Setups.cs): how a
/// setup's values are keyed, what a list may hold, and — for each kind —
/// which setup the portal picks. The picking is the part that strands work
/// when it is wrong, so it is pinned here, one kind at a time.
/// </summary>
public class SetupsTests
{
    private static SetupValues Setup(string id, bool enabled, params (string Key, string? Value)[] values) =>
        new(id, id, enabled, values.ToDictionary(v => v.Key, v => v.Value));

    // ------------------------------------------------------------ keys

    [Fact]
    public void The_main_setup_keeps_the_original_keys_and_any_other_carries_its_id()
    {
        Assert.Equal("github.appId", Setups.FieldKey("github.appId", Setups.MainId));
        Assert.Equal("github.s1a2b3c4d.appId", Setups.FieldKey("github.appId", "s1a2b3c4d"));
        Assert.Equal("phone.s1a2b3c4d.gatewayAuthHeader", Setups.FieldKey("phone.gatewayAuthHeader", "s1a2b3c4d"));
        Assert.Equal(("ai.apiKey", "s0000ffff"), Setups.ParseScoped("ai.s0000ffff.apiKey"));
    }

    [Theory]
    [InlineData("github.appId")]               // the main setup's own key
    [InlineData("ai.features.entryDigest")]    // a group setting, not an id
    [InlineData("ai.s1a2b3c4d.enabled")]       // the master switch belongs to no setup
    [InlineData("ai.s1a2b3c4d.features.spamFilter")]
    [InlineData("github.S1A2B3C4D.appId")]     // ids are the portal's, lower case
    [InlineData("github.main.appId")]          // main is never spelled into a key
    [InlineData("storage.s1a2b3c4d.appId")]    // a field of another kind
    [InlineData("github.s1a2b3c4d")]
    public void Only_a_setup_field_under_a_given_id_parses_as_scoped(string key) =>
        Assert.Null(Setups.ParseScoped(key));

    [Fact]
    public void A_scoped_key_is_its_field_without_a_default_that_only_described_the_local_stack()
    {
        var endpoint = SettingsRegistry.Find("storage.s1a2b3c4d.endpoint")!;
        Assert.Equal("storage.s1a2b3c4d.endpoint", endpoint.Key);
        Assert.Null(endpoint.Default);
        Assert.Equal("http://localhost:9000", SettingsRegistry.Find("storage.endpoint")!.Default);

        // A convention stays: a new store signs for us-east-1 unless told otherwise.
        Assert.Equal("us-east-1", SettingsRegistry.Find("storage.s1a2b3c4d.region")!.Default);
        Assert.True(SettingsRegistry.Find("github.s1a2b3c4d.privateKey")!.IsSecret);
        Assert.Null(SettingsRegistry.Find("email.s1a2b3c4d.smtpHost")!.Default);
        Assert.Equal("storage.region", SettingsRegistry.MainKey("storage.s1a2b3c4d.region"));
        Assert.Null(SettingsRegistry.Find("storage.s1a2b3c4d.nothing"));
    }

    [Fact]
    public void Every_setup_field_and_list_is_a_registered_setting_of_its_own_group()
    {
        foreach (var kind in Setups.Kinds)
        {
            Assert.Contains(SettingsRegistry.Groups, g => g.Name == kind.Group);
            var list = SettingsRegistry.Find(kind.ListKey);
            Assert.True(list is { HelpRequired: true } && list.Group == kind.Group, kind.ListKey);
            foreach (var field in kind.Fields)
                Assert.Equal(kind.Group, SettingsRegistry.Find(field)?.Group);
        }
        // Only an address on the bundled stack is a main-only default.
        Assert.Equal(
            ["email.fromAddress", "email.smtpHost", "email.smtpPort", "storage.endpoint"],
            SettingsRegistry.All.Where(d => d.MainOnlyDefault).Select(d => d.Key).Order());
    }

    // ------------------------------------------------------------ lists

    [Fact]
    public void An_untouched_list_is_the_main_setup_active()
    {
        Assert.Equal([new SetupEntry(Setups.MainId, Setups.MainName, true)], Setups.Parse(null));
        Assert.Equal(Setups.Initial, Setups.Parse("not json"));
        Assert.Equal(Setups.Initial, Setups.Parse("""[{"id":"x","name":"Bad","enabled":true}]"""));
    }

    [Fact]
    public void A_list_reads_back_as_written_with_names_trimmed()
    {
        var (list, problem) = Setups.Read("""[{"id":"s1a2b3c4d","name":"  Backup  ","enabled":true},{"id":"main","name":"Main"}]""");
        Assert.Null(problem);
        Assert.Equal([new SetupEntry("s1a2b3c4d", "Backup", true), new SetupEntry("main", "Main", false)], list);
        Assert.Equal(list, Setups.Parse(Setups.Write(list!)));
    }

    [Theory]
    [InlineData("""{"id":"main"}""", "not valid JSON")]
    [InlineData("""[{"id":"s123","name":"A","enabled":true}]""", "id")]
    [InlineData("""[{"name":"A","enabled":true}]""", "id")]
    [InlineData("""[{"id":"main","name":"  ","enabled":true}]""", "needs a name")]
    [InlineData("""[{"id":"main","name":"A"},{"id":"main","name":"B"}]""", "twice")]
    [InlineData("""[{"id":"main","name":"Backup"},{"id":"s1a2b3c4d","name":"BACKUP"}]""", "called")]
    [InlineData("""[null]""", "id")]
    public void A_list_the_portal_could_not_use_is_refused_with_a_reason(string json, string says)
    {
        var (list, problem) = Setups.Read(json);
        Assert.Null(list);
        Assert.Contains(says, problem);
    }

    [Fact]
    public void The_list_the_settings_screen_sends_is_the_list_the_portal_reads()
    {
        // Copied from the screen's save, as JSON.stringify writes it.
        const string sent = """[{"id":"main","name":"Main","enabled":false},{"id":"s1a2b3c4d","name":"Relay two","enabled":true}]""";
        var (list, problem) = Setups.Read(sent);
        Assert.Null(problem);
        Assert.Equal(sent, Setups.Write(list!));
    }

    [Fact]
    public void A_connection_holds_at_most_ten_setups()
    {
        var eleven = Enumerable.Range(0, 11).Select(i => new SetupEntry($"s{i:x8}", $"Setup {i}", i == 0));
        Assert.Contains("at most 10", Setups.Read(Setups.Write(eleven)).Problem);
        Assert.Null(Setups.Read(Setups.Write(eleven.Take(10))).Problem);
    }

    [Fact]
    public void Only_one_setup_can_be_active_and_none_is_allowed()
    {
        Assert.Contains("Only one setup can be active",
            Setups.Read("""[{"id":"main","name":"A","enabled":true},{"id":"s1a2b3c4d","name":"B","enabled":true}]""").Problem);
        Assert.Null(Setups.Read("""[{"id":"main","name":"A","enabled":false},{"id":"s1a2b3c4d","name":"B","enabled":false}]""").Problem);
        Assert.Null(Setups.Read("[]").Problem);
    }

    [Fact]
    public void A_list_saved_with_several_switched_on_keeps_every_setup_and_the_first_on_as_active()
    {
        var list = Setups.Parse(
            """[{"id":"main","name":"A","enabled":false},{"id":"s1a2b3c4d","name":"B","enabled":true},{"id":"s00000001","name":"C","enabled":true}]""");
        Assert.Equal(
            [new SetupEntry("main", "A", false), new SetupEntry("s1a2b3c4d", "B", true), new SetupEntry("s00000001", "C", false)],
            list);
    }

    [Fact]
    public void The_active_setup_is_the_one_switched_on() =>
        Assert.Equal("c", Setups.Active([Setup("a", false), Setup("b", false), Setup("c", true)])?.Id);

    // ----------------------------------------------------------- GitHub

    private static SetupValues App(string id, bool enabled, string? org, string? appId = "1", string? pem = "pem") =>
        Setup(id, enabled, ("github.appId", appId), ("github.privateKey", pem), ("github.organization", org));

    [Fact]
    public void A_repository_goes_through_the_setup_for_its_organization_even_an_inactive_one()
    {
        var setups = new[] { App("new", true, "opportunities-2027"), App("old", false, "Opportunities-2026") };
        Assert.Equal("old", GitHubService.Route(setups, "opportunities-2026")?.Id);
        Assert.Equal("new", GitHubService.Route(setups, "opportunities-2027")?.Id);
    }

    [Fact]
    public void Between_two_setups_on_one_organization_the_active_one_wins()
    {
        var setups = new[] { App("off", false, "acme"), App("on", true, "acme") };
        Assert.Equal("on", GitHubService.Route(setups, "acme")?.Id);
    }

    [Fact]
    public void An_owner_no_setup_answers_for_goes_through_the_active_setup()
    {
        var setups = new[] { App("old", false, "acme"), App("active", true, "opportunities") };
        Assert.Equal("active", GitHubService.Route(setups, "a-client-who-was-paid")?.Id);
        Assert.Equal("active", GitHubService.Route(setups, null)?.Id);
        Assert.Null(GitHubService.Route([App("old", false, "acme")], "someone"));
    }

    [Fact]
    public void An_incomplete_active_setup_is_not_covered_for_by_an_inactive_one()
    {
        var setups = new[] { App("old", false, "acme"), App("keyless", true, "opportunities", pem: " ") };
        Assert.Null(GitHubService.Route(setups, "opportunities"));
        Assert.Null(GitHubService.Route(setups, null));
        Assert.Equal("old", GitHubService.Route(setups, "acme")?.Id); // its own repositories still reachable
    }

    [Theory]
    [InlineData("/repos/acme/entry-1/collaborators/nadia", "acme")]
    [InlineData("/repos/acme/entry-1", "acme")]
    [InlineData("/orgs/opportunities%2D2026/repos", "opportunities-2026")]
    [InlineData("/app/installations", null)]
    [InlineData("/repos", null)]
    [InlineData("repos/acme/x", null)]
    public void A_path_names_its_owner(string path, string? owner) =>
        Assert.Equal(owner, GitHubService.OwnerOf(path));

    // A transfer cannot go as the App, so the token it goes on is judged
    // before GitHub is asked — the kinds GitHub is known to refuse never leave.

    [Theory]
    [InlineData(null, "no transfer token is saved")]
    [InlineData("   ", "no transfer token is saved")]
    [InlineData("github_pat_11ABCDEFG0123456789_abcdef", "fine-grained")]
    [InlineData("ghs_installationTokenLooksLikeThis", "app token")]
    [InlineData("ghu_appUserTokenLooksLikeThis", "app token")]
    [InlineData("ghr_refreshTokenLooksLikeThis", "app token")]
    public void A_transfer_token_github_refuses_is_named_before_it_is_sent(string? token, string says) =>
        Assert.Contains(says, GitHubService.TransferTokenProblem(token, "acme"));

    [Theory]
    [InlineData("ghp_classicTokenLooksLikeThis0123456789")]
    [InlineData(" ghp_classicTokenWithStraySpaces ")]
    [InlineData("0123456789abcdef0123456789abcdef01234567")] // a classic token from before the prefixes
    public void A_classic_token_may_be_sent(string token) =>
        Assert.Null(GitHubService.TransferTokenProblem(token, "acme"));

    [Fact]
    public void A_missing_transfer_token_names_the_organization_it_must_come_from() =>
        Assert.Contains("an owner of opportunities-2026", GitHubService.TransferTokenProblem("", "opportunities-2026"));

    private static GitHubRepo Repo(string owner) => new(7, $"{owner}/logo-rocky", owner, "main", false);

    [Fact]
    public void A_transfer_still_in_the_organization_is_pending() =>
        Assert.Equal(GitHubService.TransferSight.Pending, GitHubService.ReadTransfer(Repo("WinnersPortal"), "astrik"));

    [Fact]
    public void A_transfer_seen_under_its_recipient_has_arrived_whatever_the_case() =>
        Assert.Equal(GitHubService.TransferSight.Arrived, GitHubService.ReadTransfer(Repo("Astrik"), "astrik"));

    [Fact]
    public void A_repository_the_app_can_no_longer_read_has_left() =>
        Assert.Equal(GitHubService.TransferSight.Left, GitHubService.ReadTransfer(null, "astrik"));

    [Fact]
    public void The_transfer_token_is_a_field_of_every_github_setup_and_stored_as_a_secret()
    {
        Assert.Contains(GitHubService.TransferTokenKey, Setups.GitHub.Fields);
        Assert.True(SettingsRegistry.Find(Setups.FieldKey(GitHubService.TransferTokenKey, "s1a2b3c4d"))?.IsSecret);
    }


    [Fact]
    public void Connecting_remembers_the_setup_its_code_redeems_against()
    {
        var provider = new EphemeralDataProtectionProvider();
        var user = Guid.NewGuid();
        var state = GitHubAuthService.CreateState(provider, user, DateTimeOffset.UtcNow, "/profile", "s1a2b3c4d");
        var parsed = GitHubAuthService.ParseState(provider, state)!;
        Assert.Equal((user, "/profile", "s1a2b3c4d"), (parsed.UserId, parsed.Next, parsed.Setup));

        var main = GitHubAuthService.ParseState(provider,
            GitHubAuthService.CreateState(provider, user, DateTimeOffset.UtcNow, null, "main"))!;
        Assert.Equal((null, "main"), (main.Next, main.Setup));
        Assert.Null(GitHubAuthService.ParseState(provider,
            GitHubAuthService.CreateState(provider, user, DateTimeOffset.UtcNow, "/x", "not:an:id"))!.Setup);
    }

    [Fact]
    public void A_setup_is_counted_as_holding_the_repositories_in_its_organization()
    {
        var setups = new[] { App("a", true, "Acme"), App("b", false, "old-org"), App("c", true, null) };
        var held = SetupUsage.ReposBySetup(setups, ["acme/x", "ACME/y", "old-org/z", "a-client/paid-over"]);
        Assert.Equal("2 repositories in Acme", held["a"]);
        Assert.Equal("1 repository in old-org", held["b"]);
        Assert.False(held.ContainsKey("c"));
    }

    // ---------------------------------------------------------- storage

    [Fact]
    public void A_file_from_before_there_were_several_stores_lives_in_the_main_one()
    {
        Assert.Equal(Setups.MainId, StorageService.SetupOf(null));
        Assert.Equal("s1a2b3c4d", StorageService.SetupOf("s1a2b3c4d"));
        var files = SetupUsage.FilesBySetup([(null, 3), ("main", 2), ("s1a2b3c4d", 1), ("s00000000", 0)]);
        Assert.Equal(new Dictionary<string, int> { ["main"] = 5, ["s1a2b3c4d"] = 1 }, files);
    }

    [Fact]
    public void The_files_a_store_holds_are_counted_in_sql()
    {
        // No database: EF renders the SQL from the model, which catches a
        // grouping on the nullable column it could not translate.
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=nowhere;Database=none;Username=x;Password=x")
            .Options);
        var sql = SetupUsage.FileCounts(db).Select(q => q.ToQueryString()).ToList();
        Assert.Equal(3, sql.Count);
        Assert.Contains("GROUP BY a.\"StorageSetup\"", sql[0]);
        Assert.Contains("GROUP BY s.\"StorageSetup\"", sql[1]);
        Assert.Contains("\"ZipStorageKey\" IS NOT NULL", sql[2]);
        Assert.Contains("GROUP BY e.\"ZipStorageSetup\"", sql[2]);
        Assert.All(sql, s => Assert.Contains("count(*)", s, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_store_needs_its_endpoint_bucket_and_both_keys()
    {
        var complete = Setup("s", true,
            ("storage.endpoint", "http://storage:9000"), ("storage.bucket", "b"),
            ("storage.accessKey", "a"), ("storage.secretKey", "k"), ("storage.publicUrl", null), ("storage.region", null));
        var config = StorageService.Config(complete)!;
        Assert.Equal("storage:9000", config.PublicUrl.Authority); // no public URL: the endpoint signs for browsers too
        Assert.Equal("us-east-1", config.Region);
        Assert.Null(StorageService.Config(complete with
        {
            Values = new Dictionary<string, string?>(complete.Values) { ["storage.secretKey"] = "" },
        }));
        Assert.Null(StorageService.Config(Setup("blank", true)));
    }

    // ------------------------------------------------------ email, phone

    [Fact]
    public void An_smtp_setup_is_its_host_its_port_and_its_own_sender()
    {
        var server = Setup("s", true, ("email.smtpHost", "smtp.example.com"), ("email.smtpPort", "587"),
            ("email.fromAddress", "opportunities@example.com"));
        Assert.True(EmailSender.Ready(server));
        Assert.Equal(587, EmailSender.Port(server));
        Assert.Equal("opportunities@example.com", EmailSender.From(server));
        Assert.Equal("smtp.example.com:587", EmailSender.Where(server));
        Assert.False(EmailSender.Ready(Setup("blank", true, ("email.smtpHost", " "))));
        Assert.Equal(25, EmailSender.Port(Setup("blank", true)));
        Assert.Equal(EmailSender.DefaultFrom, EmailSender.From(null));
    }

    [Fact]
    public void An_email_setup_sends_by_smtp_until_it_names_a_mail_api()
    {
        Assert.Equal(EmailProviders.Smtp, EmailProviders.Of(Setup("s", true)));
        Assert.Equal(EmailProviders.Smtp, EmailProviders.Of(Setup("s", true, (EmailProviders.ProviderKey, " "))));
        Assert.Equal(EmailProviders.Mailgun, EmailProviders.Of(Setup("s", true, (EmailProviders.ProviderKey, " Mailgun "))));
    }

    [Fact]
    public void A_mail_api_setup_needs_its_key_and_mailgun_its_domain_too()
    {
        var mailgun = Setup("s", true, (EmailProviders.ProviderKey, "mailgun"), (EmailProviders.ApiKeyKey, "key-1"),
            (EmailProviders.DomainKey, " mg.example.com/ "));
        Assert.True(EmailSender.Ready(mailgun));
        Assert.Equal("Mailgun (mg.example.com)", EmailSender.Where(mailgun));
        Assert.Equal("https://api.mailgun.net", EmailProviders.MailgunBase(mailgun));
        Assert.Equal("https://api.eu.mailgun.net", EmailProviders.MailgunBase(With(mailgun, EmailProviders.RegionKey, "EU")));
        Assert.EndsWith("Mailgun needs the sending domain.", EmailSender.Problem(With(mailgun, EmailProviders.DomainKey, "")));
        Assert.EndsWith("Mailgun needs an API key.", EmailSender.Problem(With(mailgun, EmailProviders.ApiKeyKey, null)));
        // An SMTP host left from before the switch does not make a keyless Mailgun setup ready.
        Assert.False(EmailSender.Ready(With(With(mailgun, EmailProviders.ApiKeyKey, null), "email.smtpHost", "smtp.example.com")));

        var brevo = Setup("s", true, (EmailProviders.ProviderKey, "brevo"), (EmailProviders.ApiKeyKey, "xkeysib-1"));
        Assert.True(EmailSender.Ready(brevo));
        Assert.Equal("Brevo", EmailSender.Where(brevo));
        Assert.EndsWith("Brevo needs an API key.", EmailSender.Problem(With(brevo, EmailProviders.ApiKeyKey, " ")));

        Assert.Contains("does not know", EmailSender.Problem(
            Setup("s", true, (EmailProviders.ProviderKey, "sendgrid"), ("email.smtpHost", "smtp.example.com"))));
    }

    [Fact]
    public void A_refused_sign_in_says_what_the_server_said_and_what_usually_causes_it()
    {
        var brevo = Setup("s", true, ("email.smtpHost", " smtp-relay.brevo.com "), ("email.smtpUser", "a1@smtp-brevo.com"),
            ("email.smtpPassword", "xsmtpsib-1"));
        var answer = EmailSender.SignInRefused(brevo, "535: 5.7.8 Authentication failed.");
        Assert.StartsWith("The SMTP server refused the sign-in — check the SMTP user and password.", answer);
        Assert.Contains("It answered: “535: 5.7.8 Authentication failed”.", answer);
        Assert.Contains("the password an SMTP key (xsmtpsib-…)", answer);
        Assert.DoesNotContain("space", answer);

        Assert.Contains("That password is a Brevo API key",
            EmailSender.SignInRefused(With(brevo, "email.smtpPassword", "xkeysib-1"), null));
        Assert.Contains("begins or ends with a space",
            EmailSender.SignInRefused(With(brevo, "email.smtpPassword", "xsmtpsib-1 "), null));
        // Any other server: its words and nothing presumed about it.
        Assert.Equal("The SMTP server refused the sign-in — check the SMTP user and password.",
            EmailSender.SignInRefused(Relay, " "));

        // A refusal of the address the server sends from is not about the
        // user or password, so the answer points at the list of addresses.
        var byAddress = EmailSender.SignInRefused(brevo, "525: 5.7.1 Unauthorized IP address");
        Assert.StartsWith("The SMTP server refused the sign-in because of the IP address this server sends from.", byAddress);
        Assert.Contains("It answered: “525: 5.7.1 Unauthorized IP address”.", byAddress);
        Assert.Contains("Security → Authorized IPs", byAddress);
        Assert.DoesNotContain("check the SMTP user and password", byAddress);
        Assert.DoesNotContain("xsmtpsib", byAddress);
        Assert.EndsWith("to the mail service's list of allowed addresses.",
            EmailSender.SignInRefused(Relay, "550 5.7.1 Client IP not allowed"));
        // "IP" only as a word: a reply that merely contains the letters is a sign-in refusal as before.
        Assert.StartsWith("The SMTP server refused the sign-in — check",
            EmailSender.SignInRefused(Relay, "535 5.7.8 Invalid SKIP token"));
    }

    [Fact]
    public void The_email_fields_a_paste_can_pad_are_saved_without_the_padding_and_the_password_as_typed()
    {
        foreach (var key in new[] { "email.smtpHost", "email.smtpPort", "email.fromAddress", "email.smtpUser",
                     EmailProviders.ApiKeyKey, EmailProviders.DomainKey })
        {
            Assert.Equal("x@y", SettingsService.Entered(SettingsRegistry.Find(key)!, " x@y\t"));
            Assert.Equal("x@y", SettingsService.Entered(SettingsRegistry.Find(Setups.FieldKey(key, "s1a2b3c4d"))!, "x@y "));
        }
        Assert.Equal(" pass ", SettingsService.Entered(SettingsRegistry.Find("email.smtpPassword")!, " pass "));
        Assert.Equal("opportunities@example.com",
            EmailSender.From(Setup("s", true, ("email.fromAddress", " opportunities@example.com "))));
    }

    [Fact]
    public void A_certificate_is_refused_for_every_fault_but_a_revocation_list_that_could_not_be_fetched()
    {
        const SslPolicyErrors chainErrors = SslPolicyErrors.RemoteCertificateChainErrors;
        Assert.True(EmailSender.CertificateAcceptable(SslPolicyErrors.None, []));
        Assert.True(EmailSender.CertificateAcceptable(chainErrors,
            [X509ChainStatusFlags.RevocationStatusUnknown | X509ChainStatusFlags.OfflineRevocation]));
        Assert.True(EmailSender.CertificateAcceptable(chainErrors,
            [X509ChainStatusFlags.NoError, X509ChainStatusFlags.RevocationStatusUnknown]));

        Assert.False(EmailSender.CertificateAcceptable(chainErrors, [X509ChainStatusFlags.Revoked]));
        Assert.False(EmailSender.CertificateAcceptable(chainErrors,
            [X509ChainStatusFlags.RevocationStatusUnknown, X509ChainStatusFlags.NotTimeValid]));
        Assert.False(EmailSender.CertificateAcceptable(chainErrors, [X509ChainStatusFlags.UntrustedRoot]));
        Assert.False(EmailSender.CertificateAcceptable(chainErrors, [X509ChainStatusFlags.PartialChain]));
        Assert.False(EmailSender.CertificateAcceptable(
            SslPolicyErrors.RemoteCertificateNameMismatch | chainErrors, [X509ChainStatusFlags.RevocationStatusUnknown]));
        Assert.False(EmailSender.CertificateAcceptable(SslPolicyErrors.RemoteCertificateNotAvailable, []));
    }

    /// <summary>
    /// The real MailKit against a server that turns every sign-in down: the
    /// login arrives without the spaces a paste left around it, and the
    /// refusal reaches the test's answer in the server's words.
    /// </summary>
    [Fact]
    public async Task A_padded_login_reaches_the_server_trimmed_and_its_refusal_is_quoted()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var authLines = new List<string>();
        var server = Task.Run(async () =>
        {
            using var tcp = await listener.AcceptTcpClientAsync();
            using var stream = tcp.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII);
            using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
            await writer.WriteLineAsync("220 fake ESMTP");
            while (await reader.ReadLineAsync() is { } line)
            {
                if (line.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase))
                {
                    await writer.WriteLineAsync("250-fake");
                    await writer.WriteLineAsync("250 AUTH PLAIN");
                }
                else if (line.StartsWith("AUTH", StringComparison.OrdinalIgnoreCase))
                {
                    authLines.Add(line);
                    await writer.WriteLineAsync("535 5.7.8 Authentication failed");
                }
                else if (line.StartsWith("QUIT", StringComparison.OrdinalIgnoreCase))
                {
                    await writer.WriteLineAsync("221 bye");
                    break;
                }
                else await writer.WriteLineAsync("250 ok");
            }
        });

        var setup = Setup("s", true, ("email.smtpHost", "127.0.0.1"), ("email.smtpPort", port.ToString()),
            ("email.smtpUser", " a1@smtp-brevo.com\t"), ("email.smtpPassword", "xsmtpsib-1"));
        using (var client = new MailKit.Net.Smtp.SmtpClient())
        {
            var refused = await Assert.ThrowsAsync<MailKit.Security.AuthenticationException>(
                () => EmailSender.ConnectAsync(client, setup, CancellationToken.None));
            Assert.Contains("It answered: “535: 5.7.8 Authentication failed”.", EmailSender.SignInRefused(setup, refused.Message));
            await client.DisconnectAsync(true);
        }
        await server.WaitAsync(TimeSpan.FromSeconds(10));
        listener.Stop();

        var plain = Encoding.UTF8.GetString(Convert.FromBase64String(Assert.Single(authLines).Split(' ')[2]));
        Assert.Equal("\0a1@smtp-brevo.com\0xsmtpsib-1", plain);
    }

    [Fact]
    public void Every_way_of_sending_reads_fields_of_the_email_kind()
    {
        Assert.Equal(EmailProviders.Choices.Select(c => c.Value).Order(), EmailProviders.FieldsOf.Keys.Order());
        Assert.All(EmailProviders.FieldsOf.Values.SelectMany(f => f), f => Assert.Contains(f, Setups.Email.Fields));
        Assert.All(Setups.Email.Later!, f => Assert.Contains(f, Setups.Email.Fields));
        Assert.Equal(EmailProviders.Smtp, SettingsRegistry.Find(EmailProviders.ProviderKey)?.Default);
    }

    [Fact]
    public void A_gateway_setup_names_a_provider_or_a_whole_custom_request()
    {
        Assert.True(PhoneSender.Ready(Setup("s", true, (PhoneSender.ProviderKey, "textbee"))));
        Assert.False(PhoneSender.Ready(Setup("s", true, (PhoneSender.ProviderKey, PhoneProviders.None))));
        Assert.False(PhoneSender.Ready(Setup("s", true)));
        Assert.False(PhoneSender.Ready(Setup("s", true, (PhoneSender.ProviderKey, PhoneProviders.Custom))));
        Assert.True(PhoneSender.Ready(Setup("s", true,
            (PhoneSender.ProviderKey, PhoneProviders.Custom), (PhoneSender.UrlKey, "https://sms.example/send"))));
    }

    // --------------------------------------------------------------- AI

    [Fact]
    public void An_ai_setup_without_a_key_is_not_a_provider()
    {
        Assert.Null(AiOptions.Config(Setup("s", true, ("ai.provider", "anthropic"))));
        var config = AiOptions.Config(new SetupValues("s", "Backup key", true, new Dictionary<string, string?>
        {
            ["ai.apiKey"] = "k", ["ai.provider"] = " ", ["ai.model"] = " ",
        }))!;
        Assert.Equal(("gemini", null, "Backup key"), (config.Provider, config.Model, config.Setup));
    }

    // -------------------------------------------------------- last test

    private static SetupValues With(SetupValues setup, string key, string? value) =>
        setup with { Values = new Dictionary<string, string?>(setup.Values) { [key] = value } };

    private static readonly SetupValues Relay = Setup("s1a2b3c4d", false,
        ("email.smtpHost", "smtp.example.com"), ("email.smtpPort", "587"), ("email.fromAddress", "opportunities@example.com"),
        ("email.smtpUser", "relay"), ("email.smtpPassword", "hunter2"));

    [Fact]
    public void A_test_speaks_for_the_values_it_ran_with_not_the_name_or_the_switch()
    {
        var tested = SetupTestLog.Fingerprint(Setups.Email, Relay);
        Assert.Equal(tested, SetupTestLog.Fingerprint(Setups.Email, Relay with { Name = "Relay two", Enabled = true }));
        Assert.NotEqual(tested, SetupTestLog.Fingerprint(Setups.Email, With(Relay, "email.smtpPassword", "hunter3")));
        Assert.NotEqual(tested, SetupTestLog.Fingerprint(Setups.Email, With(Relay, "email.smtpPort", "465")));
        Assert.Equal(
            SetupTestLog.Fingerprint(Setups.Email, With(Relay, "email.smtpUser", null)),
            SetupTestLog.Fingerprint(Setups.Email, With(Relay, "email.smtpUser", "")));
    }

    [Fact]
    public void A_test_passed_before_the_mail_apis_still_speaks_for_its_smtp_setup()
    {
        // The fingerprint as it was written before the kind had a way of
        // sending to choose: the five SMTP fields, in their order.
        var text = new StringBuilder();
        foreach (var field in new[] { "email.smtpHost", "email.smtpPort", "email.fromAddress", "email.smtpUser", "email.smtpPassword" })
        {
            var value = Relay.Get(field) ?? "";
            text.Append(field).Append('=').Append(value.Length).Append(':').Append(value).Append('\n');
        }
        var before = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));

        Assert.Equal(before, SetupTestLog.Fingerprint(Setups.Email, Relay));
        // The new fields at their defaults, as a stored setup resolves them, change nothing…
        var resolved = With(With(Relay, EmailProviders.ProviderKey, EmailProviders.Smtp), EmailProviders.RegionKey, EmailProviders.RegionUs);
        Assert.Equal(before, SetupTestLog.Fingerprint(Setups.Email, resolved));
        // …and another way of sending, or a key, does.
        Assert.NotEqual(before, SetupTestLog.Fingerprint(Setups.Email, With(resolved, EmailProviders.ProviderKey, EmailProviders.Brevo)));
        Assert.NotEqual(before, SetupTestLog.Fingerprint(Setups.Email, With(resolved, EmailProviders.ApiKeyKey, "xkeysib-1")));
    }

    [Fact]
    public void Values_that_run_together_into_the_same_text_still_fingerprint_apart()
    {
        // Without each value's length in front of it, these two read the same.
        var one = Setup("s", false, ("email.smtpHost", "x\nemail.smtpPort=1"), ("email.smtpPort", ""));
        var two = Setup("s", false, ("email.smtpHost", "x"), ("email.smtpPort", "1\nemail.smtpPort="));
        Assert.NotEqual(SetupTestLog.Fingerprint(Setups.Email, one), SetupTestLog.Fingerprint(Setups.Email, two));
    }

    [Fact]
    public void A_test_stops_vouching_for_a_setup_once_its_values_change()
    {
        var protector = new EphemeralDataProtectionProvider().CreateProtector("WinnersPortal.SetupTests");
        var fingerprint = SetupTestLog.Fingerprint(Setups.Email, Relay);
        var stored = protector.Protect(fingerprint);
        Assert.DoesNotContain(fingerprint, stored, StringComparison.OrdinalIgnoreCase); // kept encrypted

        Assert.False(SetupTestLog.Changed(protector, stored, Setups.Email, Relay with { Enabled = true }));
        Assert.True(SetupTestLog.Changed(protector, stored, Setups.Email, With(Relay, "email.smtpHost", "smtp2.example.com")));
        // A replaced key ring cannot read the old fingerprint, so the setup wants testing again.
        Assert.True(SetupTestLog.Changed(
            new EphemeralDataProtectionProvider().CreateProtector("WinnersPortal.SetupTests"), stored, Setups.Email, Relay));
    }

    [Fact]
    public void A_text_test_is_kept_with_the_number_masked()
    {
        Assert.Equal("The gateway accepted the message (200): {\"to\":\"+92 ••• 4567\",\"id\":88}",
            SetupTestLog.WithoutNumber("The gateway accepted the message (200): {\"to\":\"+923001234567\",\"id\":88}", "+923001234567"));
        Assert.Equal("The gateway answered 400: bad recipient +92 ••• 4567",
            SetupTestLog.WithoutNumber("The gateway answered 400: bad recipient 923001234567", "+923001234567"));
    }

    [Fact]
    public void A_setup_keeps_one_last_test()
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=nowhere;Database=none;Username=x;Password=x")
            .Options);
        var key = db.Model.FindEntityType(typeof(Domain.SetupTest))!.FindPrimaryKey()!;
        Assert.Equal(["Kind", "SetupId"], key.Properties.Select(p => p.Name));
        Assert.All(Setups.Kinds, k => Assert.True(k.Group.Length <= 16));
        Assert.True(Setups.IsValidId(Setups.NewId()) && Setups.NewId().Length <= 16);
    }
}
