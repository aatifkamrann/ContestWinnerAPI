using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WinnersPortal.Api.Common;
using WinnersPortal.Services.Storage;

namespace WinnersPortal.Api.Storage;

/// <summary>The HTTP edge of <see cref="AttachmentService"/>: binds the request, answers with its outcome.</summary>
[ApiController]
public sealed class AttachmentController(AttachmentService attachments) : ControllerBase
{
    // ------------------------------------------------------- reserve
    [HttpPost("api/opportunities/{id:guid}/attachments")]
    [Authorize(Policy = "client")]
    public async Task<IResult> PostOpportunitiesAttachments(Guid id, ReserveAttachmentRequest request, CancellationToken ct) =>
        (await attachments.ReserveAsync(id, request, User, ct)).ToResult();

    // ------------------------------------------------------- the bytes
    // The file itself is the body, to the slot the reserve made. The
    // portal's own limit applies (limits.maxUploadMb, read by the
    // service), not Kestrel's 30 MB default.
    [HttpPut("api/attachments/{id:guid}/content")]
    [Authorize(Policy = "client")]
    [DisableRequestSizeLimit]
    public async Task<IResult> PutAttachmentsContent(Guid id, CancellationToken ct) =>
        (await attachments.UploadAsync(id, Request.Body, Request.ContentLength, User, ct)).ToResult();

    // -------------------------------------------------------- delete
    [HttpDelete("api/attachments/{id:guid}")]
    [Authorize(Policy = "client")]
    public async Task<IResult> DeleteAttachments(Guid id, CancellationToken ct) =>
        (await attachments.DeleteAsync(id, User, ct)).ToResult();

    // ------------------------------------------------- list (editor)
    [HttpGet("api/opportunities/{id:guid}/attachments")]
    [Authorize(Policy = "client")]
    public async Task<IResult> GetOpportunitiesAttachments(Guid id, CancellationToken ct) =>
        (await attachments.ListAsync(id, User, ct)).ToResult();

    // ------------------------------------------------------ download
    // A plain link, because auth rides the cookie: the API checks who is
    // asking and answers with a redirect to a five-minute signed URL —
    // the bytes come from the store, never through here.
    [HttpGet("api/attachments/{id:guid}/download")]
    public async Task<IResult> GetAttachmentsDownload(Guid id, CancellationToken ct) =>
        (await attachments.DownloadAsync(id, User, ct)).ToBrowserResult(Request);

    // ---------------------------------------------------------- view
    // The opportunity page's preview source, on the download's own check.
    // It is signed for the type the preview names rather than the one
    // the uploader declared, so what renders is only ever a picture, a
    // PDF, a player's media or text — and only the PDF opens inline. A
    // file with no preview gets its download.
    [HttpGet("api/attachments/{id:guid}/view")]
    public async Task<IResult> GetAttachmentsView(Guid id, CancellationToken ct) =>
        (await attachments.ViewAsync(id, User, ct)).ToBrowserResult(Request);

    // A text preview's contents, for the page to read itself: served from
    // here as plain text, so the page asks the API it already talks to and
    // no bucket has to answer a browser. nosniff keeps a browser that
    // opens the address directly from reading anything else into it.
    [HttpGet("api/attachments/{id:guid}/text")]
    public async Task<IResult> GetAttachmentsText(Guid id, CancellationToken ct)
    {
        HttpContext.Response.Headers.XContentTypeOptions = "nosniff";
        return (await attachments.TextAsync(id, User, ct)).ToResult();
    }
}
