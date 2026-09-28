using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WinnersPortal.Api.Common;
using WinnersPortal.Services.Opportunities;

namespace WinnersPortal.Api.Opportunities;

/// <summary>The HTTP edge of <see cref="EntryService"/>: binds the request, answers with its outcome.</summary>
[ApiController]
public sealed partial class EntryController(EntryService entries) : ControllerBase
{
    // Entering is no longer a POST of its own: a freelancer applies
    // (ApplicationController) and the client's selection makes the
    // entry, through JoinAsync below — the same door, one step later.
    // --------------------------------------------------------- withdraw
    [HttpPost("api/entries/{id:guid}/withdraw")]
    [Authorize(Policy = "freelancer")]
    public async Task<IResult> PostEntriesWithdraw(Guid id, WithdrawEntryRequest? request, CancellationToken ct) =>
        (await entries.WithdrawAsync(id, request, User, ct)).ToResult();

    // --------------------------------------------------------- removal
    // The harshest thing one person here does to another: somebody staked
    // days of work on an open-entry promise and is being told it does not
    // count. Hence the reason they receive word for word, the window to
    // clone what they built, and the rules in Removal that say when it
    // may happen at all.
    [HttpPost("api/entries/{id:guid}/remove")]
    [Authorize]
    public async Task<IResult> PostEntriesRemove(Guid id, RemoveEntryRequest request, CancellationToken ct) =>
        (await entries.RemoveAsync(id, request, User, ct)).ToResult();

    // ------------------------------------------ my opportunities (entries) list
    [HttpGet("api/entries/mine")]
    [Authorize(Policy = "freelancer")]
    public async Task<IResult> GetEntriesMine(CancellationToken ct) =>
        (await entries.MineAsync(User, ct)).ToResult();
}
