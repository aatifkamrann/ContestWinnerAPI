using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using WinnersPortal.Domain;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Services.Auth;
using WinnersPortal.Services.Common;
using WinnersPortal.Services.GitHub;

namespace WinnersPortal.Services.Storage;

/// <summary>
/// The blueprint's review fallback: a client who will not create a GitHub
/// account still gets the work — a signed ZIP of the frozen <c>final</c> tag.
/// Cut from GitHub on first request, cached in storage forever (a frozen
/// repo never changes), and handed out only to the opportunity's owner through
/// a short-lived signed link.
/// </summary>
public sealed class EntryZipService(AppDbContext db, StorageService storage, GitHubService github)
{
    /// <summary>A ZIP bigger than this is not a review artifact — read it on GitHub.</summary>
    public const long MaxZipBytes = 500L * 1024 * 1024;

    public async Task<Outcome<EntryZipResponse>> PackageAsync(Guid id, ClaimsPrincipal principal, CancellationToken ct)
    {
        var entry = await db.Entries.Include(e => e.Opportunity)
            .SingleOrDefaultAsync(e => e.Id == id, ct);
        // Only the pair who own the review may know the entry exists:
        // the client the work was built for — and, once it froze, the
        // entrant who built it (their own final tag, nobody else's).
        var viewerId = Principal.UserId(principal);
        var isOwner = entry is not null && entry.Opportunity!.ClientId == viewerId;
        var isEntrant = entry is not null && entry.FreelancerId == viewerId;
        if (entry is null || (!isOwner && !isEntrant)) return Outcome.NotFound();

        if (entry.FrozenAtUtc is null || entry.Status != EntryStatus.Active)
            return Outcome.Conflict("The ZIP is cut from the frozen final tag — it exists once the deadline freezes this entry.");
        if (entry.RepoFullName is null)
            return Outcome.Conflict("This entry never got a repository, so there is nothing to package.");
        if (entry.ZipPackagedAtUtc is null && await storage.UploadSetupAsync(ct) is null)
            return Outcome.Conflict("File storage is not configured on this portal, so ZIP packaging is unavailable — review the code on GitHub instead.");

        var fileName = StorageRules.SafeFileName(
            $"{entry.Opportunity!.Slug}-{entry.GithubUsername}-final.zip");

        // Package on first request; a frozen repo never changes, so
        // every later request is just a fresh signature on the cache.
        if (entry.ZipPackagedAtUtc is null)
        {
            var key = StorageRules.EntryZipKey(entry.Id);
            var tempPath = Path.GetTempFileName();
            try
            {
                long size;
                await using (var temp = System.IO.File.Create(tempPath))
                {
                    try
                    {
                        size = await github.DownloadZipballAsync(
                            entry.RepoFullName, "refs/tags/final", temp, MaxZipBytes, ct);
                    }
                    catch (GitHubApiException e) when (e.StatusCode == 404)
                    {
                        // Frozen but never tagged = nothing was ever pushed.
                        return Outcome.Conflict("This repository has no final tag — nothing was ever pushed to package.");
                    }
                    catch (GitHubApiException e)
                    {
                        return Outcome.Conflict($"GitHub would not hand over the archive: {e.Message}");
                    }
                }

                // Read again rather than carried from the check above:
                // the download can take minutes, and the package goes
                // where new files go when it lands.
                var storageSetup = await storage.UploadSetupAsync(ct)
                    ?? throw new StorageException(0, null, "File storage was switched off while the archive downloaded.");
                await using (var packaged = System.IO.File.OpenRead(tempPath))
                {
                    await storage.PutAsync(storageSetup, key, packaged, size, "application/zip", ct);
                }

                entry.ZipStorageKey = key;
                entry.ZipStorageSetup = storageSetup;
                entry.ZipSizeBytes = size;
                entry.ZipPackagedAtUtc = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
            }
            finally
            {
                System.IO.File.Delete(tempPath);
            }
        }

        var url = await storage.DownloadUrlAsync(
            entry.ZipStorageSetup, entry.ZipStorageKey!, fileName, "application/zip", ct);
        return Outcome.Ok(new EntryZipResponse
        {
            Url = url,
            FileName = fileName,
            SizeBytes = entry.ZipSizeBytes,
            PackagedAtUtc = entry.ZipPackagedAtUtc,
            ExpiresInSeconds = (int)StorageService.DownloadLinkLife.TotalSeconds,
        });
    }
}
