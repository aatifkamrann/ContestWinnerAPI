using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WinnersPortal.Api.Common;
using WinnersPortal.Services.Preview;

namespace WinnersPortal.Api.Preview;

/// <summary>The HTTP edge of <see cref="CheckpointBuildService"/>: binds the request, answers with its outcome.</summary>
[ApiController]
public sealed class CheckpointBuildController(CheckpointBuildService builds) : ControllerBase
{
    // ---------------------------------------------------------- read
    [HttpGet("api/checkpoints/{id:guid}/build")]
    [Authorize]
    public async Task<IResult> GetCheckpointsBuild(Guid id, CancellationToken ct) =>
        (await builds.ReadAsync(id, User, ct)).ToResult();

    // ------------------------------------------------------ download
    // A plain link, because auth rides the cookie — opened directly, a
    // download of the whole log rather than a page.
    [HttpGet("api/checkpoints/{id:guid}/build-log")]
    public async Task<IResult> GetCheckpointsBuildLog(Guid id, CancellationToken ct) =>
        (await builds.DownloadLogAsync(id, User, ct)).ToBrowserResult(Request);
}
