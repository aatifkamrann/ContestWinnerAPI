using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WinnersPortal.Api.Common;
using WinnersPortal.Services.Opportunities;

namespace WinnersPortal.Api.Opportunities;

/// <summary>The HTTP edge of <see cref="RatingService"/>: binds the request, answers with its outcome.</summary>
[ApiController]
public sealed class RatingController(RatingService ratings) : ControllerBase
{
    // PUT because a re-rate is an edit of the same resource, not a new one.
    [HttpPut("api/awards/{id:guid}/rating")]
    [Authorize]
    public async Task<IResult> PutAwardsRating(Guid id, RateRequest request, CancellationToken ct) =>
        (await ratings.RateAsync(id, request, User, ct)).ToResult();
}
