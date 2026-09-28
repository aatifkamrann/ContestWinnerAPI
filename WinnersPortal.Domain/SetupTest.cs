namespace WinnersPortal.Domain;

/// <summary>
/// The last test run on one setup of a connection — one row per setup,
/// replaced by the next test. It is what the badge on a setup's card on the
/// settings screen reads, so every administrator sees the same answer after
/// a reload. What was tested is kept only as a protected fingerprint of the
/// values: enough to say they have changed since, nothing a secret can be
/// recovered from.
/// </summary>
public sealed class SetupTest
{
    // Column limits: the one place a length is stated; the rules and the
    // DbContext both read it here.
    public const int MaxDetail = 1000;

    /// <summary>The connection: <c>github</c>, <c>storage</c>, <c>email</c>, <c>phone</c> or <c>ai</c>.</summary>
    public required string Kind { get; set; }

    public required string SetupId { get; set; }
    public bool Ok { get; set; }

    /// <summary>What the test said, as the screen showed it.</summary>
    public required string Detail { get; set; }

    /// <summary>A SHA-256 of the values tested, encrypted with Data Protection.</summary>
    public required string Fingerprint { get; set; }

    public DateTimeOffset TestedAtUtc { get; set; }
    public string? TestedBy { get; set; }
}
