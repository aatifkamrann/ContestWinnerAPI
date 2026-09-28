using System.Security.Cryptography;
using System.Text;

namespace WinnersPortal.Services.Identity;

/// <summary>
/// Verifies Didit's <c>X-Signature</c> header: hex HMAC-SHA256 of the raw
/// body with the destination's shared secret, plus the <c>X-Timestamp</c>
/// header within five minutes of now — a replayed delivery is signed
/// correctly and is still refused. Pure so the tests can hit it.
/// </summary>
public static class IdentityWebhookSignature
{
    /// <summary>How far a delivery's timestamp may sit from the portal's clock, either way — the provider's own rule.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    public static bool Verify(
        string secret, ReadOnlySpan<byte> payload, string? signatureHeader, string? timestampHeader, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(signatureHeader)) return false;
        if (!FreshTimestamp(timestampHeader, now)) return false;
        if (signatureHeader.Length != 64) return false;

        byte[] expected;
        try
        {
            expected = Convert.FromHexString(signatureHeader);
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

    /// <summary>Unix seconds, and within the window of now.</summary>
    public static bool FreshTimestamp(string? timestampHeader, DateTimeOffset now) =>
        long.TryParse(timestampHeader, out var seconds)
        && (now - DateTimeOffset.FromUnixTimeSeconds(seconds)).Duration() <= Window;

    /// <summary>Computes the header value; used by tests and a local delivery simulator.</summary>
    public static string Compute(string secret, ReadOnlySpan<byte> payload)
    {
        Span<byte> hash = stackalloc byte[32];
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), payload, hash);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
