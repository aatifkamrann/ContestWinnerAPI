using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using WinnersPortal.Services.GitHub;
using Xunit;

namespace WinnersPortal.Api.Tests;

public class WebhookSignatureTests
{
    private const string Secret = "a-webhook-secret";
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("""{"zen":"Design for failure."}""");

    [Fact]
    public void Computed_signature_verifies()
    {
        var header = WebhookSignature.Compute(Secret, Payload);
        Assert.StartsWith("sha256=", header);
        Assert.True(WebhookSignature.Verify(Secret, Payload, header));
    }

    [Fact]
    public void Tampered_payload_fails()
    {
        var header = WebhookSignature.Compute(Secret, Payload);
        var tampered = Encoding.UTF8.GetBytes("""{"zen":"Design for failure!"}""");
        Assert.False(WebhookSignature.Verify(Secret, tampered, header));
    }

    [Fact]
    public void Wrong_secret_fails()
    {
        var header = WebhookSignature.Compute(Secret, Payload);
        Assert.False(WebhookSignature.Verify("another-secret", Payload, header));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha1=deadbeef")] // GitHub's legacy header is not accepted
    [InlineData("sha256=")] // empty digest
    [InlineData("sha256=nothex-nothex-nothex-nothex-nothex-nothex-nothex-nothex-nothexZZ")]
    [InlineData("sha256=abad1dea")] // wrong length
    public void Malformed_headers_are_rejected_not_crashed(string? header)
    {
        Assert.False(WebhookSignature.Verify(Secret, Payload, header));
    }
}

public class MilestoneRefsTests
{
    [Theory]
    [InlineData("m1", 1)]
    [InlineData("m12", 12)]
    [InlineData("M3", 3)] // tags are case-insensitive at the claim
    [InlineData("refs/tags/m2", 2)]
    [InlineData("m0", null)] // milestones are 1-based on the board
    [InlineData("m", null)]
    [InlineData("m1x", null)]
    [InlineData("m01", null)] // no leading zeros — m01 and m1 must not be two spellings of one claim
    [InlineData("final", null)] // the freeze tag is not a claim
    [InlineData("v1", null)]
    [InlineData("refs/heads/m1", null)] // a branch push is activity, not a claim
    [InlineData(null, null)]
    public void Tag_parsing(string? reference, int? expected)
    {
        Assert.Equal(expected, MilestoneRefs.FromTag(reference));
    }

    [Theory]
    [InlineData("m2-invoices", null, 2)]
    [InlineData("m10_reporting", null, 10)]
    [InlineData("m3", "anything", 3)]
    [InlineData("feature/pdf", "[m4] printable invoices", 4)]
    [InlineData("feature/pdf", "(m5) reports", 5)]
    [InlineData("m2-x", "[m9] branch wins over title", 2)]
    [InlineData("main", "no claim here", null)]
    [InlineData("m0-nope", "[m0] nope", null)]
    [InlineData(null, null, null)]
    public void Pull_request_parsing(string? branch, string? title, int? expected)
    {
        Assert.Equal(expected, MilestoneRefs.FromPullRequest(branch, title));
    }
}

public class RepoNamesTests
{
    [Fact]
    public void Slug_plus_username()
    {
        Assert.Equal("inventory-dashboard-bilal-builds", RepoNames.For("inventory-dashboard", "bilal-builds"));
    }

    [Fact]
    public void Usernames_are_lowercased()
    {
        Assert.Equal("a-opportunity-bilalbuilds", RepoNames.For("a-opportunity", "BilalBuilds"));
    }

    [Fact]
    public void Long_slugs_truncate_but_the_username_survives_whole()
    {
        var slug = new string('a', 150);
        var name = RepoNames.For(slug, "bilal-builds");
        Assert.True(name.Length <= RepoNames.MaxLength);
        Assert.EndsWith("-bilal-builds", name);
    }

