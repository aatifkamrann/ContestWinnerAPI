using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WinnersPortal.Api.Common;
using WinnersPortal.Services.Storage;

namespace WinnersPortal.Api.Storage;

/// <summary>The HTTP edge of <see cref="EntryZipService"/>: binds the request, answers with its outcome.</summary>
[ApiController]
public sealed class EntryZipController(EntryZipService zips) : ControllerBase
{
    [HttpPost("api/entries/{id:guid}/zip")]
    [Authorize]
    public async Task<IResult> PostEntriesZip(Guid id, CancellationToken ct) =>
        (await zips.PackageAsync(id, User, ct)).ToResult();
}
