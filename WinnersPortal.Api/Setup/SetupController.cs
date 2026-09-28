using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WinnersPortal.Api.Auth;
using WinnersPortal.Services.Setup;
using WinnersPortal.Services.Auth;
using WinnersPortal.Services.Common;
using WinnersPortal.Domain;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Services.Database;
using WinnersPortal.Services.Settings;

namespace WinnersPortal.Api.Setup;

[ApiController]
public sealed class SetupController : ControllerBase
{
    /// <summary>The keys the wizard shows; the defaults endpoint prefills exactly these.</summary>
    private static readonly string[] WizardKeys =
    [
        "branding.portalName",
        "branding.publicUrl",
        "branding.apiUrl",
        "branding.accentColor",
        "email.smtpHost",
        "email.smtpPort",
        "email.fromAddress",
        JwtSettings.IssuerKey,
        JwtSettings.AudienceKey,
        JwtSettings.AccessMinutesKey,
        JwtSettings.RefreshDaysKey,
    ];

    /// <summary>A key the wizard also shows but never prefills: a secret goes in, never back out.</summary>
    private static readonly string[] WizardSecretKeys = [JwtSettings.SigningKeyKey];

    [HttpGet("api/setup/status")]
    public async Task<IResult> GetStatus([FromServices] SettingsService settings, CancellationToken ct) =>
        Results.Ok(new SetupStatusResponse { Completed = await settings.IsSetupCompletedAsync(ct) });

    // Wizard prefill: resolved values for the keys the wizard shows, plus
    // which of them are env-locked. Token-gated so strangers can't read
    // config off a half-installed portal.
    [HttpGet("api/setup/defaults")]
    public async Task<IResult> GetDefaults([FromServices] SettingsService settings, [FromServices] SetupToken token, [FromServices] DatabaseSelection database, CancellationToken ct)
    {
        if (await settings.IsSetupCompletedAsync(ct)) return Results.NotFound();
        if (!token.Matches(Request.Headers["X-Setup-Token"])) return Results.Unauthorized();

        var values = new Dictionary<string, string?>();
        var locked = new List<string>();
        foreach (var key in WizardKeys)
            values[key] = await settings.GetAsync(key, ct);
        locked.AddRange(WizardKeys.Concat(WizardSecretKeys).Where(SettingsService.IsLocked));
        // The database the API is already on, for the step that offers to
        // move it before anybody else has used it.
        var (server, name) = database.Describe();
        return Results.Ok(new SetupDefaultsResponse
        {
            Values = values,
            Locked = locked,
            Database = new SetupDatabaseInfo
            {
                Provider = DatabaseProviders.Name(database.Provider),
                ProviderLabel = DatabaseProviders.Label(database.Provider),
                Server = server,
                Database = name,
            },
        });
    }

    // The wizard's database step, behind the same token as the prefill:
    // the same four facts an administrator's Test reads later.
    [HttpPost("api/setup/database/test")]
    public async Task<IResult> PostDatabaseTest(DatabaseTargetRequest request, [FromServices] SettingsService settings, [FromServices] SetupToken token, [FromServices] DatabaseSelection database, CancellationToken ct)
    {
        if (await settings.IsSetupCompletedAsync(ct)) return Results.NotFound();
        if (!token.Matches(Request.Headers["X-Setup-Token"])) return Results.Unauthorized();
        DatabaseProvider provider;
        try
        {
            provider = DatabaseProviders.Parse(request.Provider);
        }
        catch (InvalidOperationException e)
        {
            return Results.BadRequest(new ErrorResponse(e.Message));
        }
        if (string.IsNullOrWhiteSpace(request.ConnectionString))
            return Results.BadRequest(new ErrorResponse("A connection string is required."));
        return Results.Ok(await DatabaseProbe.TestAsync(provider, request.ConnectionString.Trim(), database, ct));
    }

    [HttpPost("api/setup")]
    public async Task<IResult> PostSetup(SetupRequest request, [FromServices] SetupService setup, [FromServices] SetupToken token, [FromServices] SettingsService settings, [FromServices] TokenService tokens, [FromServices] DatabaseMover mover, CancellationToken ct)
    {
        if (await settings.IsSetupCompletedAsync(ct))
            return Results.Conflict(new ErrorResponse("Setup has already been completed."));
        if (!token.Matches(request.Token))
            return Results.Unauthorized();

        var admin = request.Admin;
        if (admin is null || string.IsNullOrWhiteSpace(admin.Email) || !admin.Email.Contains('@'))
            return Results.BadRequest(new ErrorResponse("A valid administrator email is required."));
        // The first administrator is held to the rule every later password
        // meets, and the wizard ticks it off with the same checklist.
        if (!PasswordRules.Acceptable(admin.Password))
            return Results.BadRequest(new ErrorResponse(PasswordRules.Error));
        if (string.IsNullOrWhiteSpace(admin.DisplayName))
            return Results.BadRequest(new ErrorResponse("A display name is required."));

        User created;
        try
        {
            created = await setup.CompleteAsync(
                admin.Email, admin.Password, admin.DisplayName,
                request.Settings, changedBy: "setup", ct);
        }
        catch (SettingsValidationException ex)
        {
            return Results.BadRequest(new ErrorResponse(ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(new ErrorResponse(ex.Message));
        }

        // Whoever ran setup walks away signed in as the first administrator.
        SessionCookie.Issue(HttpContext, tokens, created, persistent: false);

        // The database step chose the other database: the same move an
        // administrator can run later, on a portal that holds only this
        // account and these settings. Setup is complete either way; a move
        // that cannot start says so and leaves the portal where it is.
        if (request.Database is { ConnectionString.Length: > 0 } move)
        {
            try
            {
                var outcome = mover.Start(DatabaseProviders.Parse(move.Provider), move.ConnectionString.Trim(), created.Email);
                return Results.Ok(new SetupFinishedResponse
                {
                    Moving = outcome.Kind == OutcomeKind.Ok,
                    Error = outcome.Kind == OutcomeKind.Ok ? null : (outcome.Untyped.Body as IErrorResponse)?.Error,
                });
            }
            catch (InvalidOperationException e)
            {
                return Results.Ok(new SetupFinishedResponse { Moving = false, Error = e.Message });
            }
        }
        return Results.NoContent();
    }
}

/// <summary>Setup is complete; whether a database move is now running, or why it could not start.</summary>
public sealed record SetupFinishedResponse
{
    public required bool Moving { get; init; }
    public required string? Error { get; init; }
}

/// <summary>The database the API is already running on, shown by the wizard's database step.</summary>
public sealed record SetupDatabaseInfo
{
    public required string Provider { get; init; }
    public required string ProviderLabel { get; init; }
    public required string Server { get; init; }
    public required string Database { get; init; }
}

/// <summary>Whether the first-run wizard has been completed.</summary>
public sealed record SetupStatusResponse
{
    public required bool Completed { get; init; }
}

/// <summary>The wizard's prefill: each key's resolved value, and the keys an environment variable locks.</summary>
public sealed record SetupDefaultsResponse
{
    public required Dictionary<string, string?> Values { get; init; }
    public required List<string> Locked { get; init; }
    public required SetupDatabaseInfo Database { get; init; }
}
