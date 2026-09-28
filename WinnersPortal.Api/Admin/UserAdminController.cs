using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WinnersPortal.Api.Auth;
using WinnersPortal.Api.Common;
using WinnersPortal.Services.Admin;
using WinnersPortal.Services.Auth;
using WinnersPortal.Services.Common;
using WinnersPortal.Services.Identity;

namespace WinnersPortal.Api.Admin;

/// <summary>The HTTP edge of <see cref="UserAdminService"/>: binds the request, answers with its outcome.</summary>
[ApiController]
public sealed class UserAdminController(UserAdminService users) : ControllerBase
{
    // ------------------------------------------------------------ list
    [HttpGet("api/admin/users")]
    [Authorize(Policy = "admin")]
    public async Task<IResult> GetUsers(CancellationToken ct) =>
        (await users.ListAsync(User, ct)).ToResult();

    // ---------------------------------------------------------- create
    // The one account an administrator makes rather than corrects, and
    // the only door administrators come through after the setup wizard.
    //
    // Two ways in, the same two the password section below offers: a
    // password chosen here and handed over out of band, or an invitation
    // to the address given. There is no third, because an account nobody
    // can sign into is a row pretending to be a person. Note which way
    // is which — with an invitation the administrator never sees the
    // link, so an account made this way is the person's from the start.
    [HttpPost("api/admin/users")]
    [Authorize(Policy = "admin")]
    public async Task<IResult> PostUsers(CreateUserRequest request, CancellationToken ct) =>
        (await users.CreateAsync(request, User, ct)).ToResult();

    // --------------------------------------------------------- profile
    [HttpPut("api/admin/users/{id:guid}")]
    [Authorize(Policy = "admin")]
    public async Task<IResult> PutUsers(Guid id, UpdateUserRequest request, CancellationToken ct) =>
        (await users.UpdateAsync(id, request, User, ct)).ToResult();

    // ------------------------------------------------------------ lock
    [HttpPost("api/admin/users/{id:guid}/lock")]
    [Authorize(Policy = "admin")]
    public async Task<IResult> PostLock(Guid id, LockUserRequest request, CancellationToken ct) =>
        (await users.LockAsync(id, request, User, ct)).ToResult();

    [HttpPost("api/admin/users/{id:guid}/unlock")]
    [Authorize(Policy = "admin")]
    public async Task<IResult> PostUnlock(Guid id, CancellationToken ct) =>
        (await users.UnlockAsync(id, User, ct)).ToResult();

    // ---------------------------------------------- identity verification
    // Takes a verdict back — a document that turned out to be somebody
    // else's, a member who asks to start over. The doors shut again until
    // a new verification passes.
    [HttpPost("api/admin/users/{id:guid}/reset-verification")]
    [Authorize(Policy = "admin")]
    public async Task<IResult> PostResetVerification(Guid id, [FromServices] IdentityService identity, CancellationToken ct)
    {
        var outcome = await identity.ResetAsync(id, ct);
        return outcome.Kind == OutcomeKind.NoContent
            ? (await users.RowAsync(id, User, ct)).ToResult()
            : outcome.ToResult();
    }

    // The proof behind a verdict: what the provider read off the document,
    // its whole decision, and the stored copies of the images. 404 for a
    // member who never started a verification.
    [HttpGet("api/admin/users/{id:guid}/identity")]
    [Authorize(Policy = "admin")]
    public async Task<IResult> GetIdentityProof(Guid id, [FromServices] IdentityProofService proof, CancellationToken ct) =>
        (await proof.RecordAsync(id, ct)).ToResult();

    [HttpPost("api/admin/users/{id:guid}/identity/fetch")]
    [Authorize(Policy = "admin")]
    public async Task<IResult> PostIdentityProofFetch(Guid id, [FromServices] IdentityProofService proof, CancellationToken ct) =>
        (await proof.FetchAgainAsync(id, ct)).ToResult();

    // One stored document image, through a short-lived signed link. Every
    // opening is an activity row, so who looked at whose document is known.
    [HttpGet("api/admin/identity-documents/{id:guid}/view")]
    [Authorize(Policy = "admin")]
    public async Task<IResult> GetIdentityDocument(Guid id, [FromServices] IdentityProofService proof, CancellationToken ct) =>
        (await proof.ViewAsync(id, ct)).ToBrowserResult(Request);

    // -------------------------------------------------------- password
    // Two ways back in. Setting one directly is for the portal without
    // email, or the person who cannot reach their inbox: the
    // administrator hands it over out of band. The link is the same
    // one the forgot form sends, minus the throttle — an administrator
    // is not a stranger flooding an inbox.
    [HttpPost("api/admin/users/{id:guid}/password")]
    [Authorize(Policy = "admin")]
    public async Task<IResult> PostPassword(Guid id, SetPasswordRequest request, [FromServices] TokenService tokens, CancellationToken ct)
    {
        var outcome = await users.SetPasswordAsync(id, request, User, ct);
        SessionCookie.Apply(HttpContext, tokens, outcome);
        return outcome.ToResult();
    }

    [HttpPost("api/admin/users/{id:guid}/reset-link")]
    [Authorize(Policy = "admin")]
    public async Task<IResult> PostResetLink(Guid id, CancellationToken ct) =>
        (await users.SendResetLinkAsync(id, ct)).ToResult();

    // ---------------------------------------------------------- delete
    [HttpDelete("api/admin/users/{id:guid}")]
    [Authorize(Policy = "admin")]
    public async Task<IResult> DeleteUsers(Guid id, CancellationToken ct) =>
        (await users.EraseAsync(id, User, ct)).ToResult();
}
