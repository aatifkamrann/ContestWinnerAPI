using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WinnersPortal.Services.GitHub;

/// <summary>
/// The short-lived RS256 JWT a GitHub App authenticates with, hand-rolled:
/// two base64url JSON segments and an RSA-SHA256 signature is not worth a
/// dependency. Issued 60s in the past (GitHub rejects clock skew forwards,
/// forgives it backwards) and valid for 9 minutes of the 10 allowed.
/// </summary>
public static class GitHubAppJwt
{
    public static string Create(string appId, string privateKeyPem, DateTimeOffset? now = null)
    {
        var at = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        var header = B64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT" }));
        var payload = B64Url(JsonSerializer.SerializeToUtf8Bytes(new { iat = at - 60, exp = at + 540, iss = appId }));

        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem); // handles both PKCS#1 (GitHub's download) and PKCS#8
        var signature = rsa.SignData(
            Encoding.ASCII.GetBytes($"{header}.{payload}"),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        return $"{header}.{payload}.{B64Url(signature)}";
    }

    private static string B64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
