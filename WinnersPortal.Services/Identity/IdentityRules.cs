using WinnersPortal.Domain;

namespace WinnersPortal.Services.Identity;

/// <summary>
/// The pure half of identity verification: what the provider's words mean
/// to the portal, and the sentence each door says when it is shut. The
/// three doors — publishing, applying, a client reading payment details —
/// read one fact, <see cref="User.IdentityVerifiedAtUtc"/>, and these rules
/// are why the form's eligibility line and the refusal can never disagree.
/// </summary>
public static class IdentityRules
{
    /// <summary>What a shut door says; the web routes on the flag beside it, this is the line under it.</summary>
    public const string PublishProblem =
        "Verify your identity before publishing — it takes about two minutes, once, and this opportunity is a promise of money to strangers.";

    public const string ApplyProblem =
        "Verify your identity before applying — it takes about two minutes, once.";

    /// <summary>Under a profile's payment section, for a client who may not read it yet.</summary>
    public static string PaymentsWithheldNote(string displayName) =>
        $"Shown once {displayName} has verified their identity.";

    /// <summary>The provider's status words, folded to what the portal acts on. Unknown words are "in progress": the session exists and nothing is decided.</summary>
    public static IdentityStatus MapStatus(string? providerStatus) => providerStatus?.Trim() switch
    {
        "Not Started" => IdentityStatus.NotStarted,
        "Approved" => IdentityStatus.Approved,
        "Declined" => IdentityStatus.Declined,
        "In Review" => IdentityStatus.InReview,
        "Abandoned" => IdentityStatus.Abandoned,
        "Expired" or "Kyc Expired" => IdentityStatus.Expired,
        _ => IdentityStatus.InProgress, // In Progress, Resubmitted, Awaiting User
    };

    /// <summary>
    /// A provider's status word, folded: Didit's words as above, Shufti Pro's
    /// events as their own. An event that carries no verdict is never
    /// applied (<see cref="ShuftiEvents.ReadBack"/>, <see cref="ShuftiEvents.Ignored"/>).
    /// </summary>
    public static IdentityStatus MapStatus(string provider, string? word) => provider switch
    {
        IdentityProviders.ShuftiPro => word?.Trim() switch
        {
            ShuftiEvents.Pending => IdentityStatus.NotStarted,
            ShuftiEvents.Accepted => IdentityStatus.Approved,
            ShuftiEvents.Declined => IdentityStatus.Declined,
            ShuftiEvents.ReviewPending => IdentityStatus.InReview,
            ShuftiEvents.Cancelled => IdentityStatus.Abandoned,
            ShuftiEvents.Timeout => IdentityStatus.Expired,
            _ => IdentityStatus.InProgress, // request.received
        },
        _ => MapStatus(word),
    };

    /// <summary>A verdict the provider will not change on its own; the member may start again after any but Approved.</summary>
    public static bool IsFinal(IdentityStatus status) =>
        status is IdentityStatus.Approved or IdentityStatus.Declined or IdentityStatus.Abandoned or IdentityStatus.Expired;

    /// <summary>A session the member may still finish: opened, not yet answered, not lapsed. Its link is handed back rather than a new one opened.</summary>
    public static bool IsOpen(IdentityStatus? status) =>
        status is IdentityStatus.NotStarted or IdentityStatus.InProgress;

    /// <summary>
    /// Whether a fresh session may be opened: never over an approval or a
    /// review; over an open session only when there is no link to pick it
    /// up by (one opened before links were kept).
    /// </summary>
    public static bool MayStart(IdentityStatus? current, bool canResume) =>
        current is null
        || current is IdentityStatus.Declined or IdentityStatus.Abandoned or IdentityStatus.Expired
        || (IsOpen(current) && !canResume);

    /// <summary>
    /// How long an open session's stored status is trusted before a status
    /// read asks the provider again — so a session it let lapse stops being
    /// offered even where no webhook said so, at one call per member per
    /// interval, and only while one is open.
    /// </summary>
    public static readonly TimeSpan OpenRecheck = TimeSpan.FromMinutes(15);

