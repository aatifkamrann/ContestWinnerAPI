using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WinnersPortal.Api.Common;
using WinnersPortal.Services.Chat;

namespace WinnersPortal.Api.Chat;

/// <summary>The HTTP edge of <see cref="ChatModerationService"/>: binds the request, answers with its outcome.</summary>
/// <remarks>
/// Administrators only, and reading only but for marking a member's report
/// reviewed. The list is a read like any other and leaves no row; opening a
/// conversation does — it is named in the activity log, so every reading
/// of what two members said to each other can be accounted for.
/// </remarks>
[ApiController]
public sealed class ChatModerationController(ChatModerationService moderation) : ControllerBase
{
    /// <summary>Every conversation with something said in it, newest line first; one member's, matching the words, or reported.</summary>
    [HttpGet("api/admin/conversations")]
    [Authorize(Policy = "admin")]
    public async Task<IResult> GetConversations(
        [FromQuery] string? q, [FromQuery] Guid? member, [FromQuery] bool? reported, [FromQuery] string? cursor, [FromQuery] int? take,
        CancellationToken ct) =>
        (await moderation.ListAsync(q, member, reported == true, cursor, take, ct)).ToResult();

    /// <summary>One conversation, a page of lines at a time, newest first. Marks nothing read.</summary>
    [HttpGet("api/admin/conversations/{id:guid}")]
    [Authorize(Policy = "admin")]
    public async Task<IResult> GetConversation(Guid id, [FromQuery] string? cursor, [FromQuery] int? take, CancellationToken ct) =>
        (await moderation.ReadAsync(id, cursor, take, ct)).ToResult();

    /// <summary>The conversation's open reports marked reviewed; each reporter is told it was, and nothing more.</summary>
    [HttpPost("api/admin/conversations/{id:guid}/reports/resolve")]
    [Authorize(Policy = "admin")]
    public async Task<IResult> PostResolveReports(Guid id, ResolveReportsRequest request, CancellationToken ct) =>
        (await moderation.ResolveAsync(id, request, User, ct)).ToResult();
}
