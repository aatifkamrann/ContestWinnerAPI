using WinnersPortal.Services.Settings;
using WinnersPortal.Services.Storage;
using Xunit;

namespace WinnersPortal.Api.Tests;

/// <summary>
/// The storage layer's pure parts: the SigV4 presigner (checked against the
/// worked example in AWS's own signature documentation), the file-name
/// laundering, the attachment rules, and a setup's values as the store is
/// signed for and as a failed test reports them. The live round-trip against MinIO
/// is the other half of the proof; these pin the arithmetic.
/// </summary>
public class StorageTests
{
    // ------------------------------------------------------------ presign

    /// <summary>
    /// AWS's published SigV4 presigning example, byte for byte: known keys,
    /// known clock, known signature. If this passes, the canonical request,
    /// key derivation, and encoding all match the spec.
    /// </summary>
    [Fact]
    public void Presigner_reproduces_the_AWS_documentation_example()
    {
        var url = S3Presign.Url(
            new Uri("https://examplebucket.s3.amazonaws.com"),
            bucket: "",
            key: "test.txt",
            accessKey: "AKIAIOSFODNN7EXAMPLE",
            secretKey: "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY",
            region: "us-east-1",
            method: "GET",
            expires: TimeSpan.FromSeconds(86400),
            now: new DateTimeOffset(2013, 5, 24, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(
            "https://examplebucket.s3.amazonaws.com/test.txt"
            + "?X-Amz-Algorithm=AWS4-HMAC-SHA256"
            + "&X-Amz-Credential=AKIAIOSFODNN7EXAMPLE%2F20130524%2Fus-east-1%2Fs3%2Faws4_request"
            + "&X-Amz-Date=20130524T000000Z"
            + "&X-Amz-Expires=86400"
            + "&X-Amz-SignedHeaders=host"
            + "&X-Amz-Signature=aeeed9bbccd4d02ee5c0109b86d86835f995330da4c265957d157751f604d404",
            url.ToString());
    }

    [Fact]
    public void Presigner_uses_path_style_and_keeps_a_nonstandard_port()
    {
        var url = S3Presign.Url(
            new Uri("http://localhost:9000"), "winnersportal", "opportunities/abc/brief/def/logo.png",
            "key", "secret", "us-east-1", "PUT",
            TimeSpan.FromMinutes(15), new DateTimeOffset(2026, 8, 31, 12, 0, 0, TimeSpan.Zero));

        Assert.StartsWith("http://localhost:9000/winnersportal/opportunities/abc/brief/def/logo.png?", url.ToString());
        Assert.Contains("X-Amz-Signature=", url.ToString());
    }

    [Fact]
    public void Response_overrides_are_part_of_the_signature()
    {
        Uri Sign(string disposition) => S3Presign.Url(
            new Uri("http://localhost:9000"), "b", "k", "key", "secret", "us-east-1", "GET",
            TimeSpan.FromMinutes(5), new DateTimeOffset(2026, 8, 31, 12, 0, 0, TimeSpan.Zero),
            [new("response-content-disposition", disposition)]);

        var a = Sign("attachment; filename=\"a.txt\"").Query;
        var b = Sign("attachment; filename=\"b.txt\"").Query;
        string Sig(string q) => q[(q.IndexOf("X-Amz-Signature=", StringComparison.Ordinal) + 16)..];

        // Same everything but the override — a holder of one link cannot
        // rewrite how the file comes back without breaking the signature.
        Assert.NotEqual(Sig(a), Sig(b));
    }

    [Theory]
    [InlineData("simple-name_1.txt", "simple-name_1.txt")]
    [InlineData("hello world.png", "hello%20world.png")]
    [InlineData("a/b", "a%2Fb")]
    [InlineData("tilde~dot.-_", "tilde~dot.-_")]
    public void Rfc3986_encodes_the_way_SigV4_expects(string raw, string encoded) =>
        Assert.Equal(encoded, S3Presign.Rfc3986(raw));

    [Fact]
    public void Canonical_path_encodes_segments_but_keeps_the_slashes() =>
        Assert.Equal("/bucket/a%20b/c.txt", S3Presign.CanonicalPath("bucket", "a b/c.txt"));

    // ------------------------------------------------------ file names

    [Theory]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("..\\..\\windows\\system32\\config", "config")]
    [InlineData("logo.png", "logo.png")]
    [InlineData("  spaced  .pdf", "spaced  .pdf")]
    [InlineData("we\u001bird\tname.txt", "we-ird-name.txt")]
    [InlineData("quote\"angle<pipe|.txt", "quote-angle-pipe-.txt")]
    [InlineData("", "file")]
    [InlineData("...", "file")]
    [InlineData("রঙিন-লোগো.svg", "রঙিন-লোগো.svg")]
    public void SafeFileName_launders_what_browsers_and_attackers_send(string raw, string expected) =>
        Assert.Equal(expected, StorageRules.SafeFileName(raw));

    [Fact]
    public void SafeFileName_caps_length_but_keeps_the_extension()
    {
        var safe = StorageRules.SafeFileName(new string('a', 400) + ".sketch");
        Assert.Equal(StorageRules.MaxFileNameLength, safe.Length);
        Assert.EndsWith(".sketch", safe);
    }

    // ------------------------------------------------------ attachment rules

    private const long Max = 25 * 1024 * 1024;

    [Fact]
    public void A_reasonable_file_is_accepted() =>
        Assert.Null(StorageRules.Problem("logo.png", 512_000, Max, existingCount: 3));

    [Fact]
    public void The_name_is_required() =>
        Assert.NotNull(StorageRules.Problem("  ", 1, Max, 0));

    [Fact]
    public void An_empty_file_is_rejected() =>
        Assert.NotNull(StorageRules.Problem("a.txt", 0, Max, 0));

    [Fact]
    public void The_size_ceiling_is_enforced_and_named_in_megabytes()
    {
        var problem = StorageRules.Problem("big.psd", Max + 1, Max, 0);
        Assert.Contains("25 MB", problem);
    }

    [Fact]
    public void The_attachment_cap_closes_the_brief() =>
        Assert.NotNull(StorageRules.Problem("one-more.txt", 1, Max,
            existingCount: StorageRules.MaxAttachmentsPerOpportunity));

    // ------------------------------------------------------ keys & headers

    [Fact]
    public void Attachment_keys_carry_both_owning_ids()
    {
        var opportunity = Guid.NewGuid();
        var attachment = Guid.NewGuid();
        Assert.Equal(
            $"opportunities/{opportunity:N}/brief/{attachment:N}/logo.png",
            StorageRules.AttachmentKey(opportunity, attachment, "logo.png"));
    }

    [Fact]
    public void Entry_zip_keys_are_stable_per_entry()
    {
        var entry = Guid.NewGuid();
        Assert.Equal($"entries/{entry:N}/final.zip", StorageRules.EntryZipKey(entry));
    }

    [Fact]
    public void Downloads_are_always_attachments_never_pages()
    {
        var disposition = StorageRules.ContentDisposition("brief notes.html");
        Assert.StartsWith("attachment;", disposition);
        Assert.Contains("filename=\"brief notes.html\"", disposition);
    }

    [Fact]
    public void NonAscii_names_survive_via_rfc5987_with_an_ascii_fallback()
    {
        var disposition = StorageRules.ContentDisposition("লোগো.svg");
        Assert.Contains("filename=\"____.svg\"", disposition);
        Assert.Contains("filename*=UTF-8''%E0%A6%B2%E0%A7%8B%E0%A6%97%E0%A7%8B.svg", disposition);
    }

    [Theory]
    [InlineData("image/png", "mockup.png", "image", "image/png")]
    [InlineData("", "brand-mark.svg", "image", "image/svg+xml")]
    [InlineData("application/pdf", "brand-guide.pdf", "pdf", "application/pdf")]
    [InlineData("application/octet-stream", "walkthrough.mp4", "video", "video/mp4")]
    [InlineData("audio/mpeg", "jingle.mp3", "audio", "audio/mpeg")]
    // Excel claims every .csv, and browsers often declare nothing for Markdown.
    [InlineData("application/vnd.ms-excel", "stock.csv", "text", "text/csv")]
    [InlineData("", "NOTES.MD", "text", "text/markdown")]
    [InlineData("application/json", "sample.json", "text", "application/json")]
    [InlineData("text/plain; charset=utf-8", "README", "text", "text/plain")]
    public void Brief_files_a_browser_can_show_get_a_preview(string contentType, string fileName, string kind, string servedAs)
    {
        var preview = StorageRules.Preview(contentType, fileName, 2048);
        Assert.NotNull(preview);
        Assert.Equal(kind, preview.Kind);
        Assert.Equal(servedAs, preview.ContentType);
    }

    [Theory]
    [InlineData("text/html", "brief.html")]
    [InlineData("application/zip", "assets.zip")]
    [InlineData("application/octet-stream", "design.fig")]
    [InlineData(null, "no-extension")]
    public void Anything_else_stays_a_download(string? contentType, string fileName) =>
        Assert.Null(StorageRules.Preview(contentType, fileName, 2048));

    [Fact]
    public void A_preview_is_served_as_its_own_type_never_the_declared_one()
    {
        // An HTML page declared as a picture comes back a broken picture, not a page.
        Assert.Equal("image/png", StorageRules.Preview("text/html", "mockup.png", 2048)!.ContentType);
    }

    [Fact]
    public void Text_too_long_to_glance_at_stays_a_download_while_a_pdf_has_its_own_viewer()
    {
        Assert.NotNull(StorageRules.Preview("text/csv", "stock.csv", StorageRules.MaxTextPreviewBytes));
        Assert.Null(StorageRules.Preview("text/csv", "stock.csv", StorageRules.MaxTextPreviewBytes + 1));
        Assert.NotNull(StorageRules.Preview("application/pdf", "brand-guide.pdf", 20L * 1024 * 1024));
    }

    // ------------------------------------------------- the browser's half

    private const string Page = "https://portal.example.com";

    [Fact]
    public void The_origins_checked_are_the_page_and_the_web_url_each_once()
    {
        Assert.Equal(new[] { Page }, StorageService.BrowserOrigins(Page, Page + "/opportunities"));
        Assert.Equal(new[] { Page, "https://10.0.0.5:8443" }, StorageService.BrowserOrigins(Page, "https://10.0.0.5:8443/"));
        Assert.Equal(new[] { "https://web.crm.com" }, StorageService.BrowserOrigins(null, "https://web.crm.com"));
        Assert.Empty(StorageService.BrowserOrigins("null", ""));
    }

    // ------------------------------------------------------ the setup's values

    private static SetupValues Setup(string accessKey) => new(Setups.MainId, Setups.MainName, true,
        new Dictionary<string, string?>
        {
            ["storage.endpoint"] = " http://127.0.0.1:9000 ",
            ["storage.bucket"] = "portal-files\t",
            ["storage.accessKey"] = accessKey,
            ["storage.secretKey"] = "secret\r\n",
        });

    /// <summary>A key saved before the save trimmed, or set by an environment variable, still signs.</summary>
    [Fact]
    public void A_pasted_space_does_not_become_part_of_the_key()
    {
        var c = StorageService.Config(Setup(" portal \n"))!;
        Assert.Equal("portal", c.AccessKey);
        Assert.Equal("secret", c.SecretKey);
        Assert.Equal("portal-files", c.Bucket);
        Assert.Equal(new Uri("http://127.0.0.1:9000"), c.Endpoint);
    }

    [Fact]
    public void The_save_trims_every_storage_field_of_every_setup_and_leaves_a_password_as_typed()
    {
        foreach (var field in Setups.Storage.Fields)
        {
            Assert.Equal("x", SettingsService.Entered(SettingsRegistry.Find(field)!, " x\n"));
            Assert.Equal("x", SettingsService.Entered(SettingsRegistry.Find(Setups.FieldKey(field, "s1a2b3c4d"))!, " x\n"));
        }
        Assert.Equal(" pass ", SettingsService.Entered(SettingsRegistry.Find("email.smtpPassword")!, " pass "));
    }

    [Fact]
    public void An_unknown_key_names_the_store_that_does_not_know_it()
    {
        var c = StorageService.Config(Setup("portal"))!;
        var text = StorageService.TestFailureText(c, new StorageException(403, "InvalidAccessKeyId",
            "PUT health/roundtrip-1.txt: The Access Key Id you provided does not exist in our records."));
        Assert.Contains("http://127.0.0.1:9000", text);
        Assert.Contains("“portal”", text);
        Assert.Contains("mc admin user add <alias> portal <secret key>", text);
        Assert.DoesNotContain("in our records", text);
        Assert.DoesNotContain("Identity", text);
    }

    /// <summary>
    /// A bucket renamed in the settings but not in the key's policy: the
    /// store says only "Access Denied", the test says which key, which bucket
    /// and what the policy must name.
    /// </summary>
    [Theory]
    [InlineData("PUT", "write to")]
    [InlineData("GET", "read from")]
    [InlineData("DELETE", "delete from")]
    public void A_refused_bucket_is_named_with_what_the_policy_needs(string op, string verb)
    {
        var c = StorageService.Config(Setup("portal"))!;
        var text = StorageService.TestFailureText(c,
            new StorageException(403, "AccessDenied", $"{op} health/roundtrip-1.txt: Access Denied.", op));
        Assert.Contains($"will not let key “portal” {verb} bucket “portal-files”", text);
        Assert.Contains("http://127.0.0.1:9000", text);
        Assert.Contains("arn:aws:s3:::portal-files/*", text);
        Assert.Contains("mc admin policy create", text);
    }

    /// <summary>
    /// The browser blocks a preview or a download from an http store into
    /// an https page before it is asked for, which the server's round-trip
    /// cannot see.
    /// </summary>
    [Fact]
    public void An_http_store_under_an_https_page_is_refused_with_the_https_address()
    {
        var text = StorageService.InsecureAddressProblem(new Uri("http://web.crm.com:9000"),
            ["http://10.0.0.5:3000", "https://web.crm.com"]);
        Assert.NotNull(text);
        Assert.Contains("a page on https://web.crm.com cannot open files from http://web.crm.com:9000", text);
        Assert.Contains("set the Public base URL to https://web.crm.com:9000 ", text);

        var standardPort = StorageService.InsecureAddressProblem(new Uri("http://files.example.com"), ["https://example.com"]);
        Assert.Contains("set the Public base URL to https://files.example.com ", standardPort);
    }

    [Theory]
    [InlineData("https://web.crm.com:9000", "https://web.crm.com")]
    [InlineData("http://web.crm.com:9000", "http://web.crm.com")]
    [InlineData("http://localhost:9000", "https://portal.example.com")]
    [InlineData("http://127.0.0.1:9000", "https://portal.example.com")]
    [InlineData("http://s3.localhost:8088", "https://web.localhost")]
    public void A_store_the_browser_may_reach_passes(string publicUrl, string origin) =>
        Assert.Null(StorageService.InsecureAddressProblem(new Uri(publicUrl), [origin]));

    [Fact]
    public void A_refused_create_says_the_bucket_is_missing()
    {
        var c = StorageService.Config(Setup("portal"))!;
        var text = StorageService.TestFailureText(c,
            new StorageException(403, "AccessDenied", "CREATE BUCKET portal-files: Access Denied.", "CREATE BUCKET"));
        Assert.Contains("has no bucket “portal-files”, and key “portal” may not create it", text);
        Assert.Contains("s3:CreateBucket on arn:aws:s3:::portal-files", text);

        var other = new StorageException(403, "SignatureDoesNotMatch", "PUT health/roundtrip-1.txt: bad signature.", "PUT");
        Assert.Equal(other.Message, StorageService.TestFailureText(c, other));
    }

    [Fact]
    public void Only_a_pdf_preview_opens_inline()
    {
        Assert.True(StorageRules.OpensInline(StorageRules.Preview("application/pdf", "brand-guide.pdf", 2048)));
        Assert.False(StorageRules.OpensInline(StorageRules.Preview("image/svg+xml", "brand-mark.svg", 2048)));
        Assert.False(StorageRules.OpensInline(StorageRules.Preview("text/plain", "notes.txt", 2048)));
        Assert.False(StorageRules.OpensInline(null));
        Assert.StartsWith("inline;", StorageRules.ContentDisposition("brand-guide.pdf", inline: true));
    }
}
