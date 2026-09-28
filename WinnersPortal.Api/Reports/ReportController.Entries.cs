using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WinnersPortal.Api.Common;

namespace WinnersPortal.Api.Reports;

public sealed partial class ReportController : ControllerBase
{
    [HttpGet("api/reports/entries")]
    [Authorize]
    public async Task<IResult> GetEntries(CancellationToken ct) =>
        (await reports.EntriesAsync(User, ct)).ToResult();
}
