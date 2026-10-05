using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WinnersPortal.Api.Common;
using WinnersPortal.Services.Opportunities;

namespace WinnersPortal.Api.Opportunities;

/// <summary>The HTTP edge of <see cref="MilestonePaymentService"/>: binds the request, answers with its outcome.</summary>
[ApiController]
public sealed class MilestonePaymentController(MilestonePaymentService payments) : ControllerBase
{
    // The client's three answers to a milestone handed in.
    [HttpPost("api/checkpoints/{id:guid}/approve")]
    [Authorize(Policy = "client")]
    public async Task<IResult> PostCheckpointsApprove(Guid id, CancellationToken ct) =>
        (await payments.ApproveAsync(id, User, ct)).ToResult();

    [HttpPost("api/checkpoints/{id:guid}/changes")]
    [Authorize(Policy = "client")]
    public async Task<IResult> PostCheckpointsChanges(Guid id, MilestoneChangesRequest request, CancellationToken ct) =>
        (await payments.RequestChangesAsync(id, request, User, ct)).ToResult();

    [HttpPost("api/checkpoints/{id:guid}/paid")]
    [Authorize(Policy = "client")]
    public async Task<IResult> PostCheckpointsPaid(Guid id, CancellationToken ct) =>
        (await payments.MarkPaidAsync(id, User, ct)).ToResult();

    // The freelancer's: a milestone sent back, handed in again.
    [HttpPost("api/checkpoints/{id:guid}/resubmit")]
    [Authorize(Policy = "freelancer")]
    public async Task<IResult> PostCheckpointsResubmit(Guid id, CancellationToken ct) =>
        (await payments.ResubmitAsync(id, User, ct)).ToResult();
}
