using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WinnersPortal.Api.Common;
using WinnersPortal.Services.Preview;

namespace WinnersPortal.Api.Preview;

/// <summary>
/// The HTTP edge of <see cref="PreviewService"/>. A preview's id is its
/// checkpoint's (a milestone) or its entry's (the final), so one route
/// serves both.
/// </summary>
[ApiController]
public sealed class PreviewController(PreviewService previews) : ControllerBase
{
    [HttpGet("api/previews/{id:guid}")]
    [Authorize]
    public async Task<IResult> GetPreview(Guid id, CancellationToken ct) =>
        (await previews.ReadAsync(id, User, ct)).ToResult();

    [HttpPost("api/previews/{id:guid}")]
    [Authorize]
    public async Task<IResult> PostPreview(Guid id, CancellationToken ct) =>
        (await previews.StartAsync(id, User, ct)).ToResult();

    [HttpDelete("api/previews/{id:guid}")]
    [Authorize]
    public async Task<IResult> DeletePreview(Guid id, CancellationToken ct) =>
        (await previews.StopAsync(id, User, ct)).ToResult();

    // A plain link, because auth rides the cookie — opened in a new tab, it
    // lands on the preview's own address with a one-time ticket.
    [HttpGet("api/previews/{id:guid}/open")]
    public async Task<IResult> GetPreviewOpen(Guid id, CancellationToken ct) =>
        (await previews.OpenAsync(id, User, ct)).ToBrowserResult(Request);
}
