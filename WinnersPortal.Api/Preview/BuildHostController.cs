using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WinnersPortal.Api.Common;
using WinnersPortal.Services.Preview;

namespace WinnersPortal.Api.Preview;

/// <summary>The HTTP edge of <see cref="BuildHostService"/>: binds the request, answers with its outcome.</summary>
[ApiController]
public sealed class BuildHostController(BuildHostService buildHost) : ControllerBase
{
    // Whether the opportunity form may offer Docker Compose at all — the
    // option must not render while builds are off.
    [HttpGet("api/build-host/status")]
    [Authorize]
    public async Task<IResult> GetBuildHostStatus(CancellationToken ct) =>
        (await buildHost.StatusAsync(ct)).ToResult();
}
