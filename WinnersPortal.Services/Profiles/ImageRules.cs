using WinnersPortal.Domain;

namespace WinnersPortal.Services.Profiles;

/// <summary>
/// What a picture uploaded to a profile may be — the part that is the same
/// whether it is somebody's face or a screenshot of their work. The bytes
/// decide what they are: a declared content type is the one thing about an
/// upload that costs nothing to lie about, so it is never read.
///
/// The ceilings are the callers' — <see cref="AvatarRules"/> sets one for a
/// 256-pixel square, <see cref="ProjectImageRules"/> a larger one for a
/// screenshot — because they are set for what the browser already shrank the
/// picture to, not for the photo it was shrunk from.
/// </summary>
public static class ImageRules
{
    public const int MaxContentTypeLength = ProfileImage.MaxContentTypeLength;

    /// <summary>
    /// What the bytes say they are: JPEG, PNG or WebP by their signatures,
    /// and null for anything else — including a real picture in a format
    /// the portal does not serve, which a member is told to convert rather
    /// than have stored and rendered as a broken image.
    /// </summary>
    public static string? SniffContentType(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return "image/jpeg";
        if (bytes.Length >= 8
            && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47
            && bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
            return "image/png";
        if (bytes.Length >= 12
            && bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F'
            && bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P')
            return "image/webp";
        return null;
    }

    /// <summary>
    /// The picture as the form sends it — base64, with or without the
    /// <c>data:…;base64,</c> prefix a canvas puts on it — decoded and
    /// checked against <paramref name="maxBytes"/>. Either the bytes and
    /// their real type, or a problem in the words the form shows.
    /// </summary>
    public static (byte[]? Bytes, string? ContentType, string? Problem) Read(
        string? dataBase64, int maxBytes, string tooBig, string missing)
    {
        if (string.IsNullOrWhiteSpace(dataBase64)) return (null, null, missing);

        var text = dataBase64.Trim();
        var comma = text.IndexOf(',');
        var payload = text.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma > 0
            ? text[(comma + 1)..]
            : text;

        // Judged on the encoded length before anything is decoded: four
        // characters carry three bytes, so an oversize upload is refused
        // without first being allocated.
        if (payload.Length > (maxBytes / 3 + 1) * 4) return (null, null, tooBig);

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(payload);
        }
        catch (FormatException)
        {
            return (null, null, "The picture could not be read — choose it again.");
        }

        if (bytes.Length == 0) return (null, null, missing);
        if (bytes.Length > maxBytes) return (null, null, tooBig);

        var contentType = SniffContentType(bytes);
        if (contentType is null)
            return (null, null, "That file is not a JPEG, PNG or WebP picture.");
        return (bytes, contentType, null);
    }
}

/// <summary>
/// What a screenshot of past work may be, and how many of them one project
/// may carry. Larger than a profile picture because it is read rather than
/// glanced at — a screenshot nobody can read the labels on says nothing —
/// and bounded because these ride the same save as the rest of the form.
/// </summary>
public static class ProjectImageRules
{
    /// <summary>
    /// Four is a project: the screen, the hard part, the result, and one
    /// more. Past that it is an album, and the page below it stops being
    /// read.
    /// </summary>
    public const int MaxPerProject = 4;

    /// <summary>
    /// The browser sends a JPEG it has already shrunk to fit 1280 pixels
    /// (ProjectImages.tsx), so a real upload is a couple of hundred
    /// kilobytes; this is the ceiling for a browser that did not.
    /// </summary>
    public const int MaxBytes = 1024 * 1024;

    /// <summary>
    /// A save carries the bytes of every picture that is new to it, so the
    /// whole form has a ceiling as well as each picture. Reached only by
    /// somebody adding dozens of pictures before their first save — which is
    /// worth a sentence they can act on rather than a request that dies in
    /// the server with no message at all.
    /// </summary>
    public const int MaxNewBytesPerSave = 12 * 1024 * 1024;

    public const string TooBig =
        "That picture is too large — the portal keeps a screenshot under a megabyte.";

    public const string TooMuchAtOnce =
        "That is a lot of new pictures in one save. Save the ones you have added, then add the rest.";

    public static string TooMany(string? title) =>
        $"“{title?.Trim()}” has more than {MaxPerProject} pictures — {MaxPerProject} is the most a project shows.";

    /// <summary>
    /// The address of one picture. The id is the address: a replacement is a
    /// new row with a new id, so a browser may keep what it has for a year
    /// and never see a stale picture — the serving endpoint says as much.
    /// </summary>
    public static string Url(Guid imageId) => $"/api/project-images/{imageId}";
}
