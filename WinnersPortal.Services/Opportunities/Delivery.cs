using WinnersPortal.Domain;

namespace WinnersPortal.Services.Opportunities;

/// <summary>
/// The pure rules of how work is handed in. An opportunity is delivered through a
/// repository, through file uploads, or both; the choice is the client's,
/// made while drafting, and every rule below reads off it — who needs a
/// GitHub username, when an upload may land, and who may open the files.
///
/// The upload path is deliberately shaped like the repository path: work
/// lands only while the opportunity is open, freezes at the deadline, claims a
/// milestone first-come and never un-claims it, and is the client's to read
/// once the deadline has passed and not before. Two ways in, one set of
/// promises to the entrant.
/// </summary>
public static class Delivery
{
    public const string RepositoryName = "repository";
    public const string UploadName = "upload";
    public const string BothName = "both";

    /// <summary>The form's word for it, or null for a word that is not one of the three.</summary>
    public static OpportunityDelivery? Parse(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        RepositoryName => OpportunityDelivery.Repository,
        UploadName => OpportunityDelivery.Upload,
        BothName => OpportunityDelivery.Both,
        _ => null,
    };

    public static string Name(OpportunityDelivery delivery) => delivery switch
    {
        OpportunityDelivery.Upload => UploadName,
        OpportunityDelivery.Both => BothName,
        _ => RepositoryName,
    };

    public static string? Problem(string? name) =>
        Parse(name) is null
            ? "Choose how work is delivered: a GitHub repository, file uploads, or both."
            : null;

    /// <summary>
    /// Why the opportunity may not require entries to run with Docker Compose,
    /// or null. Only a repository carries a compose file to build from; an
    /// upload-only opportunity asking for one would be a rule nobody can meet.
    /// </summary>
    public static string? ComposeProblem(string? deliveryName, bool? requiresCompose) =>
        requiresCompose == true && Parse(deliveryName) is { } d && !UsesRepository(d)
            ? "Entries can only be required to run with Docker Compose when work is delivered through a repository."
            : null;

    public static bool UsesRepository(OpportunityDelivery d) => (d & OpportunityDelivery.Repository) != 0;
    public static bool UsesUpload(OpportunityDelivery d) => (d & OpportunityDelivery.Upload) != 0;

    /// <summary>A GitHub username is where the repository invitation goes; without a repository there is nothing to send.</summary>
    public static bool NeedsGithubUsername(OpportunityDelivery d) => UsesRepository(d);

    /// <summary>
    /// Why an upload may not land now, or null when it may. Uploads follow
    /// the repository's clock exactly: open while the opportunity is, frozen the
    /// moment the deadline passes — what was handed in by then is what is
    /// judged, and a file that arrives afterwards is not part of the entry.
    /// </summary>
    public static string? UploadProblem(OpportunityStatus status, DateTimeOffset? deadlineUtc, DateTimeOffset now)
    {
        if (status == OpportunityStatus.Open && (deadlineUtc is null || deadlineUtc > now)) return null;
        return status switch
        {
            OpportunityStatus.Open or OpportunityStatus.Reviewing =>
                "The deadline has passed — what was uploaded by then is what the client reviews.",
            OpportunityStatus.Awarded => "This opportunity has its winner; its files are final.",
            OpportunityStatus.Cancelled => "This opportunity was cancelled; nothing more can be handed in.",
            _ => "This opportunity is not open.",
        };
    }

    /// <summary>
    /// Who may open an entrant's files. The entrant always: they are their
    /// own. An administrator always: moderation cannot wait for a deadline.
    /// The client from the deadline on — the same promise the repository
    /// path makes ("the client only sees your work at the deadline"), kept
    /// for a file, because an entrant who uploads a first draft early
    /// should not be judged on it before the others have handed in.
    /// </summary>
    public static bool CanSeeFiles(bool isEntrant, bool isOwner, bool isAdmin, OpportunityStatus status) =>
        isEntrant
        || isAdmin
        || (isOwner && status is OpportunityStatus.Reviewing or OpportunityStatus.Awarded);

    /// <summary>
    /// Why a file may not be deleted, or null. A file that carried a
    /// milestone claim stays: claims are first-come and never undone, on
    /// either path, because the board is the record the client reads.
    /// </summary>
    public static string? DeleteProblem(bool claimedMilestone, string? frozen)
    {
        if (frozen is not null) return frozen;
        return claimedMilestone
            ? "This file claimed a milestone, and claims are not undone. Upload the better version alongside it."
            : null;
    }
}
