using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WinnersPortal.Api.Common;
using WinnersPortal.Services.Opportunities;

namespace WinnersPortal.Api.Opportunities;

/// <summary>The HTTP edge of <see cref="ClaimReviewService"/>: binds the request, answers with its outcome.</summary>
[ApiController]
public sealed class ClaimReviewController(ClaimReviewService reviews) : ControllerBase
{
    // A milestone claimed on a competitive opportunity, reviewed: the
    // client's, or an administrator's on their behalf. The service tells the
    // two apart and answers anyone else with not found.
    [HttpPost("api/checkpoints/{id:guid}/review")]
    [Authorize]
    public async Task<IResult> PostCheckpointsReview(Guid id, ClaimReviewRequest request, CancellationToken ct) =>
        (await reviews.ReviewAsync(id, request, User, ct)).ToResult();
}
