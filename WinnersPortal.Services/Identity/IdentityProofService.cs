using Microsoft.EntityFrameworkCore;
using WinnersPortal.Domain;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Services.Common;
using WinnersPortal.Services.Storage;

namespace WinnersPortal.Services.Identity;

/// <summary>
/// The administrator's view of a member's verification proof: the verdict,
/// the facts read off the document, the provider's whole decision, and the
/// stored copies of its images — each opened through a short-lived signed
/// link, never a public one. Administrators only; the member's own screens
/// show the verdict and nothing more.
/// </summary>
public sealed class IdentityProofService(AppDbContext db, StorageService storage, IdentityProofWorkSignal signal)
{
    public async Task<Outcome<IdentityProofResponse>> RecordAsync(Guid userId, CancellationToken ct)
    {
        var row = await db.IdentityVerifications.AsNoTracking().SingleOrDefaultAsync(v => v.UserId == userId, ct);
        if (row is null) return Outcome.NotFound();
        var documents = await db.IdentityDocuments.AsNoTracking()
            .Where(d => d.VerificationId == row.Id && d.RemovedAtUtc == null)
            .OrderBy(d => d.StorageKey)
            .ToListAsync(ct);
        return Outcome.Ok(new IdentityProofResponse
        {
            Status = IdentityRules.StatusName(row.Status),
            Provider = row.Provider,
            SessionId = row.SessionId,
            StartedAtUtc = row.CreatedAtUtc,
            DecidedAtUtc = row.DecidedAtUtc,
            Note = row.Note,
            DecisionReadAtUtc = row.DecisionReadAtUtc,
            Decision = row.DecisionJson is null ? null : IdentityProof.Pretty(row.DecisionJson),
            Facts = IdentityProof.Summary(row.DecisionJson)
                .Select(f => new IdentityProofFact { Label = f.Label, Value = f.Value }),
            Documents = documents.Select(d => new IdentityProofDocument
            {
                Id = d.Id,
                Label = IdentityProof.Label(d.Name),
                ContentType = d.ContentType,
                SizeBytes = d.SizeBytes,
            }),
            ProofPending = row.ProofDueAtUtc is not null,
            ProofError = row.ProofError,
        });
    }

    /// <summary>Reads the decision and copies its images again — after storage was configured, or a copy gave up.</summary>
    public async Task<Outcome<IdentityProofResponse>> FetchAgainAsync(Guid userId, CancellationToken ct)
    {
        var row = await db.IdentityVerifications.SingleOrDefaultAsync(v => v.UserId == userId, ct);
        if (row is null) return Outcome.NotFound();
        row.ProofDueAtUtc = DateTimeOffset.UtcNow;
        row.ProofAttempts = 0;
        row.ProofError = null;
        await db.SaveChangesAsync(ct);
        signal.Wake();
        return await RecordAsync(userId, ct);
    }

    /// <summary>One stored image, opened inline through a five-minute signed link.</summary>
    public async Task<Outcome> ViewAsync(Guid documentId, CancellationToken ct)
    {
        var document = await db.IdentityDocuments.AsNoTracking()
            .SingleOrDefaultAsync(d => d.Id == documentId && d.RemovedAtUtc == null, ct);
        if (document is null) return Outcome.NotFound();
        var fileName = $"{IdentityProof.Label(document.Name).Replace(" · ", " - ")}.{IdentityProof.Extension(document.ContentType)}";
        var url = await storage.DownloadUrlAsync(
            document.StorageSetup, document.StorageKey, fileName, document.ContentType, ct, inline: true);
        return Outcome.Redirect(url);
    }
}
