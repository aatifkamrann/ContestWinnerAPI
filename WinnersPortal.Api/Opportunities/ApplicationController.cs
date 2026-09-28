using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WinnersPortal.Api.Common;
using WinnersPortal.Services.Opportunities;

namespace WinnersPortal.Api.Opportunities;

/// <summary>The HTTP edge of <see cref="ApplicationService"/>: binds the request, answers with its outcome.</summary>
[ApiController]
public sealed class ApplicationController(ApplicationService applications) : ControllerBase
{
    // ------------------------------------------- what the wizard reads
    [HttpGet("api/opportunities/{slug}/apply")]
    [Authorize(Policy = "freelancer")]
    public async Task<IResult> GetOpportunitiesApply(string slug, CancellationToken ct) =>
        (await applications.ApplyFormAsync(slug, User, ct)).ToResult();

    // ---------------------------------------------------- submitting
    [HttpPost("api/opportunities/{slug}/applications")]
    [Authorize(Policy = "freelancer")]
    public async Task<IResult> PostOpportunitiesApplications(string slug, ApplyRequest request, CancellationToken ct) =>
        (await applications.ApplyAsync(slug, request, User, ct)).ToResult();

    // ------------------------------------------ the applicant's own list
    // My Opportunities: every application they filed, whatever became of it,
    // with the opportunity in the figures an entry row carries and their fit
    // read now, so the ring on the row is the one the browse card shows.
    // The waiting ones first, then the newest.
    [HttpGet("api/applications/mine")]
    [Authorize(Policy = "freelancer")]
    public async Task<IResult> GetApplicationsMine(CancellationToken ct) =>
        (await applications.MineAsync(User, ct)).ToResult();

    // ------------------------------------------- one application, read
    // The applicant's own page after submitting, polled while the
    // model's words are still coming; the client and an administrator
    // may read it too.
    [HttpGet("api/applications/{id:guid}")]
    [Authorize]
    public async Task<IResult> GetApplications(Guid id, CancellationToken ct) =>
        (await applications.ReadAsync(id, User, ct)).ToResult();

    // ---------------------------------------------------- the decision
    [HttpPost("api/applications/{id:guid}/decide")]
    [Authorize]
    public async Task<IResult> PostApplicationsDecide(Guid id, DecideRequest request, CancellationToken ct) =>
        (await applications.DecideAsync(id, request, User, ct)).ToResult();

    // ------------------------------------ the approach, drafted inline
    // "Generate approach": the model reads the brief and the applicant's
    // own profile and drafts a plan they edit before submitting. Answered
    // in the request, like the summary drafter — somebody is waiting with
    // their hand on the field.
    [HttpPost("api/opportunities/{slug}/ai/approach")]
    [Authorize(Policy = "freelancer")]
    public async Task<IResult> PostOpportunitiesAiApproach(string slug, ApproachDraftRequest request, CancellationToken ct) =>
        (await applications.DraftApproachAsync(slug, request, User, ct)).ToResult();
}
