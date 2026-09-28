namespace WinnersPortal.Services.Profiles;

/// <summary>
/// What a profile picture may be: the ceiling, the words, and the address.
/// Reading the bytes is <see cref="ImageRules"/>' job and shared with the
/// screenshots of past work — the same three signatures, the same refusal
/// to believe a declared type.
/// </summary>
public static class AvatarRules
{
    /// <summary>
    /// The browser sends a square it shrank to 256 pixels first, so a real
    /// upload is tens of kilobytes; this is the ceiling for a browser that
    /// did not. Half a megabyte, because the row is served on every page
    /// that shows the name.
    /// </summary>
    public const int MaxBytes = 512 * 1024;

    /// <summary>Kept as this name because the column is mapped from it. See <see cref="ImageRules.MaxContentTypeLength"/>.</summary>
    public const int MaxContentTypeLength = ImageRules.MaxContentTypeLength;

    public const string TooBig =
        "That picture is too large — the portal keeps profile pictures under half a megabyte.";

    /// <summary>What the bytes say they are — <see cref="ImageRules.SniffContentType"/>.</summary>
    public static string? SniffContentType(ReadOnlySpan<byte> bytes) =>
        ImageRules.SniffContentType(bytes);

    /// <summary>
    /// The picture as the form sends it — base64, with or without the
    /// <c>data:…;base64,</c> prefix a canvas puts on it — decoded and
    /// checked. Either the bytes and their real type, or a problem in the
    /// words the form shows.
    /// </summary>
    public static (byte[]? Bytes, string? ContentType, string? Problem) Read(string? dataBase64) =>
        ImageRules.Read(dataBase64, MaxBytes, TooBig, "Choose a picture first.");

    /// <summary>
    /// The address of somebody's picture, or null where they have none.
    /// The version in the query is when the picture was set, so the URL of
    /// a new picture is a new URL and a browser may keep the old one for
    /// as long as it likes — the serving endpoint says a year.
    /// </summary>
    public static string? Url(Guid userId, DateTimeOffset? updatedAtUtc) =>
        updatedAtUtc is { } at ? $"/api/avatars/{userId}?v={at.ToUnixTimeSeconds()}" : null;
}
