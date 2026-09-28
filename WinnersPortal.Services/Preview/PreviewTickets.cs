using System.Security.Cryptography;
using System.Text;

namespace WinnersPortal.Services.Preview;

/// <summary>
/// The one-time ticket a browser carries from the portal to a preview.
///
/// The portal checks who may open a preview, then redirects the browser to
/// the preview's own address with a ticket in the query; the build agent
/// checks the ticket, trades it for a cookie of the preview's own host and
/// redirects again without it. Neither the portal's cookie nor its session
/// token ever reaches the host, and the host never calls the portal back.
///
/// A ticket is four dot-separated parts, all URL-safe as they stand:
/// <c>{label}.{expires}.{nonce}.{signature}</c> — the preview's label
/// (<c>p-1a2b3c4d</c>, so a ticket for one preview opens no other), the
/// Unix second it lapses (two minutes on), sixteen random bytes in hex (the
/// agent remembers each until it lapses, so a ticket opens once), and the
/// hex HMAC-SHA256 of the first three, joined by newlines, under a key
/// derived from the agent's token: HMAC-SHA256(token, "preview-ticket").
/// The agent (<c>deploy/preview-host/preview-agent.py</c>) computes the same
/// bytes; the fixed vector in the tests and in the agent's self-test keeps
/// the two in step.
/// </summary>
public static class PreviewTickets
{
    public static readonly TimeSpan Life = TimeSpan.FromMinutes(2);

    /// <summary>The query parameter the ticket rides in.</summary>
    public const string QueryName = "wp_ticket";

    /// <summary>The signing key: the agent's token is the one secret the portal and the host share.</summary>
    public static byte[] Key(string agentToken) =>
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(agentToken), Encoding.UTF8.GetBytes("preview-ticket"));

    /// <summary>A fresh ticket for one preview.</summary>
    public static string Mint(string agentToken, string label, DateTimeOffset now) =>
        Mint(agentToken, label, (now + Life).ToUnixTimeSeconds(), Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant());

    /// <summary>The ticket for given parts — the tests' fixed vector goes through here.</summary>
    public static string Mint(string agentToken, string label, long expiresUnix, string nonce) =>
        $"{label}.{expiresUnix}.{nonce}.{Signature(agentToken, label, expiresUnix, nonce)}";

    /// <summary>
    /// A ticket's parts if it is well formed, signed with this token and not
    /// yet lapsed; null otherwise. The agent does this check for real; the
    /// portal only needs it to test its own tickets.
    /// </summary>
    public static (string Label, long Expires, string Nonce)? Read(string agentToken, string? ticket, DateTimeOffset now)
    {
        var parts = ticket?.Split('.');
        if (parts is not { Length: 4 } || !long.TryParse(parts[1], out var expires)) return null;
        var expected = Encoding.ASCII.GetBytes(Signature(agentToken, parts[0], expires, parts[2]));
        if (!CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(parts[3]))) return null;
        if (expires < now.ToUnixTimeSeconds()) return null;
        return (parts[0], expires, parts[2]);
    }

    /// <summary>The preview's address with the ticket on it: where Open preview sends the browser.</summary>
    public static Uri Url(Uri previewAddress, string ticket) =>
        new UriBuilder(previewAddress) { Query = $"{QueryName}={Uri.EscapeDataString(ticket)}" }.Uri;

    private static string Signature(string agentToken, string label, long expiresUnix, string nonce) =>
        Convert.ToHexString(HMACSHA256.HashData(Key(agentToken), Encoding.UTF8.GetBytes($"{label}\n{expiresUnix}\n{nonce}")))
            .ToLowerInvariant();
}
