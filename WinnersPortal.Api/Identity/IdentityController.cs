using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WinnersPortal.Api.Common;
using WinnersPortal.Services.Identity;

namespace WinnersPortal.Api.Identity;

/// <summary>The HTTP edge of <see cref="IdentityService"/>: binds the request, answers with its outcome.</summary>
[ApiController]
public sealed class IdentityController(IdentityService identity) : ControllerBase
{
    // Opens a session with the provider and answers where to send the
    // member; the browser goes there and comes back to /verify/done.
    [HttpPost("api/identity/session")]
    [Authorize]
    public async Task<IResult> PostSession(CancellationToken ct) =>
        (await identity.StartAsync(User, ct)).ToResult();

    [HttpGet("api/identity/status")]
    [Authorize]
    public async Task<IResult> GetStatus(CancellationToken ct) =>
        (await identity.StatusAsync(User, ct)).ToResult();

    // The verdict asked of the provider directly — the return page's
    // first call, for the member whose webhook has not landed yet.
    [HttpPost("api/identity/refresh")]
    [Authorize]
    public async Task<IResult> PostRefresh(CancellationToken ct) =>
        (await identity.RefreshAsync(User, ct)).ToResult();
}

/// <summary>The provider's webhook fan-in: raw bytes, because the signature is over them.</summary>
[ApiController]
public sealed class IdentityWebhookController(IdentityService identity) : ControllerBase
{
    [HttpPost("api/webhooks/identity")]
    public async Task<IResult> PostWebhooksIdentity(CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await Request.Body.CopyToAsync(buffer, ct);
        return (await identity.ReceiveAsync(
            buffer.ToArray(),
            Request.Headers["X-Signature"].ToString(),
            Request.Headers["X-Timestamp"].ToString(),
            ct)).ToResult();
    }
}
