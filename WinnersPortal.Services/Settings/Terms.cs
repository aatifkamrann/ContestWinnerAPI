using WinnersPortal.Services.Common;

namespace WinnersPortal.Services.Settings;

/// <summary>
/// The terms of service, and who owes an acceptance. The portal has terms
/// once legal.termsMarkdown has text; their version is legal.termsVersion,
/// a whole number the operator raises when the text changes materially.
///
/// Acceptance is a fact about the account, not the browser: the user row
/// records the version accepted and when, so it follows the person across
/// devices the way their accent colour does. The rule is "accepted at least
/// this version" — raising the version turns into a prompt on the next
/// visit, lowering it undoes nothing, and a portal with no terms asks
/// nobody anything.
///
/// While an acceptance is owed the account is read-only: the gate below
/// refuses every writing API call except the session's own endpoints, so
/// the prompt the web tier shows is a rule the API holds, not a courtesy.
/// </summary>
public static class Terms
{
    public const string MarkdownKey = "legal.termsMarkdown";
    public const string VersionKey = "legal.termsVersion";

    /// <summary>Terms exist when there is text to agree to.</summary>
    public static bool Exist(string? markdown) => !string.IsNullOrWhiteSpace(markdown);

    /// <summary>A version is a whole number of one or more; the settings screen refuses anything else.</summary>
    public static bool IsValidVersion(string? configured) =>
        int.TryParse(configured?.Trim(), out var v) && v >= 1;

    /// <summary>
    /// The configured version, falling back to 1 for a value the validation
    /// never saw — an environment override, say.
    /// </summary>
    public static int Version(string? configured) =>
        IsValidVersion(configured) ? int.Parse(configured!.Trim()) : 1;

    /// <summary>Does this account owe an acceptance?</summary>
    public static bool Pending(int? acceptedVersion, int currentVersion, bool termsExist) =>
        termsExist && (acceptedVersion ?? 0) < currentVersion;

    /// <summary>
    /// Pure so the rule is testable: may this request proceed while the
    /// caller owes an acceptance? Reads always may — the person has to be
    /// able to see the terms, and the page under them. Under /api/auth is
    /// the session itself: accepting, signing out, the theme, a password.
    /// Every other write waits.
    /// </summary>
    public static bool GateAllows(string path, string method)
    {
        if (!Requests.StartsWithSegments(path, "/api")) return true;
        if (Requests.IsRead(method)) return true;
        // The visit beacon is a POST that changes nothing of the person's.
        return Requests.StartsWithSegments(path, "/api/auth") || Requests.StartsWithSegments(path, "/api/activity");
    }

    /// <summary>Whether the portal has terms right now, and at what version.</summary>
    public static async Task<(bool Exist, int Version)> CurrentAsync(SettingsService settings, CancellationToken ct)
    {
        var markdown = await settings.GetAsync(MarkdownKey, ct);
        return (Exist(markdown), Version(await settings.GetAsync(VersionKey, ct)));
    }
}
