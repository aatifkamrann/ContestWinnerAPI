using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WinnersPortal.Api.Common;
using WinnersPortal.Services.Dashboard;

namespace WinnersPortal.Api.Dashboard;

/// <summary>The HTTP edge of <see cref="DashboardService"/>: binds the request, answers with its outcome.</summary>
[ApiController]
public sealed class DashboardController(DashboardService dashboards) : ControllerBase
{
    [HttpGet("api/dashboard")]
    [Authorize]
    public async Task<IResult> GetDashboard(string? tz, CancellationToken ct) =>
        (await dashboards.ReadAsync(tz, User, ct)).ToResult();
}
