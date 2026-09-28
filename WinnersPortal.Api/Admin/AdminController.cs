using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WinnersPortal.Api.Common;
using WinnersPortal.Services.Admin;

namespace WinnersPortal.Api.Admin;

/// <summary>The HTTP edge of <see cref="AdminService"/>: binds the request, answers with its outcome.</summary>
[ApiController]
public sealed class AdminController(AdminService admin) : ControllerBase
{
    // --------------------------------------------------------- overview
    [HttpGet("api/admin/operations")]
    [Authorize(Policy = "admin")]
    public async Task<IResult> GetOperations(CancellationToken ct) =>
        (await admin.OperationsAsync(ct)).ToResult();

    // ---------------------------------------------------- retry provision
    [HttpPost("api/admin/entries/{id:guid}/retry-provision")]
    [Authorize(Policy = "admin")]
    public async Task<IResult> PostEntriesRetryProvision(Guid id, CancellationToken ct) =>
        (await admin.RetryProvisionAsync(id, ct)).ToResult();

    // --------------------------------------------------- restart handover
    [HttpPost("api/admin/awards/{id:guid}/restart-handover")]
    [Authorize(Policy = "admin")]
    public async Task<IResult> PostAwardsRestartHandover(Guid id, CancellationToken ct) =>
        (await admin.RestartHandoverAsync(id, ct)).ToResult();

    // ---------------------------------------------- clear slow queries
    [HttpPost("api/admin/slow-queries/clear")]
    [Authorize(Policy = "admin")]
    public IResult PostSlowQueriesClear() => admin.ClearSlowQueries().ToResult();

    // ---------------------------------------------------- replay delivery
    [HttpPost("api/admin/deliveries/{id:guid}/replay")]
    [Authorize(Policy = "admin")]
    public async Task<IResult> PostDeliveriesReplay(Guid id, CancellationToken ct) =>
        (await admin.ReplayDeliveryAsync(id, ct)).ToResult();
}
