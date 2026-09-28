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

    /// <summary>A verdict the provider will not change on its own; the member may start again after any but Approved.</summary>
    public static bool IsFinal(IdentityStatus status) =>
        status is IdentityStatus.Approved or IdentityStatus.Declined or IdentityStatus.Abandoned or IdentityStatus.Expired;

    /// <summary>Whether a fresh session may be opened: never over an approval, never over one still running.</summary>
    public static bool MayStart(IdentityStatus? current) =>
        current is null || current is IdentityStatus.Declined or IdentityStatus.Abandoned or IdentityStatus.Expired;

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

    /// <summary>The reason category the provider gives on a decline, clipped and stripped of anything document-shaped.</summary>
    public static string? Note(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return null;
        var trimmed = reason.Trim();
        return trimmed.Length > 400 ? trimmed[..400] : trimmed;
    }
}
