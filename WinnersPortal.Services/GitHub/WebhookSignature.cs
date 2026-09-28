using System.Security.Cryptography;
using System.Text;

namespace WinnersPortal.Services.GitHub;

/// <summary>
/// Verifies GitHub's <c>X-Hub-Signature-256</c> header: HMAC-SHA256 of the
/// raw body with the shared webhook secret. Pure so the tests can hit it.
/// </summary>
public static class WebhookSignature
{
    public static bool Verify(string secret, ReadOnlySpan<byte> payload, string? signatureHeader)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(signatureHeader)) return false;
        if (!signatureHeader.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase)) return false;
        if (signatureHeader.Length != "sha256=".Length + 64) return false;

        byte[] expected;
        try
        {
            expected = Convert.FromHexString(signatureHeader.AsSpan(7));
        }
        catch (FormatException)
        {
            return false; // not hex — a malformed header is a rejection, not a crash
        }

        Span<byte> actual = stackalloc byte[32];
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), payload, actual);

        // Constant-time: never let a comparison's timing leak how close a forgery got.
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>Computes the header value; used by tests and the local delivery simulator.</summary>
    public static string Compute(string secret, ReadOnlySpan<byte> payload)
    {
        Span<byte> hash = stackalloc byte[32];
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), payload, hash);
        return "sha256=" + Convert.ToHexString(hash).ToLowerInvariant();
    }
}
