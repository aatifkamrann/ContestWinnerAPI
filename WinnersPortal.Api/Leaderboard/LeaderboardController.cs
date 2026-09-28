using Microsoft.AspNetCore.Mvc;
using WinnersPortal.Api.Common;
using WinnersPortal.Services.Leaderboard;

namespace WinnersPortal.Api.Leaderboard;

/// <summary>The HTTP edge of <see cref="LeaderboardService"/>: binds the request, answers with its outcome.</summary>
[ApiController]
public sealed class LeaderboardController(LeaderboardService leaderboard) : ControllerBase
{
    [HttpGet("api/public/leaderboard")]
    public async Task<IResult> GetPublicLeaderboard(string? tab, string? by, string? region, string? category, CancellationToken ct) =>
        (await leaderboard.ReadAsync(tab, by, region, category, ct)).ToResult();
}
