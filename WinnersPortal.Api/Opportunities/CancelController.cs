using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WinnersPortal.Api.Common;
using WinnersPortal.Services.Opportunities;

namespace WinnersPortal.Api.Opportunities;

/// <summary>The HTTP edge of <see cref="CancelService"/>: binds the request, answers with its outcome.</summary>
[ApiController]
public sealed class CancelController(CancelService cancellations) : ControllerBase
{
    [HttpPost("api/opportunities/{id:guid}/cancel")]
    [Authorize(Policy = "client")]
    public async Task<IResult> PostOpportunitiesCancel(Guid id, CancelRequest request, CancellationToken ct) =>
        (await cancellations.CancelOpportunityAsync(id, request, User, ct)).ToResult();
}
