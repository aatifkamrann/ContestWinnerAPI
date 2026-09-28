namespace WinnersPortal.Domain;

/// <summary>
/// One file an entrant handed in — the upload half of
/// <see cref="OpportunityDelivery"/>, for the work that has no commits: a logo,
/// a document, a deck. The bytes live in object storage under
/// <see cref="StorageKey"/> and only ever leave through a signed URL; this
/// row is the permission record the API checks before signing one.
///
/// Two-phase like a brief attachment: the row reserves the key, the bytes
/// arrive through the API and are written to storage, and
/// <see cref="UploadedAtUtc"/> is stamped in that same request with the
/// size that really arrived. An unconfirmed row is invisible everywhere
/// and expires.
/// </summary>
public sealed class Submission
{
    // Column limits: the one place a length is stated; the rules and the
    // DbContext both read it here.
    public const int MaxFileNameLength = Attachment.MaxFileNameLength;

    public Guid Id { get; set; }

    public Guid EntryId { get; set; }
    public Entry? Entry { get; set; }

    /// <summary>
    /// The milestone this file claimed, when it did. Set only on the file
    /// that carried the claim — a second file tagged to an already-claimed
    /// milestone is stored with no milestone, exactly as a second tag push
    /// records activity but moves no board. The claim itself is the
    /// <see cref="Checkpoint"/> row; this is how the file knows it is the
    /// one that cannot be deleted without leaving the board dishonest.
    /// </summary>
    public Guid? MilestoneId { get; set; }
    public Milestone? Milestone { get; set; }

    /// <summary>Sanitised (StorageRules.SafeFileName) — safe to store, echo, and download as.</summary>
    public required string FileName { get; set; }

    /// <summary>As declared at reserve time; served back as the response content type.</summary>
    public required string ContentType { get; set; }

    /// <summary>Declared at reserve, replaced by the object's true size at confirm.</summary>
    public long SizeBytes { get; set; }

    public required string StorageKey { get; set; }

    /// <summary>The storage setup the bytes went to (Settings/Setups.cs); null on a row from before there was a choice — the main one.</summary>
    public string? StorageSetup { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    /// <summary>Null until the upload is confirmed against the store.</summary>
    public DateTimeOffset? UploadedAtUtc { get; set; }
}
