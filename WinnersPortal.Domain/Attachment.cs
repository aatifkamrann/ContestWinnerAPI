namespace WinnersPortal.Domain;

/// <summary>
/// A file attached to an opportunity brief — logo masters, mockups, sample data;
/// everything heavy that is not code. The bytes live in object storage under
/// <see cref="StorageKey"/> and only ever leave through a signed URL; this
/// row is the permission record the API checks before signing one.
///
/// Two-phase by design: the row is written first (reserving the key), the
/// bytes arrive through the API and are written to storage, and
/// <see cref="UploadedAtUtc"/> is stamped in that same request with the
/// size that really arrived. An unconfirmed row is invisible everywhere.
/// </summary>
public sealed class Attachment
{
    // Column limits: the one place a length is stated; the rules and the
    // DbContext both read it here.
    public const int MaxFileNameLength = 160;

    public Guid Id { get; set; }

    public Guid OpportunityId { get; set; }
    public Opportunity? Opportunity { get; set; }

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

    /// <summary>Null until the upload is confirmed against the store. Unconfirmed rows expire.</summary>
    public DateTimeOffset? UploadedAtUtc { get; set; }
}
