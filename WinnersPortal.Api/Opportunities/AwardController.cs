using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WinnersPortal.Api.Common;
using WinnersPortal.Services.Opportunities;

namespace WinnersPortal.Api.Opportunities;

/// <summary>The HTTP edge of <see cref="AwardService"/>: binds the request, answers with its outcome.</summary>
[ApiController]
public sealed class AwardController(AwardService awards) : ControllerBase
{
    // --------------------------------------------------------- announce
    [HttpPost("api/opportunities/{id:guid}/award")]
    [Authorize(Policy = "client")]
    public async Task<IResult> PostOpportunitiesAward(Guid id, AnnounceRequest request, CancellationToken ct) =>
        (await awards.AnnounceAsync(id, request, User, ct)).ToResult();

    // ------------------------------------------------------- mark paid
    [HttpPost("api/awards/{id:guid}/paid")]
    [Authorize(Policy = "client")]
    public async Task<IResult> PostAwardsPaid(Guid id, CancellationToken ct) =>
        (await awards.MarkPaidAsync(id, User, ct)).ToResult();
}
