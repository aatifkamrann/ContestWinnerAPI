using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WinnersPortal.Api.Common;
using WinnersPortal.Services.Notifications;

namespace WinnersPortal.Api.Notifications;

/// <summary>The HTTP edge of <see cref="NotificationService"/>: binds the request, answers with its outcome.</summary>
[ApiController]
public sealed class NotificationController(NotificationService notifications) : ControllerBase
{
    // ------------------------------------------------------------ inbox
    // The bell: a page of the caller's lines with the unread count, a line
    // marked read or unread, and the whole inbox marked either way.
    [HttpGet("api/notifications/inbox")]
    [Authorize]
    public async Task<IResult> GetInbox([FromQuery] string? cursor, [FromQuery] int? take, CancellationToken ct) =>
        (await notifications.InboxAsync(cursor, take, User, ct)).ToResult();

    [HttpPut("api/notifications/inbox/{id:guid}")]
    [Authorize]
    public async Task<IResult> PutInboxLine(Guid id, InboxMarkRequest request, CancellationToken ct) =>
        (await notifications.MarkAsync(id, request.Read, User, ct)).ToResult();

    [HttpPut("api/notifications/inbox")]
    [Authorize]
    public async Task<IResult> PutInbox(InboxMarkRequest request, CancellationToken ct) =>
        (await notifications.MarkAllAsync(request.Read, User, ct)).ToResult();

    // ------------------------------------------------------ preferences
    [HttpGet("api/notifications")]
    [Authorize]
    public async Task<IResult> GetNotifications(CancellationToken ct) =>
        (await notifications.PreferencesAsync(User, ct)).ToResult();

    [HttpPut("api/notifications")]
    [Authorize]
    public async Task<IResult> PutNotifications(NotificationPreferencesRequest request, CancellationToken ct) =>
        (await notifications.SavePreferencesAsync(request, User, ct)).ToResult();

    // ---------------------------------------------------------- devices
    // A browser registers the subscription its push service gave it.
    // The endpoint is the identity: the same browser registering again
    // refreshes its row, and a browser that changed accounts moves its
    // row, because the push service will deliver to whoever holds the
    // browser now regardless of what the portal remembers.
    [HttpPost("api/notifications/devices")]
    [Authorize]
    public async Task<IResult> PostDevices(RegisterDeviceRequest request, CancellationToken ct) =>
        (await notifications.RegisterDeviceAsync(request, User, ct)).ToResult();

    [HttpDelete("api/notifications/devices/{id:guid}")]
    [Authorize]
    public async Task<IResult> DeleteDevices(Guid id, CancellationToken ct) =>
        (await notifications.RemoveDeviceAsync(id, User, ct)).ToResult();

    // ------------------------------------------------------ unsubscribe
    // Anonymous: the token is the proof, and the person clicking it may
    // well be signed out on this device. It stops email and nothing
    // else — push on a device they enabled is their choice on that
    // device, and the page they land on says where to change it.
    [HttpPost("api/notifications/unsubscribe")]
    public async Task<IResult> PostUnsubscribe(UnsubscribeRequest request, CancellationToken ct) =>
        (await notifications.UnsubscribeAsync(request, ct)).ToResult();
}
