using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WinnersPortal.Api.Common;

namespace WinnersPortal.Api.Reports;

public sealed partial class ReportController : ControllerBase
{
    [HttpGet("api/reports/applications")]
    [Authorize]
    public async Task<IResult> GetApplications(CancellationToken ct) =>
        (await reports.ApplicationsAsync(User, ct)).ToResult();
}
