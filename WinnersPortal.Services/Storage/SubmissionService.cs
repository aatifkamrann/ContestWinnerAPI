using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using WinnersPortal.Domain;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Services.Auth;
using WinnersPortal.Services.Common;
using WinnersPortal.Services.Opportunities;
using WinnersPortal.Services.Live;

namespace WinnersPortal.Services.Storage;

/// <summary>
/// The upload half of delivery: an entrant's files, on the same path as
/// brief attachments — reserve a slot here, send the bytes here, and the
/// API writes them to the store and stamps the row in that one request;
/// every download is a fresh signed link.
///
/// The lifecycle mirrors the repository's: files land while the opportunity is
/// open and freeze at the deadline; a file tagged to a milestone claims it
/// first-come and the claim never comes off; the client opens the files
/// from the deadline on, the entrant and an administrator at any time.
/// </summary>
public sealed class SubmissionService(AppDbContext db, StorageService storage, ILiveBoard live)
{
    public async Task<Outcome<UploadSlotResponse>> ReserveAsync(Guid id, ReserveSubmissionRequest request, ClaimsPrincipal principal, CancellationToken ct)
    {
        var entry = await db.Entries
            .Include(e => e.Opportunity).ThenInclude(c => c!.Milestones)
            .SingleOrDefaultAsync(e => e.Id == id && e.FreelancerId == Principal.UserId(principal), ct);
        if (entry is null) return Outcome.NotFound();
        if (entry.Status != EntryStatus.Active)
            return Outcome.Conflict("This entry is no longer active.");
        if (!Delivery.UsesUpload(entry.Opportunity!.Delivery))
            return Outcome.Conflict("This opportunity is delivered through GitHub — push to your repository instead.");
        var book = MilestonePay.ByMilestone(entry.Opportunity.Kind)
            ? await MilestonePaymentService.ReadAsync(db, entry.OpportunityId, entry.Id, ct)
            : null;
        if (Delivery.UploadProblem(entry.Opportunity.Status, entry.Opportunity.DeadlineUtc, DateTimeOffset.UtcNow,
                entry.Opportunity.Kind, book is not null && MilestonePay.Complete(book.States)) is { } shut)
            return Outcome.Conflict(shut);
        if (await storage.UploadSetupAsync(ct) is not { } storageSetup)
            return Outcome.Conflict("File storage is not configured on this portal yet — ask the operator.");

        // Milestones are 1-based on every screen and every tag (m1, m2…).
        Milestone? milestone = null;
        if (request.Milestone is { } number)
        {
            milestone = entry.Opportunity.Milestones.SingleOrDefault(m => m.Order == number - 1);
            if (milestone is null)
                return Outcome.Invalid($"This opportunity has no milestone {number}.");
            // Paid by milestone, a file tagged with one hands it in — so only
            // the milestone being worked on, said now rather than after the
            // bytes have travelled.
            if (book is not null && MilestonePay.ClaimProblem(entry.Opportunity.Status, book.States, number - 1) is { } closed)
                return Outcome.Conflict(closed);
        }

        // Reservations that never became uploads free their slot here —
        // a closed tab should not eat into the file cap for good.
        var stale = DateTimeOffset.UtcNow - StorageService.ReservationLife;
        await db.Submissions
            .Where(s => s.EntryId == id && s.UploadedAtUtc == null && s.CreatedAtUtc < stale)
            .ExecuteDeleteAsync(ct);

        var existing = await db.Submissions.CountAsync(s => s.EntryId == id, ct);
        var maxBytes = await storage.MaxUploadBytesAsync(ct);
        if (StorageRules.SubmissionProblem(request.FileName, request.SizeBytes, maxBytes, existing) is { } problem)
            return Outcome.Invalid(problem);

        var submission = new Submission
        {
            Id = Guid.NewGuid(),
            EntryId = entry.Id,
            MilestoneId = milestone?.Id,
            FileName = StorageRules.SafeFileName(request.FileName),
            ContentType = string.IsNullOrWhiteSpace(request.ContentType)
                ? "application/octet-stream"
                : request.ContentType.Trim(),
            SizeBytes = request.SizeBytes,
            StorageKey = "",
            StorageSetup = storageSetup,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        submission.StorageKey = StorageRules.SubmissionKey(entry.Id, submission.Id, submission.FileName);
        db.Submissions.Add(submission);
        await db.SaveChangesAsync(ct);

        return Outcome.Ok(new UploadSlotResponse { Id = submission.Id, FileName = submission.FileName });
    }

    /// <summary>
    /// The bytes, as the request that carries them is read: written to the
    /// store under the reserved key, then the row stamped with what really
    /// arrived, and the milestone claimed if the file carries a tag.
    /// </summary>
    public async Task<Outcome<SubmissionSummary>> UploadAsync(
        Guid id, Stream body, long? length, ClaimsPrincipal principal, CancellationToken ct)
    {
        var submission = await db.Submissions
            .Include(s => s.Entry).ThenInclude(e => e!.Opportunity)
            .Include(s => s.Milestone)
            .SingleOrDefaultAsync(s => s.Id == id && s.Entry!.FreelancerId == Principal.UserId(principal), ct);
        if (submission is null) return Outcome.NotFound();
        // The same file sent twice is one upload; say what it already is.
        if (submission.UploadedAtUtc is not null) return Outcome.Ok(Dto(submission));

        var entry = submission.Entry!;
        var opportunity = entry.Opportunity!;
        if (entry.Status != EntryStatus.Active)
            return Outcome.Conflict("This entry is no longer active.");
        var byMilestone = MilestonePay.ByMilestone(opportunity.Kind);
        var complete = byMilestone
            && MilestonePay.Complete((await MilestonePaymentService.ReadAsync(db, opportunity.Id, entry.Id, ct)).States);
        if (Delivery.UploadProblem(opportunity.Status, opportunity.DeadlineUtc, DateTimeOffset.UtcNow, opportunity.Kind, complete) is { } closed)
            return Outcome.Conflict(closed);

        // The declared size was the promise; the bytes are the truth.
        var maxBytes = await storage.MaxUploadBytesAsync(ct);
        var stored = await storage.StoreAsync(
            submission.StorageSetup, submission.StorageKey, body, length, submission.ContentType, maxBytes, ct);
        if (stored is null) return Outcome.Invalid(StorageRules.TooLarge(maxBytes));

        // The deadline is the deadline. A file whose bytes landed after
        // it is not part of the entry — the same fate as a tag pushed
        // into a frozen repository, made explicit because here the
        // entrant is still looking at the page.
        var now = DateTimeOffset.UtcNow;
        if (Delivery.UploadProblem(opportunity.Status, opportunity.DeadlineUtc, now, opportunity.Kind, complete) is { } shut)
        {
            await storage.DeleteAsync(submission.StorageSetup, submission.StorageKey, ct);
            db.Submissions.Remove(submission);
            await db.SaveChangesAsync(ct);
            return Outcome.Conflict(shut + " This file was not kept.");
        }

        submission.SizeBytes = stored.Value;
        submission.UploadedAtUtc = now;
        // An upload is activity, the way a push is — the board's "last
        // activity" column and the entrant's record both read it.
        entry.LastPushAtUtc = now;
        entry.PushCount += 1;

        // The claim, first-come: the file keeps its milestone only when
        // it is the one that claimed it. A later file tagged to the same
        // milestone is stored plain, so the board and the file agree
        // about which upload the claim points at.
        var claimed = false;
        if (byMilestone && submission.MilestoneId is not null)
        {
            // Paid by milestone: the file hands the milestone in, or hands
            // it in again after a request for changes. Saved with the file.
            var order = submission.Milestone!.Order;
            var (problem, _, _) = await MilestonePaymentService.HandInAsync(
                db, entry, opportunity, order, "upload", submission.FileName, null, now, ct);
            // Refused — handed in meanwhile, or no longer the one open — the
            // file is kept plain, as a competitive late claim is.
            if (problem is not null) submission.MilestoneId = null;
            else claimed = true;
        }
        else if (submission.MilestoneId is { } milestoneId)
        {
            var already = await db.Checkpoints.AnyAsync(
                cp => cp.EntryId == entry.Id && cp.MilestoneId == milestoneId, ct);
            if (already)
            {
                submission.MilestoneId = null;
            }
            else
            {
                db.Checkpoints.Add(new Checkpoint
                {
                    Id = Guid.NewGuid(),
                    EntryId = entry.Id,
                    MilestoneId = milestoneId,
                    CommitSha = null,
                    Via = "upload",
                    Ref = submission.FileName,
                    ClaimedAtUtc = now,
                });
                claimed = true;
            }
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (DbErrors.IsUniqueViolation(e))
        {
            // Two uploads raced for one milestone; the other won. This
            // file is kept, just not as the claim.
            db.ChangeTracker.Entries<Checkpoint>()
                .Where(c => c.State == EntityState.Added).ToList()
                .ForEach(c => c.State = EntityState.Detached);
            submission.MilestoneId = null;
            claimed = false;
            await db.SaveChangesAsync(ct);
        }

        // After the save, so a watcher's refetch reads the file it was
        // woken for.
        await live.OpportunityChangedAsync(opportunity.Slug, ct);
        return Outcome.Ok(Dto(submission, claimed));
    }

    public async Task<Outcome> DeleteAsync(Guid id, ClaimsPrincipal principal, CancellationToken ct)
    {
        var submission = await db.Submissions
            .Include(s => s.Entry).ThenInclude(e => e!.Opportunity)
            .SingleOrDefaultAsync(s => s.Id == id && s.Entry!.FreelancerId == Principal.UserId(principal), ct);
        if (submission is null) return Outcome.NotFound();

        var opportunity = submission.Entry!.Opportunity!;
        var frozen = Delivery.UploadProblem(opportunity.Status, opportunity.DeadlineUtc, DateTimeOffset.UtcNow);
        if (Delivery.DeleteProblem(submission.MilestoneId is not null, frozen) is { } problem)
            return Outcome.Conflict(problem);

        await storage.DeleteAsync(submission.StorageSetup, submission.StorageKey, ct);
        db.Submissions.Remove(submission);
        await db.SaveChangesAsync(ct);
        await live.OpportunityChangedAsync(opportunity.Slug, ct);
        return Outcome.NoContent();
    }

    public async Task<Outcome<SubmissionListResponse>> ListAsync(Guid id, ClaimsPrincipal principal, CancellationToken ct)
    {
        var viewerId = Principal.UserId(principal);
        var entry = await db.Entries.AsNoTracking()
            .Where(e => e.Id == id)
            .Select(e => new
            {
                e.FreelancerId, e.Status,
                e.Opportunity!.ClientId, OpportunityStatus = e.Opportunity.Status,
                e.Opportunity.DeadlineUtc, e.Opportunity.Delivery,
            })
            .SingleOrDefaultAsync(ct);
        // A stranger gets the same 404 as a missing entry.
        var isEntrant = entry is not null && entry.FreelancerId == viewerId;
        var isOwner = entry is not null && entry.ClientId == viewerId;
        var isAdmin = principal.IsInRole(Roles.Admin);
        if (entry is null || !(isEntrant || isOwner || isAdmin)) return Outcome.NotFound();
        // The owner, before the deadline: told why, not shown a 404 —
        // the page asks on their behalf and needs the reason to print.
        if (!Delivery.CanSeeFiles(isEntrant, isOwner, isAdmin, entry.OpportunityStatus))
            return Outcome.Conflict("Entrants' files open to you when the deadline passes.");

        var items = await db.Submissions.AsNoTracking()
            .Where(s => s.EntryId == id && s.UploadedAtUtc != null)
            .OrderBy(s => s.UploadedAtUtc)
            .Include(s => s.Milestone)
            .ToListAsync(ct);
        return Outcome.Ok(new SubmissionListResponse
        {
            Items = items.Select(s => Dto(s)),
            Frozen = Delivery.UploadProblem(entry.OpportunityStatus, entry.DeadlineUtc, DateTimeOffset.UtcNow) is not null,
        });
    }

    public async Task<Outcome> DownloadAsync(Guid id, ClaimsPrincipal principal, CancellationToken ct)
    {
        var viewerId = Principal.UserId(principal);
        if (viewerId is null)
            return Outcome.Unauthorized();

        var submission = await ReadableAsync(db, id, viewerId.Value, principal.IsInRole(Roles.Admin), ct);
        if (submission is null) return Outcome.NotFound();

        var url = await storage.DownloadUrlAsync(
            submission.StorageSetup, submission.StorageKey, submission.FileName, submission.ContentType, ct);
        return Outcome.Redirect(url);
    }

    public async Task<Outcome> ViewAsync(Guid id, ClaimsPrincipal principal, CancellationToken ct)
    {
        var viewerId = Principal.UserId(principal);
        if (viewerId is null)
            return Outcome.Unauthorized();

        var submission = await ReadableAsync(db, id, viewerId.Value, principal.IsInRole(Roles.Admin), ct);
        if (submission is null) return Outcome.NotFound();

        var preview = StorageRules.Preview(submission.ContentType, submission.FileName, submission.SizeBytes);
        var url = await storage.DownloadUrlAsync(
            submission.StorageSetup, submission.StorageKey, submission.FileName, preview?.ContentType ?? submission.ContentType, ct,
            inline: StorageRules.OpensInline(preview));
        return Outcome.Redirect(url);
    }

    /// <summary>A text preview's contents as plain text, on the delivery rule — see <see cref="AttachmentService.TextAsync"/>.</summary>
    public async Task<Outcome> TextAsync(Guid id, ClaimsPrincipal principal, CancellationToken ct)
    {
        var viewerId = Principal.UserId(principal);
        if (viewerId is null)
            return Outcome.Unauthorized();

        var submission = await ReadableAsync(db, id, viewerId.Value, principal.IsInRole(Roles.Admin), ct);
        if (submission is null) return Outcome.NotFound();
        if (StorageRules.Preview(submission.ContentType, submission.FileName, submission.SizeBytes)?.Kind != "text")
            return Outcome.NotFound();

        var bytes = await storage.ReadAsync(submission.StorageSetup, submission.StorageKey, ct);
        return Outcome.Bytes(bytes, StorageRules.TextPreviewContentType);
    }

    private sealed record ReadableFile(string? StorageSetup, string StorageKey, string FileName, string ContentType, long SizeBytes);

    /// <summary>
    /// The delivery rule for opening an entrant's file, shared by the download
    /// and the preview: a confirmed upload, opened by its entrant, by an
    /// administrator, or by the client once the work is frozen — the same
    /// null for a file that is not theirs to see as for one that is missing.
    /// </summary>
    private static async Task<ReadableFile?> ReadableAsync(
        AppDbContext db, Guid id, Guid viewerId, bool isAdmin, CancellationToken ct)
    {
        var row = await db.Submissions.AsNoTracking()
            .Where(s => s.Id == id && s.UploadedAtUtc != null)
            .Select(s => new
            {
                s.StorageSetup, s.StorageKey, s.FileName, s.ContentType, s.SizeBytes,
                s.Entry!.FreelancerId,
                s.Entry.Opportunity!.ClientId, OpportunityStatus = s.Entry.Opportunity.Status,
            })
            .SingleOrDefaultAsync(ct);
        return row is not null
            && Delivery.CanSeeFiles(row.FreelancerId == viewerId, row.ClientId == viewerId, isAdmin, row.OpportunityStatus)
            ? new ReadableFile(row.StorageSetup, row.StorageKey, row.FileName, row.ContentType, row.SizeBytes)
            : null;
    }

    internal static SubmissionSummary Dto(Submission s, bool? claimed = null) =>
        Summary(s.Id, s.FileName, s.ContentType, s.SizeBytes, s.UploadedAtUtc,
            s.Milestone is null ? null : s.Milestone.Order + 1, claimed);

    /// <summary>
    /// An entrant's file as every list shows it (their own panel, the
    /// client's review, the winner's files): with the preview a page can
    /// show (null keeps it a download) and the content type the view link
    /// then serves it as, exactly as a brief attachment is listed.
    /// </summary>
    internal static SubmissionSummary Summary(
        Guid id, string fileName, string contentType, long sizeBytes, DateTimeOffset? uploadedAtUtc,
        int? milestone, bool? claimed = null)
    {
        var preview = StorageRules.Preview(contentType, fileName, sizeBytes);
        return new SubmissionSummary
        {
            Id = id,
            FileName = fileName,
            ContentType = preview?.ContentType ?? contentType,
            SizeBytes = sizeBytes,
            UploadedAtUtc = uploadedAtUtc,
            // 1-based, matching the board and the tags. Null: this file carries no claim.
            Milestone = milestone,
            Preview = preview?.Kind,
            // Only on a confirm: whether this upload just claimed its milestone.
            Claimed = claimed,
        };
    }
}

/// <summary>Milestone is 1-based and optional — most files claim nothing.</summary>
public sealed record ReserveSubmissionRequest(string? FileName, string? ContentType, long SizeBytes, int? Milestone = null);
