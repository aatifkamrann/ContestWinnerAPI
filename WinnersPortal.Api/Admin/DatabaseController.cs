using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WinnersPortal.Api.Common;
using WinnersPortal.Services.Auth;
using WinnersPortal.Services.Database;

namespace WinnersPortal.Api.Admin;

/// <summary>The HTTP edge of <see cref="DatabaseAdminService"/>: binds the request, answers with its outcome.</summary>
[ApiController]
public sealed class DatabaseController(DatabaseAdminService database) : ControllerBase
{
    // Where the portal is, and where a move stands.
    [HttpGet("api/admin/database")]
    [Authorize(Policy = "admin")]
    public async Task<IResult> GetDatabase(CancellationToken ct) =>
        (await database.StatusAsync(ct)).ToResult();

    // A candidate, in four facts — the body carries a password over HTTPS
    // and is never echoed.
    [HttpPost("api/admin/database/test")]
    [Authorize(Policy = "admin")]
    public async Task<IResult> PostDatabaseTest(DatabaseConnectionFields request, CancellationToken ct) =>
        (await database.TestAsync(request, ct)).ToResult();

    // Starts the move; 202 with the state to poll, 409 while one runs.
    [HttpPost("api/admin/database/move")]
    [Authorize(Policy = "admin")]
    public IResult PostDatabaseMove(DatabaseConnectionFields request)
    {
        var outcome = database.Move(request, Principal.Email(User) ?? "an administrator");
        return outcome.Kind == WinnersPortal.Services.Common.OutcomeKind.Ok
            ? Results.Accepted("/api/admin/database/move", outcome.Body)
            : outcome.ToResult();
    }

    // Saves an edited connection onto this portal's own data — a new
    // password, a new host, a restored copy — and restarts on it; 202 with
    // the state to poll, 409 when the database is not this portal's.
    [HttpPost("api/admin/database/connection")]
    [Authorize(Policy = "admin")]
    public async Task<IResult> PostDatabaseConnection(DatabaseConnectionFields request, CancellationToken ct)
    {
        var outcome = await database.ChangeAsync(request, Principal.Email(User) ?? "an administrator", ct);
        return outcome.Kind == WinnersPortal.Services.Common.OutcomeKind.Ok
            ? Results.Accepted("/api/admin/database/move", outcome.Body)
            : outcome.ToResult();
    }

    [HttpGet("api/admin/database/move")]
    [Authorize(Policy = "admin")]
    public IResult GetDatabaseMove() => database.Progress().ToResult();
}
