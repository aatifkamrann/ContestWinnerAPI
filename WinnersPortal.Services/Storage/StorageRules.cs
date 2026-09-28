using WinnersPortal.Domain;
using System.Text;

namespace WinnersPortal.Services.Storage;

/// <summary>
/// The pure rules for files: what may be attached, what a stored name may
/// look like, and where objects live in the bucket. Key layout is a contract
/// — objects are found again only by the key a row recorded, so every key
/// carries the id that owns it.
/// </summary>
public static class StorageRules
{
    /// <summary>Attachments are brief material, not a file share. Ten is generous.</summary>
    public const int MaxAttachmentsPerOpportunity = 10;

    public const int MaxFileNameLength = Attachment.MaxFileNameLength;

    /// <summary>The client's word on size, checked again against the real object at confirm.</summary>
    public static string? Problem(string? fileName, long sizeBytes, long maxBytes, int existingCount)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return "The file needs a name.";
        if (existingCount >= MaxAttachmentsPerOpportunity)
            return $"A brief carries at most {MaxAttachmentsPerOpportunity} attachments — link anything bigger from the brief itself.";
        if (sizeBytes <= 0)
            return "The file is empty.";
        if (sizeBytes > maxBytes)
            return TooLarge(maxBytes);
        return null;
    }

    /// <summary>Said at reserve on the declared size, and again on the real bytes when they arrive.</summary>
    public static string TooLarge(long maxBytes) =>
        $"That file is over the portal's {maxBytes / (1024 * 1024)} MB upload limit.";

    /// <summary>
    /// A browser-supplied name reduced to something safe to store and echo:
    /// no directories, no control characters, length capped with the
    /// extension kept — the extension is how the download stays openable.
    /// </summary>
    public static string SafeFileName(string? name)
    {
        // Whatever path the browser or an attacker prepends, keep the leaf.
        var leaf = (name ?? "").Replace('\\', '/');
        leaf = leaf[(leaf.LastIndexOf('/') + 1)..];

        var sb = new StringBuilder(leaf.Length);
        foreach (var c in leaf)
            sb.Append(c < ' ' || c is '"' or '<' or '>' or '|' or ':' or '*' or '?' ? '-' : c);
        var cleaned = sb.ToString().Trim(' ', '.');
        if (cleaned.Length == 0) return "file";
        if (cleaned.Length <= MaxFileNameLength) return cleaned;

        var dot = cleaned.LastIndexOf('.');
        var ext = dot > 0 && cleaned.Length - dot <= 12 ? cleaned[dot..] : "";
        return cleaned[..(MaxFileNameLength - ext.Length)].TrimEnd(' ', '.') + ext;
    }

    /// <summary>Brief attachments: owned by the opportunity, keyed by their own id.</summary>
    public static string AttachmentKey(Guid opportunityId, Guid attachmentId, string safeFileName) =>
        $"opportunities/{opportunityId:N}/brief/{attachmentId:N}/{safeFileName}";

    /// <summary>
    /// An entry hands in a bounded set of files, not a folder: enough for a
    /// logo pack (sources, exports, one-colour, reversed, notes) with room
    /// to spare, and few enough that a client can read all of them.
    /// </summary>
    public const int MaxSubmissionsPerEntry = 20;

    /// <summary>The entrant's word on a file, checked again against the real object at confirm.</summary>
    public static string? SubmissionProblem(string? fileName, long sizeBytes, long maxBytes, int existingCount)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return "The file needs a name.";
        if (existingCount >= MaxSubmissionsPerEntry)
            return $"An entry carries at most {MaxSubmissionsPerEntry} files — delete one you no longer stand by, or bundle the rest.";
        if (sizeBytes <= 0)
            return "The file is empty.";
        if (sizeBytes > maxBytes)
            return TooLarge(maxBytes);
        return null;
    }


    /// <summary>Text shows in the page only up to this size — past it a preview is a wall, not a glance.</summary>
    public const long MaxTextPreviewBytes = 1024 * 1024;

    /// <summary>
    /// What a text preview is served as, whatever the file: the page reads
    /// the characters and decides the rendering from the listed type, and a
    /// Markdown, CSV or JSON file opened by its address alone is then only
    /// ever text.
    /// </summary>
    public const string TextPreviewContentType = "text/plain; charset=utf-8";

    // What a brief file can be shown as, and the one type it is then served
    // as. Found by extension first: browsers often declare nothing for a .md,
    // and Excel claims every .csv as a spreadsheet.
    private static readonly (string Extension, string Kind, string ContentType)[] Previews =
    [
        (".png", "image", "image/png"),
        (".jpg", "image", "image/jpeg"),
        (".jpeg", "image", "image/jpeg"),
        (".gif", "image", "image/gif"),
        (".webp", "image", "image/webp"),
        (".svg", "image", "image/svg+xml"),
        (".pdf", "pdf", "application/pdf"),
        (".mp4", "video", "video/mp4"),
        (".webm", "video", "video/webm"),
        (".mp3", "audio", "audio/mpeg"),
        (".wav", "audio", "audio/wav"),
        (".ogg", "audio", "audio/ogg"),
        (".m4a", "audio", "audio/mp4"),
        (".txt", "text", "text/plain"),
        (".md", "text", "text/markdown"),
        (".markdown", "text", "text/markdown"),
        (".csv", "text", "text/csv"),
        (".json", "text", "application/json"),
    ];

    /// <summary>
    /// How a page can show a stored file (a brief attachment, or a file an
    /// entrant handed in) in place of a bare link: an image, a PDF, a video
    /// or audio player, or text read in the page; or null, and the file stays
    /// a download. The preview's type is the one the view link serves, never
    /// the uploader's word, so what renders is only ever what the kind says:
    /// an HTML page declared as a picture comes back a broken picture, and an
    /// SVG renders only through an &lt;img&gt;, where a script in it is inert.
    /// </summary>
    public static FilePreview? Preview(string? contentType, string fileName, long sizeBytes)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        var declared = (contentType ?? "").Split(';')[0].Trim().ToLowerInvariant();
        var match = Array.Find(Previews, p => p.Extension == extension);
        if (match.Kind is null) match = Array.Find(Previews, p => p.ContentType == declared);
        if (match.Kind is null) return null;
        if (match.Kind == "text" && sizeBytes > MaxTextPreviewBytes) return null;
        return new FilePreview(match.Kind, match.ContentType);
    }

    /// <summary>
    /// Only a PDF preview is served inline: the browser's own viewer is the
    /// preview, and it cannot open a download. An image, a player and a
    /// fetch read a forced download all the same, so nothing else needs the
    /// exception — and an SVG opened on its own must still never become a page.
    /// </summary>
    public static bool OpensInline(FilePreview? preview) => preview?.Kind == "pdf";

    /// <summary>Entry submissions: owned by the entry, keyed by their own id.</summary>
    public static string SubmissionKey(Guid entryId, Guid submissionId, string safeFileName) =>
        $"entries/{entryId:N}/files/{submissionId:N}/{safeFileName}";

    /// <summary>The review fallback: one ZIP of the frozen final tag per entry, cached forever — a frozen repo never changes.</summary>
    public static string EntryZipKey(Guid entryId) =>
        $"entries/{entryId:N}/final.zip";

    /// <summary>
    /// Forced-download disposition, signed into every download link. Files
    /// other users uploaded never render in the browser — an HTML or SVG
    /// "brief attachment" must not become a page someone can be sent to.
    /// Inline only for a preview that needs it (OpensInline), which is served
    /// as the type its preview names. ASCII fallback plus RFC 5987 for names
    /// beyond it.
    /// </summary>
    public static string ContentDisposition(string fileName, bool inline = false)
    {
        var ascii = new StringBuilder(fileName.Length);
        foreach (var c in fileName)
            ascii.Append(c is >= ' ' and <= '~' && c != '"' && c != '\\' ? c : '_');
        return $"{(inline ? "inline" : "attachment")}; filename=\"{ascii}\"; filename*=UTF-8''{S3Presign.Rfc3986(fileName)}";
    }
}

/// <summary>How a stored file shows in a page: the kind of preview, and the type the view link serves it as.</summary>
public sealed record FilePreview(string Kind, string ContentType);
