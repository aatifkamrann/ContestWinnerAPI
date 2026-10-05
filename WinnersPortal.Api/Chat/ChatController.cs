using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WinnersPortal.Api.Common;
using WinnersPortal.Services.Chat;

namespace WinnersPortal.Api.Chat;

/// <summary>The HTTP edge of <see cref="ChatService"/>: binds the request, answers with its outcome.</summary>
/// <remarks>
/// A conversation is keyed by the entry it belongs to. Every route is
/// signed-in only and the service answers "not found" to anyone but the
/// entry's freelancer and the opportunity's client — an administrator
/// included, who reads conversations read-only through
/// <see cref="ChatModerationController"/> — so the routes carry no role
/// policy of their own.
/// </remarks>
[ApiController]
public sealed class ChatController(ChatService chat) : ControllerBase
{
    /// <summary>Every conversation the caller is party to, newest first, with the unread count in all.</summary>
    [HttpGet("api/chat/threads")]
    [Authorize]
    public async Task<IResult> GetThreads(CancellationToken ct) =>
        (await chat.ThreadsAsync(User, ct)).ToResult();

    /// <summary>One conversation, a page of lines at a time, newest first.</summary>
    [HttpGet("api/chat/threads/{id:guid}")]
    [Authorize]
    public async Task<IResult> GetThread(Guid id, [FromQuery] string? cursor, [FromQuery] int? take, CancellationToken ct) =>
        (await chat.ThreadAsync(id, cursor, take, User, ct)).ToResult();

    [HttpPost("api/chat/threads/{id:guid}")]
    [Authorize]
    public async Task<IResult> PostThread(Guid id, SendMessageRequest request, CancellationToken ct) =>
        (await chat.SendAsync(id, request, User, ct)).ToResult();

    /// <summary>The other side's lines marked read — what opening the conversation does.</summary>
    [HttpPut("api/chat/threads/{id:guid}/read")]
    [Authorize]
    public async Task<IResult> PutThreadRead(Guid id, CancellationToken ct) =>
        (await chat.ReadAsync(id, User, ct)).ToResult();

    /// <summary>The conversation reported by one of its two parties; the other is not told.</summary>
    [HttpPost("api/chat/threads/{id:guid}/report")]
    [Authorize]
    public async Task<IResult> PostThreadReport(Guid id, ReportConversationRequest request, CancellationToken ct) =>
        (await chat.ReportAsync(id, request, User, ct)).ToResult();
}
