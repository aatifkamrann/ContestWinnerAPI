using Microsoft.AspNetCore.Mvc;
using WinnersPortal.Api.Common;
using WinnersPortal.Services.Leaderboard;

namespace WinnersPortal.Api.Leaderboard;

/// <summary>The HTTP edge of <see cref="TalentService"/>: binds the request, answers with its outcome.</summary>
[ApiController]
public sealed class TalentController(TalentService talent) : ControllerBase
{
    [HttpGet("api/public/talent")]
    public async Task<IResult> GetPublicTalent(string? category, string? region, string? merit, string? availability, string? experience, string? wins, CancellationToken ct) =>
        (await talent.ReadAsync(category, region, merit, availability, experience, wins, ct)).ToResult();
}
