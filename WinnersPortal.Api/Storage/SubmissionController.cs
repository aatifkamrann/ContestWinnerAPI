using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WinnersPortal.Api.Common;
using WinnersPortal.Services.Storage;

namespace WinnersPortal.Api.Storage;

/// <summary>The HTTP edge of <see cref="SubmissionService"/>: binds the request, answers with its outcome.</summary>
[ApiController]
public sealed class SubmissionController(SubmissionService submissions) : ControllerBase
{
    // ------------------------------------------------------- reserve
    [HttpPost("api/entries/{id:guid}/submissions")]
    [Authorize(Policy = "freelancer")]
    public async Task<IResult> PostEntriesSubmissions(Guid id, ReserveSubmissionRequest request, CancellationToken ct) =>
        (await submissions.ReserveAsync(id, request, User, ct)).ToResult();

    // ------------------------------------------------------- the bytes
    // The file itself is the body, to the slot the reserve made. The
    // portal's own limit applies (limits.maxUploadMb, read by the
    // service), not Kestrel's 30 MB default.
    [HttpPut("api/submissions/{id:guid}/content")]
    [Authorize(Policy = "freelancer")]
    [DisableRequestSizeLimit]
    public async Task<IResult> PutSubmissionsContent(Guid id, CancellationToken ct) =>
        (await submissions.UploadAsync(id, Request.Body, Request.ContentLength, User, ct)).ToResult();

    // -------------------------------------------------------- delete
    [HttpDelete("api/submissions/{id:guid}")]
    [Authorize(Policy = "freelancer")]
    public async Task<IResult> DeleteSubmissions(Guid id, CancellationToken ct) =>
        (await submissions.DeleteAsync(id, User, ct)).ToResult();

    // ---------------------------------------------------------- list
    [HttpGet("api/entries/{id:guid}/submissions")]
    [Authorize]
    public async Task<IResult> GetEntriesSubmissions(Guid id, CancellationToken ct) =>
        (await submissions.ListAsync(id, User, ct)).ToResult();

    // ------------------------------------------------------ download
    // A plain link, because auth rides the cookie: the API checks who is
    // asking and answers with a redirect to a five-minute signed URL,
    // signed with a forced-download disposition — opened directly, a
    // download rather than a page.
    [HttpGet("api/submissions/{id:guid}/download")]
    public async Task<IResult> GetSubmissionsDownload(Guid id, CancellationToken ct) =>
        (await submissions.DownloadAsync(id, User, ct)).ToBrowserResult(Request);

    // ---------------------------------------------------------- view
    // The previews' source, on the download's own delivery rule — the
    // link a brief attachment's preview uses, for an entrant's file:
    // signed for the type the preview names, never the one the entrant
    // declared, and inline only for a PDF. A file with no preview gets
    // its download.
    [HttpGet("api/submissions/{id:guid}/view")]
    public async Task<IResult> GetSubmissionsView(Guid id, CancellationToken ct) =>
        (await submissions.ViewAsync(id, User, ct)).ToBrowserResult(Request);

    // A text preview's contents as plain text, for the page to read itself — see AttachmentController.
    [HttpGet("api/submissions/{id:guid}/text")]
    public async Task<IResult> GetSubmissionsText(Guid id, CancellationToken ct)
    {
        HttpContext.Response.Headers.XContentTypeOptions = "nosniff";
        return (await submissions.TextAsync(id, User, ct)).ToResult();
    }
}
