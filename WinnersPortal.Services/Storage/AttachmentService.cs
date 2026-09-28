using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using WinnersPortal.Domain;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Services.Auth;
using WinnersPortal.Services.Common;

namespace WinnersPortal.Services.Storage;

/// <summary>
/// Brief attachments. Reserve a slot here, then send the bytes here: the
/// API writes them to the store and stamps the row in the same request, so
/// a browser never talks to the bucket and the bucket needs no CORS rule.
/// Every download is still a fresh five-minute signed link, so the
/// permission check runs on every request while only the payload moves.
///
/// Attachments are brief material, so they share the brief's lifecycle:
/// editable while the opportunity is a draft, locked the moment it publishes,
/// and readable by any signed-in user once it does.
/// </summary>
public sealed class AttachmentService(AppDbContext db, StorageService storage)
{
    public async Task<Outcome<UploadSlotResponse>> ReserveAsync(Guid id, ReserveAttachmentRequest request, ClaimsPrincipal principal, CancellationToken ct)
    {
        var opportunity = await db.Opportunities
            .SingleOrDefaultAsync(c => c.Id == id && c.ClientId == Principal.UserId(principal), ct);
        if (opportunity is null) return Outcome.NotFound();
        if (opportunity.Status != OpportunityStatus.Draft)
            return Outcome.Conflict("Attachments are part of the brief — published briefs are locked, and so are their files.");
        if (await storage.UploadSetupAsync(ct) is not { } storageSetup)
            return Outcome.Conflict("File storage is not configured on this portal yet — ask the operator, or link the file from the brief.");

        // Reservations that never became uploads free their slot here —
        // a closed tab should not eat into the attachment cap for good.
        // On SQL Server, Attachment_DropStale.
        var stale = DateTimeOffset.UtcNow - StorageService.ReservationLife;
        if (db.UseDapper) await db.Sql.ExecuteAsync(Procedures.AttachmentDropStale, new { id, stale }, ct);
        else await db.Attachments
            .Where(a => a.OpportunityId == id && a.UploadedAtUtc == null && a.CreatedAtUtc < stale)
            .ExecuteDeleteAsync(ct);

        var existing = await db.Attachments.CountAsync(a => a.OpportunityId == id, ct);
        var maxBytes = await storage.MaxUploadBytesAsync(ct);
        if (StorageRules.Problem(request.FileName, request.SizeBytes, maxBytes, existing) is { } problem)
            return Outcome.Invalid(problem);

        var attachment = new Attachment
        {
            Id = Guid.NewGuid(),
            OpportunityId = opportunity.Id,
            FileName = StorageRules.SafeFileName(request.FileName),
            ContentType = string.IsNullOrWhiteSpace(request.ContentType)
                ? "application/octet-stream"
                : request.ContentType.Trim(),
            SizeBytes = request.SizeBytes,
            StorageKey = "",
            StorageSetup = storageSetup,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        attachment.StorageKey = StorageRules.AttachmentKey(opportunity.Id, attachment.Id, attachment.FileName);
        db.Attachments.Add(attachment);
        await db.SaveChangesAsync(ct);

        return Outcome.Ok(new UploadSlotResponse { Id = attachment.Id, FileName = attachment.FileName });
    }

    /// <summary>
    /// The bytes, as the request that carries them is read: written to the
    /// store under the reserved key, then the row stamped with what really
    /// arrived — the declared size was the promise, the bytes are the truth.
    /// </summary>
    public async Task<Outcome<AttachmentSummary>> UploadAsync(
        Guid id, Stream body, long? length, ClaimsPrincipal principal, CancellationToken ct)
    {
        var attachment = await db.Attachments.Include(a => a.Opportunity)
            .SingleOrDefaultAsync(a => a.Id == id && a.Opportunity!.ClientId == Principal.UserId(principal), ct);
        if (attachment is null) return Outcome.NotFound();
        // The same file sent twice is one upload; say what it already is.
        if (attachment.UploadedAtUtc is not null) return Outcome.Ok(Dto(attachment));
        if (attachment.Opportunity!.Status != OpportunityStatus.Draft)
            return Outcome.Conflict("Attachments are part of the brief — published briefs are locked, and so are their files.");

        var maxBytes = await storage.MaxUploadBytesAsync(ct);
        var stored = await storage.StoreAsync(
            attachment.StorageSetup, attachment.StorageKey, body, length, attachment.ContentType, maxBytes, ct);
        if (stored is null) return Outcome.Invalid(StorageRules.TooLarge(maxBytes));

        attachment.SizeBytes = stored.Value;
        attachment.UploadedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Outcome.Ok(Dto(attachment));
    }

    public async Task<Outcome> DeleteAsync(Guid id, ClaimsPrincipal principal, CancellationToken ct)
    {
        var attachment = await db.Attachments.Include(a => a.Opportunity)
            .SingleOrDefaultAsync(a => a.Id == id && a.Opportunity!.ClientId == Principal.UserId(principal), ct);
        if (attachment is null) return Outcome.NotFound();
        if (attachment.Opportunity!.Status != OpportunityStatus.Draft)
            return Outcome.Conflict("The brief is published — entrants may be building against this file.");

        await storage.DeleteAsync(attachment.StorageSetup, attachment.StorageKey, ct);
        db.Attachments.Remove(attachment);
        await db.SaveChangesAsync(ct);
        return Outcome.NoContent();
    }

    public async Task<Outcome<AttachmentListResponse>> ListAsync(Guid id, ClaimsPrincipal principal, CancellationToken ct)
    {
        var owns = await db.Opportunities.AnyAsync(c => c.Id == id && c.ClientId == Principal.UserId(principal), ct);
        if (!owns) return Outcome.NotFound();
        var attachments = await db.Attachments
            .Where(a => a.OpportunityId == id && a.UploadedAtUtc != null)
            .OrderBy(a => a.UploadedAtUtc)
            .ToListAsync(ct);
        return Outcome.Ok(new AttachmentListResponse { Items = attachments.Select(Dto) });
    }

    public async Task<Outcome> DownloadAsync(Guid id, ClaimsPrincipal principal, CancellationToken ct)
    {
        var viewerId = Principal.UserId(principal);
        // A person, not a bot: attachments are for people weighing an
        // entry. The edge sends a signed-out browser to the login page.
        if (viewerId is null)
            return Outcome.Unauthorized();

        var attachment = await ReadableAsync(db, id, viewerId.Value, ct);
        if (attachment is null) return Outcome.NotFound();

        var url = await storage.DownloadUrlAsync(
            attachment.StorageSetup, attachment.StorageKey, attachment.FileName, attachment.ContentType, ct);
        return Outcome.Redirect(url);
    }

    public async Task<Outcome> ViewAsync(Guid id, ClaimsPrincipal principal, CancellationToken ct)
    {
        var viewerId = Principal.UserId(principal);
        if (viewerId is null)
            return Outcome.Unauthorized();

        var attachment = await ReadableAsync(db, id, viewerId.Value, ct);
        if (attachment is null) return Outcome.NotFound();

        var preview = StorageRules.Preview(attachment.ContentType, attachment.FileName, attachment.SizeBytes);
        var url = await storage.DownloadUrlAsync(
            attachment.StorageSetup, attachment.StorageKey, attachment.FileName, preview?.ContentType ?? attachment.ContentType, ct,
            inline: StorageRules.OpensInline(preview));
        return Outcome.Redirect(url);
    }

    /// <summary>
    /// A text preview's contents, read from the store here and handed to
    /// the page as plain text: the page fetches the API it already talks
    /// to, from any host, and no bucket is asked to answer a browser. Only
    /// for a file whose preview is text — small by the rule that names it.
    /// </summary>
    public async Task<Outcome> TextAsync(Guid id, ClaimsPrincipal principal, CancellationToken ct)
    {
        var viewerId = Principal.UserId(principal);
        if (viewerId is null)
            return Outcome.Unauthorized();

        var attachment = await ReadableAsync(db, id, viewerId.Value, ct);
        if (attachment is null) return Outcome.NotFound();
        if (StorageRules.Preview(attachment.ContentType, attachment.FileName, attachment.SizeBytes)?.Kind != "text")
            return Outcome.NotFound();

        var bytes = await storage.ReadAsync(attachment.StorageSetup, attachment.StorageKey, ct);
        return Outcome.Bytes(bytes, StorageRules.TextPreviewContentType);
    }

    private sealed record ReadableFile(string? StorageSetup, string StorageKey, string FileName, string ContentType, long SizeBytes);

    /// <summary>
    /// The one rule for reading a brief file, shared by the download and the
    /// preview: confirmed, and on a published opportunity or the viewer's own
    /// draft. A stranger asking about a draft's file gets the same null as a
    /// missing one — drafts are invisible, files included.
    /// </summary>
    private static async Task<ReadableFile?> ReadableAsync(AppDbContext db, Guid id, Guid viewerId, CancellationToken ct)
    {
        var row = await db.Attachments.AsNoTracking()
            .Where(a => a.Id == id && a.UploadedAtUtc != null)
            .Select(a => new
            {
                a.StorageSetup, a.StorageKey, a.FileName, a.ContentType, a.SizeBytes,
                a.Opportunity!.Status, a.Opportunity.ClientId,
            })
            .SingleOrDefaultAsync(ct);
        return row is null || (row.Status == OpportunityStatus.Draft && row.ClientId != viewerId)
            ? null
            : new ReadableFile(row.StorageSetup, row.StorageKey, row.FileName, row.ContentType, row.SizeBytes);
    }

    private static AttachmentSummary Dto(Attachment a) =>
        Summary(a.Id, a.FileName, a.ContentType, a.SizeBytes, a.UploadedAtUtc);

    /// <summary>
    /// A brief file as the opportunity page and the draft editor both list it:
    /// with the preview the page can show (null keeps it a download) and the
    /// content type the view link then serves it as — so the client sees
    /// each file while drafting exactly as entrants will.
    /// </summary>
    internal static AttachmentSummary Summary(
        Guid id, string fileName, string contentType, long sizeBytes, DateTimeOffset? uploadedAtUtc)
    {
        var preview = StorageRules.Preview(contentType, fileName, sizeBytes);
        return new AttachmentSummary
        {
            Id = id,
            FileName = fileName,
            ContentType = preview?.ContentType ?? contentType,
            SizeBytes = sizeBytes,
            UploadedAtUtc = uploadedAtUtc,
            Preview = preview?.Kind,
        };
    }
}

public sealed record ReserveAttachmentRequest(string? FileName, string? ContentType, long SizeBytes);