    public static bool RecheckDue(IdentityStatus status, DateTimeOffset updatedAtUtc, DateTimeOffset now) =>
        IsOpen(status) && now - updatedAtUtc >= OpenRecheck;

    /// <summary>
    /// The way back as the return page may use it: a path on this portal
    /// ("/opportunities/x/apply?step=eligibility"), never another site's
    /// address, a protocol-relative "//host" or a backslash browsers read as
    /// one. Anything else is no way back at all.
    /// </summary>
    public static string? SafeReturn(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = path.Trim();
        if (p.Length > 512 || p[0] != '/' || p.StartsWith("//", StringComparison.Ordinal)
            || p.Contains('\\') || p.Any(char.IsControl))
            return null;
        return p;
    }

    /// <summary>The status word the web reads: none, pending, in_review, approved, declined.</summary>
    public static string StatusName(IdentityStatus? status) => status switch
    {
        null => "none",
        IdentityStatus.Approved => "approved",
        IdentityStatus.Declined => "declined",
        IdentityStatus.InReview => "in_review",
        IdentityStatus.Abandoned or IdentityStatus.Expired => "none", // may start again; nothing to show for the old one
        _ => "pending",
    };

    /// <summary>Whether the publishing door asks: the feature on, the switch on, the client not yet verified.</summary>
    public static bool PublishOwed(bool enabled, bool required, bool verified) => enabled && required && !verified;

    public static bool ApplyOwed(bool enabled, bool required, bool verified) => enabled && required && !verified;

    /// <summary>
    /// Whether a client who may read payment details — one who has announced
    /// the member as a winner — is held back because the member has not
    /// verified. Never the member themselves, never an administrator: the
    /// switch is about strangers.
    /// </summary>
    public static bool PaymentsWithheld(bool enabled, bool required, bool own, string? viewerRole, bool ownerVerified) =>
        enabled && required && !own && viewerRole == Roles.Client && !ownerVerified;

    /// <summary>
    /// What the settings test says of a probe — a session that does not
    /// exist, asked about. Didit answers a good key 404 and a bad one 401 or
    /// 403. Shufti Pro answers bad credentials 401 (request.unauthorized) and
    /// good ones a refusal about the reference instead: any other answer
    /// below 500 that is not about the sign-in.
    /// </summary>
    public static (bool Ok, string Detail) ProbeAnswer(string provider, string label, string setup, int status, string? body)
    {
        var unauthorized = status is 401 or 403
            || (body?.Contains("request.unauthorized", StringComparison.OrdinalIgnoreCase) ?? false);
        if (unauthorized)
            return (false, provider == IdentityProviders.ShuftiPro
                ? $"{label} rejected the client ID and secret key ({status}) — check both in its back office."
                : $"{label} rejected the key ({status}) — check it in the provider's console.");
        return status switch
        {
            404 when provider == IdentityProviders.Didit =>
                (true, $"{label} accepted the key (“{setup}”). Workflow and webhook secret are set; the first real verification proves the workflow."),
            >= 200 and < 500 and not 429 when provider == IdentityProviders.ShuftiPro =>
                (true, $"{label} accepted the client ID and secret key (“{setup}”) and found no such request, as expected. Callbacks are signed with the same key; the first real verification proves the rest."),
            429 => (false, $"{label} is rate-limiting this portal ({status}) — try again in a minute."),
            >= 500 => (false, $"{label} is not available right now ({status})."),
            _ => (false, $"{label} answered {status} to the probe; the key may still be fine — try a real verification."),
        };
    }

    /// <summary>The reason category the provider gives on a decline, clipped and stripped of anything document-shaped.</summary>
    public static string? Note(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return null;
        var trimmed = reason.Trim();
        return trimmed.Length > 400 ? trimmed[..400] : trimmed;
    }
}