    [Fact]
    public void Truncation_never_leaves_a_double_hyphen()
    {
        // budget lands exactly on a hyphen boundary → TrimEnd removes it
        var slug = new string('a', 86) + "-b"; // budget for a 12-char username is 87
        var name = RepoNames.For(slug, "bilal-builds");
        Assert.DoesNotContain("--", name);
        Assert.True(name.Length <= RepoNames.MaxLength);
    }
}

public class GitHubAppJwtTests
{
    [Fact]
    public void Jwt_is_rs256_signed_and_carries_the_app_id()
    {
        using var rsa = RSA.Create(2048);
        var pem = rsa.ExportRSAPrivateKeyPem();

        var jwt = GitHubAppJwt.Create("123456", pem, DateTimeOffset.UtcNow);
        var parts = jwt.Split('.');
        Assert.Equal(3, parts.Length);

        var header = JsonDocument.Parse(FromB64Url(parts[0]));
        Assert.Equal("RS256", header.RootElement.GetProperty("alg").GetString());

        var payload = JsonDocument.Parse(FromB64Url(parts[1]));
        Assert.Equal("123456", payload.RootElement.GetProperty("iss").GetString());
        var iat = payload.RootElement.GetProperty("iat").GetInt64();
        var exp = payload.RootElement.GetProperty("exp").GetInt64();
        Assert.Equal(600, exp - iat); // issued 60s back, expires 540s ahead

        // The signature must verify against the key's public half.
        Assert.True(rsa.VerifyData(
            Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"),
            FromB64Url(parts[2]),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1));
    }

    private static byte[] FromB64Url(string s)
    {
        var padded = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight((padded.Length + 3) / 4 * 4, '='));
    }
}

public class OAuthStateTests
{
    private readonly IDataProtectionProvider _provider = new EphemeralDataProtectionProvider();

    [Fact]
    public void State_round_trips()
    {
        var userId = Guid.NewGuid();
        var state = GitHubAuthService.CreateState(_provider, userId, DateTimeOffset.UtcNow, "/profile");
        var parsed = GitHubAuthService.ParseState(_provider, state);
        Assert.Equal(userId, parsed?.UserId);
        Assert.Equal("/profile", parsed?.Next);
    }

    [Fact]
    public void State_without_a_return_path_parses_to_none()
    {
        var state = GitHubAuthService.CreateState(_provider, Guid.NewGuid(), DateTimeOffset.UtcNow);
        Assert.Null(GitHubAuthService.ParseState(_provider, state)!.Next);
    }

    // The return path reaches us as a query string on a link anyone can
    // retype, and it ends up in a Location header. Everything that is not a
    // plain path on this portal has to come back as "no opinion".
    [Theory]
    [InlineData("//evil.example/pwned")]
    [InlineData("https://evil.example")]
    [InlineData("/profile?next=%2F")]
    [InlineData("/profile#x")]
    [InlineData("/../profile")]
    [InlineData("profile")]
    [InlineData("")]
    [InlineData(null)]
    public void An_offsite_return_path_is_dropped(string? next)
    {
        Assert.Null(GitHubAuthService.SafeNext(next));
        var state = GitHubAuthService.CreateState(_provider, Guid.NewGuid(), DateTimeOffset.UtcNow, next);
        Assert.Null(GitHubAuthService.ParseState(_provider, state)!.Next);
    }

    [Fact]
    public void Expired_state_is_refused()
    {
        var state = GitHubAuthService.CreateState(
            _provider, Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(-11));
        Assert.Null(GitHubAuthService.ParseState(_provider, state));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-protected-payload")]
    public void Garbage_state_is_refused(string? state)
    {
        Assert.Null(GitHubAuthService.ParseState(_provider, state));
    }

    [Fact]
    public void State_from_another_key_ring_is_refused()
    {
        var state = GitHubAuthService.CreateState(_provider, Guid.NewGuid(), DateTimeOffset.UtcNow);
        Assert.Null(GitHubAuthService.ParseState(new EphemeralDataProtectionProvider(), state));
    }
}
