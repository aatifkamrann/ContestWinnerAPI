namespace WinnersPortal.Domain;

/// <summary>
/// One image from an identity decision — the front of the document, the
/// back, the portrait read off it, the selfie — copied out of the
/// provider's expiring link into the portal's own file storage, so the
/// proof outlives the provider's retention. Administrators view it; the
/// member does not.
///
/// No foreign key to the verification or the account, on purpose: when a
/// verification is reset, or an account deleted outright, its rows are left
/// behind, and the proof worker deletes each stored image before its row —
/// a key that cascaded would drop the row and strand the file. A row whose
/// image should go is marked <see cref="RemovedAtUtc"/> for the same worker.
/// </summary>
public sealed class IdentityDocument
{
    public const int MaxName = 200;

    public Guid Id { get; set; }

    /// <summary>The <see cref="IdentityVerification"/> it proves; a row whose verification is gone is swept.</summary>
    public Guid VerificationId { get; set; }

    public Guid UserId { get; set; }

    /// <summary>Where it sat in the decision, as a path: "id_verification.front_image", "liveness.reference_image".</summary>
    public required string Name { get; set; }

    public required string ContentType { get; set; }

    public long SizeBytes { get; set; }

    /// <summary>The storage setup it went to; null for the main one.</summary>
    public string? StorageSetup { get; set; }

    public required string StorageKey { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    /// <summary>Set when the image is to go — a newer decision replaced it, the account was erased; the worker deletes file and row.</summary>
    public DateTimeOffset? RemovedAtUtc { get; set; }
}
