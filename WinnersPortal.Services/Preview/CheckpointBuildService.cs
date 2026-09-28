using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using WinnersPortal.Domain;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Services.Auth;
using WinnersPortal.Services.Common;
using WinnersPortal.Services.Storage;

namespace WinnersPortal.Services.Preview;

/// <summary>
/// A claimed milestone's build as the board's dialog reads it, and its
/// whole log as a download. Both open on one rule: the entrant, the
/// opportunity's client and an administrator — the client before the deadline
/// too, because the build is the one thing of an entry they may see early
/// (the running product, never the code). Anybody else gets the same 404
/// as for a build that does not exist.
/// </summary>
public sealed class CheckpointBuildService(AppDbContext db, StorageService storage)
{
    public async Task<Outcome<CheckpointBuildResponse>> ReadAsync(Guid id, ClaimsPrincipal principal, CancellationToken ct)
    {
        var viewerId = Principal.UserId(principal);
        if (viewerId is null) return Outcome.Unauthorized();

        var row = await ReadableAsync(id, viewerId.Value, principal.IsInRole(Roles.Admin), ct);
        if (row is null) return Outcome.NotFound();

        return Outcome.Ok(new CheckpointBuildResponse
        {
            Status = CheckpointBuilds.StatusName(row.BuildStatus),
            MilestoneNumber = row.Order + 1,
            Commit = row.CommitSha is { Length: >= 7 } sha ? sha[..7] : row.CommitSha,
            StartedAtUtc = row.BuildStartedAtUtc,
            FinishedAtUtc = row.BuildFinishedAtUtc,
            Error = row.BuildStatus == PreviewBuildStatus.Failed ? row.BuildError : null,
            LogTail = row.BuildLogTail,
            LogAvailable = row.BuildLogKey is not null,
        });
    }

    // A plain link, because auth rides the cookie: the API checks who is
    // asking and answers with a redirect to a five-minute signed URL that
    // downloads the log under the milestone's and the commit's name.
    public async Task<Outcome> DownloadLogAsync(Guid id, ClaimsPrincipal principal, CancellationToken ct)
    {
        var viewerId = Principal.UserId(principal);
        if (viewerId is null) return Outcome.Unauthorized();

        var row = await ReadableAsync(id, viewerId.Value, principal.IsInRole(Roles.Admin), ct);
        if (row?.BuildLogKey is null) return Outcome.NotFound();

        var url = await storage.DownloadUrlAsync(
            row.BuildLogSetup, row.BuildLogKey, CheckpointBuilds.LogFileName(row.Order + 1, row.CommitSha), "text/plain", ct);
        return Outcome.Redirect(url);
    }

    private sealed record Readable(
        PreviewBuildStatus BuildStatus, int Order, string? CommitSha, DateTimeOffset? BuildStartedAtUtc, DateTimeOffset? BuildFinishedAtUtc,
        string? BuildError, string? BuildLogTail, string? BuildLogKey, string? BuildLogSetup);

    /// <summary>The build, when the viewer may read it — the same null for one that is not theirs as for one that is missing.</summary>
    private async Task<Readable?> ReadableAsync(Guid id, Guid viewerId, bool isAdmin, CancellationToken ct)
    {
        var row = await db.Checkpoints.AsNoTracking()
            .Where(cp => cp.Id == id)
            .Select(cp => new
            {
                cp.BuildStatus, cp.CommitSha, cp.BuildStartedAtUtc, cp.BuildFinishedAtUtc,
                cp.BuildError, cp.BuildLogTail, cp.BuildLogKey, cp.BuildLogSetup,
                Order = cp.Milestone!.Order,
                cp.Entry!.FreelancerId,
                cp.Entry.Opportunity!.ClientId,
            })
            .SingleOrDefaultAsync(ct);
        return row is not null && CheckpointBuilds.CanSee(row.FreelancerId == viewerId, row.ClientId == viewerId, isAdmin)
            ? new Readable(row.BuildStatus, row.Order, row.CommitSha, row.BuildStartedAtUtc, row.BuildFinishedAtUtc,
                row.BuildError, row.BuildLogTail, row.BuildLogKey, row.BuildLogSetup)
            : null;
    }
}
