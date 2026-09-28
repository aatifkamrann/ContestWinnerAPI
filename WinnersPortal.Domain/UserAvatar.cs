namespace WinnersPortal.Domain;

/// <summary>
/// The picture beside a member's name. A table of its own rather than a
/// column on <see cref="User"/>, because the account row is read on every
/// request the cookie makes and a picture on it would ride along with all
/// of them; this row is read by exactly one endpoint, the one that serves
/// the picture. The account carries only the stamp
/// (<see cref="User.AvatarUpdatedAtUtc"/>), which is what every place that
/// shows a name needs: whether there is a picture, and which version.
///
/// Small on purpose. The browser is asked to shrink the picture to a
/// 256-pixel square before it sends it (AvatarField.tsx), so a row is tens
/// of kilobytes, backed up with everything else, and there is no second
/// store to configure before a member can have a face.
/// </summary>
public sealed class UserAvatar
{
    // Column limits: the one place a length is stated; the rules and the
    // DbContext both read it here.
    public const int MaxContentTypeLength = ProfileImage.MaxContentTypeLength;

    /// <summary>Primary key and foreign key both: one picture per account.</summary>
    public Guid UserId { get; set; }

    public required byte[] Bytes { get; set; }

    /// <summary>What the bytes are, read off the bytes themselves (AvatarRules.SniffContentType), never declared.</summary>
    public required string ContentType { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
}
