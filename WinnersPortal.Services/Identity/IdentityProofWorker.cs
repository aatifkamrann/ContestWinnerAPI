using Microsoft.EntityFrameworkCore;
using WinnersPortal.Domain;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Services.Common;
using WinnersPortal.Services.Storage;

namespace WinnersPortal.Services.Identity;

public sealed class IdentityProofWorkSignal(WorkRelay? relay = null) : WorkSignal("identity-proof", relay);

/// <summary>
/// Keeps the proof behind each identity verdict: when a verdict lands, the
/// provider's whole decision is read and kept on the verification, and every
/// image it links to — the document's front and back, the portrait read off
/// it, the selfie — is copied into the portal's own file storage as an
/// <see cref="IdentityDocument"/>, because the provider's links expire.
/// A copy that fails is retried with backoff and, after
/// <see cref="IdentityProof.MaxAttempts"/> tries, given up on with the reason
/// on the administrator's panel. The same sweep deletes the stored images
/// nobody should keep any more: ones a newer decision replaced, ones of an
/// erased account, and ones whose verification was reset or deleted —
/// the file first, then the row.
/// </summary>
public sealed class IdentityProofWorker(
    IServiceScopeFactory scopes,
    IdentityOptions options,
    IdentityProviderClient client,
    StorageService storage,
    IdentityProofWorkSignal signal,
    ILogger<IdentityProofWorker> log,
    AppPause pause) : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(30);
    private const int Batch = 10;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // No cycle while a database move has the portal paused.
                using var lease = pause.TryEnter();
                if (lease is not null) await RunCycleAsync(ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError(e, "Identity proof worker cycle failed; retrying next sweep.");
            }
            await signal.WaitAsync(SweepInterval, ct);
        }
    }

    private async Task RunCycleAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await SweepAsync(db, ct);
        await CopyDueAsync(db, DateTimeOffset.UtcNow, ct);
    }

    // ------------------------------------------------------------ the sweep

    /// <summary>Stored images marked removed, or left by a verification that is gone: file, then row.</summary>
    private async Task SweepAsync(AppDbContext db, CancellationToken ct)
    {
        var gone = await db.IdentityDocuments
            .Where(d => d.RemovedAtUtc != null || !db.IdentityVerifications.Any(v => v.Id == d.VerificationId))
            .OrderBy(d => d.CreatedAtUtc)
            .Take(50)
            .ToListAsync(ct);
        foreach (var document in gone)
        {
            try
            {
                await storage.DeleteAsync(document.StorageSetup, document.StorageKey, ct);
                db.IdentityDocuments.Remove(document);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Kept, so the next sweep tries again: a row is the only
                // record that the file still exists.
                log.LogWarning(e, "Deleting a stored identity image failed; it stays queued for deletion.");
            }
        }
        if (gone.Count > 0) await db.SaveChangesAsync(ct);
    }

    // ------------------------------------------------------------- the copy

    private async Task CopyDueAsync(AppDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var due = await db.IdentityVerifications
            .Where(v => v.ProofDueAtUtc != null && v.ProofDueAtUtc <= now)
            .OrderBy(v => v.ProofDueAtUtc)
            .Take(Batch)
            .ToListAsync(ct);
        foreach (var row in due)
        {
            try
            {
                await CopyAsync(db, row, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                Failed(row, e.Message, now);
                log.LogWarning(e, "Copying the proof of verification {Id} failed (try {Attempt}).", row.Id, row.ProofAttempts);
            }
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task CopyAsync(AppDbContext db, IdentityVerification row, CancellationToken ct)
    {
        var config = await options.ProviderConfigAsync(ct);
        if (config is null || config.Provider != row.Provider)
        {
            GiveUp(row, $"No active identity setup for {row.Provider} can read this decision. Switch it back on, then choose Fetch again.");
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var decision = await client.ReadDecisionJsonAsync(config.Provider, config.ApiKey, row.SessionId, ct);
        row.DecisionJson = decision;
        row.DecisionReadAtUtc = now;

        // The images this decision replaces go, whatever happens next: a
        // stale copy beside a new decision would prove the wrong thing.
        var previous = await db.IdentityDocuments
            .Where(d => d.VerificationId == row.Id && d.RemovedAtUtc == null)
            .ToListAsync(ct);
        foreach (var old in previous) old.RemovedAtUtc = now;

        var images = IdentityProof.Images(decision);
        if (images.Count == 0)
        {
            Done(row);
            return;
        }
        if (await storage.UploadSetupAsync(ct) is not { } setup)
        {
            GiveUp(row, "File storage is not configured, so the document images were not copied; the decision is kept. "
                + "Configure storage, then choose Fetch again.");
            return;
        }

        var problems = new List<string>();
        for (var i = 0; i < images.Count; i++)
        {
            var image = images[i];
            try
            {
                var (bytes, type) = await client.DownloadAsync(image.Url, IdentityProof.MaxImageBytes, ct);
                if (!IdentityProof.Keepable(type))
                {
                    problems.Add($"{IdentityProof.Label(image.Name)}: not an image ({type ?? "no type"})");
                    continue;
                }
                var key = IdentityProof.StorageKey(row.UserId, row.Id, i + 1, image.Name, type);
                using (var stream = new MemoryStream(bytes))
                    await storage.PutAsync(setup, key, stream, bytes.LongLength, type!, ct);
                db.IdentityDocuments.Add(new IdentityDocument
                {
                    Id = Guid.NewGuid(),
                    VerificationId = row.Id,
                    UserId = row.UserId,
                    Name = image.Name.Length > IdentityDocument.MaxName ? image.Name[..IdentityDocument.MaxName] : image.Name,
                    ContentType = type!,
                    SizeBytes = bytes.LongLength,
                    StorageSetup = setup,
                    StorageKey = key,
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                });
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                problems.Add($"{IdentityProof.Label(image.Name)}: {e.Message}");
            }
        }

        if (problems.Count == 0) Done(row);
        else Failed(row, $"{problems.Count} of {images.Count} images could not be copied — {string.Join("; ", problems)}", now);
    }

    private static void Done(IdentityVerification row)
    {
        row.ProofDueAtUtc = null;
        row.ProofAttempts = 0;
        row.ProofError = null;
    }

    private static void GiveUp(IdentityVerification row, string why)
    {
        row.ProofDueAtUtc = null;
        row.ProofError = Cut(why);
    }

    private static void Failed(IdentityVerification row, string why, DateTimeOffset now)
    {
        row.ProofAttempts++;
        row.ProofError = Cut(why);
        row.ProofDueAtUtc = row.ProofAttempts >= IdentityProof.MaxAttempts
            ? null
            : now + IdentityProof.Backoff(row.ProofAttempts);
    }

    private static string Cut(string s) => s.Length <= 400 ? s : s[..397] + "…";
}
