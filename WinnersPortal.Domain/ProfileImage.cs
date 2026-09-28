namespace WinnersPortal.Domain;

/// <summary>
/// One screenshot of one piece of past work. A store belonging to the
/// member rather than rows hanging off a project, because a project is
/// written from scratch on every save — every list on a profile is replaced
/// wholesale — and bytes that were re-sent each time somebody fixed a typo
/// would be far and away the most expensive field on the form.
///
/// A project points at these by id (<see cref="ProfileProject.ImageIds"/>),
/// so a save carries the ids of the pictures already here and the bytes of
/// the ones that are new. Anything no project points at once the save is
/// applied goes in the same transaction: taking a picture out of the form is
/// what deletes it, and nothing accumulates behind the screen.
///
/// Small on purpose, like <see cref="UserAvatar"/>: the browser shrinks each
/// one before it sends it, so a row is a couple of hundred kilobytes, it is
/// backed up with everything else, and there is no second store to configure
/// before a member can show their work.
/// </summary>
public sealed class ProfileImage
{
    // Column limits: the one place a length is stated; the rules and the
    // DbContext both read it here.
    public const int MaxContentTypeLength = 16;

    /// <summary>Also the address it is served at. A replacement is a new row, so a URL never goes stale.</summary>
    public Guid Id { get; set; }

    /// <summary>The profile that owns it. No other member's project may point at it.</summary>
    public Guid UserId { get; set; }

    public required byte[] Bytes { get; set; }

    /// <summary>What the bytes are, read off the bytes themselves (ImageRules.SniffContentType), never declared.</summary>
    public required string ContentType { get; set; }

    public DateTimeOffset UploadedAtUtc { get; set; }
}
