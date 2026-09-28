using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WinnersPortal.Api.Common;
using WinnersPortal.Services.Reports;

namespace WinnersPortal.Api.Reports;

/// <summary>The HTTP edge of <see cref="ReportService"/>: binds the request, answers with its outcome.</summary>
[ApiController]
public sealed partial class ReportController(ReportService reports) : ControllerBase
{
    [HttpGet("api/reports/opportunities")]
    [Authorize]
    public async Task<IResult> GetOpportunities(CancellationToken ct) =>
        (await reports.OpportunitiesAsync(User, ct)).ToResult();
}
