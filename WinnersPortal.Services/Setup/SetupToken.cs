using System.Security.Cryptography;
using System.Text;

namespace WinnersPortal.Services.Setup;

/// <summary>
/// The one-time token that gates the setup wizard. Pin it with SETUP_TOKEN;
/// otherwise a fresh one is generated per process and logged on first run.
/// </summary>
public sealed class SetupToken
{
    public string Value { get; }
    public bool Pinned { get; }

    public SetupToken(IConfiguration config)
    {
        var pinned = config["SETUP_TOKEN"];
        Pinned = !string.IsNullOrWhiteSpace(pinned);
        Value = Pinned
            ? pinned!.Trim()
            : Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    }

    public bool Matches(string? candidate) =>
        !string.IsNullOrEmpty(candidate) &&
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(candidate),
            Encoding.UTF8.GetBytes(Value));
}
