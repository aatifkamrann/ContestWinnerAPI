using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
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
        Results.Ok(new SetupStatusResponse { Completed = await settings.IsSetupCompletedAsync(ct), NeedsDatabase = false, Connecting = null });

    // Wizard prefill: resolved values for the keys the wizard shows, plus
    // which of them the deployment's environment supplies. Token-gated so strangers can't read
    // config off a half-installed portal.
    [HttpGet("api/setup/defaults")]
    public async Task<IResult> GetDefaults([FromServices] SettingsService settings, [FromServices] SetupToken token, [FromServices] DatabaseSelection database, CancellationToken ct)
    {
        if (await settings.IsSetupCompletedAsync(ct)) return Results.NotFound();
        if (!token.Matches(Request.Headers["X-Setup-Token"])) return Results.Unauthorized();

        var values = new Dictionary<string, string?>();
        var fromEnvironment = new List<string>();
        foreach (var key in WizardKeys)
            values[key] = await settings.GetAsync(key, ct);
        foreach (var key in WizardKeys.Concat(WizardSecretKeys))
            if (await settings.SourceAsync(key, ct) == SettingSource.Environment) fromEnvironment.Add(key);
        // The database the API is already on, for the step that offers to
        // move it before anybody else has used it.
        var (server, name) = database.Describe();
        return Results.Ok(new SetupDefaultsResponse
        {
            Values = values,
            FromEnvironment = fromEnvironment,
            NeedsDatabase = false,
            Connection = null,
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
    public async Task<IResult> PostDatabaseTest(DatabaseConnectionFields request, [FromServices] SettingsService settings, [FromServices] SetupToken token, [FromServices] DatabaseSelection database, [FromServices] IDataProtectionProvider protection, CancellationToken ct)
    {
        if (await settings.IsSetupCompletedAsync(ct)) return Results.NotFound();
        if (!token.Matches(Request.Headers["X-Setup-Token"])) return Results.Unauthorized();
        if (DatabaseConnections.TryBuild(request, (database.Provider, database.ConnectionString), out var provider, out var connectionString) is { } problem)
            return Results.BadRequest(new ErrorResponse(problem));
        return Results.Ok(await DatabaseProbe.TestAsync(provider, connectionString, database,
            protection.CreateProtector(SettingsService.ProtectorPurpose), ProbePurpose.Move, ct));
    }

    [HttpPost("api/setup")]
    public async Task<IResult> PostSetup(SetupRequest request, [FromServices] SetupService setup, [FromServices] SetupToken token, [FromServices] SettingsService settings, [FromServices] TokenService tokens, [FromServices] DatabaseMover mover, [FromServices] DatabaseSelection database, CancellationToken ct)
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
        if (request.Database is { } move)
        {
            if (DatabaseConnections.TryBuild(move, (database.Provider, database.ConnectionString), out var provider, out var connectionString) is { } problem)
                return Results.Ok(new SetupFinishedResponse { Moving = false, Error = problem });
            var outcome = mover.Start(provider, connectionString, created.Email);
            return Results.Ok(new SetupFinishedResponse
            {
                Moving = outcome.Kind == OutcomeKind.Ok,
                Error = outcome.Kind == OutcomeKind.Ok ? null : (outcome.Untyped.Body as IErrorResponse)?.Error,
            });
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

/// <summary>Whether the first-run wizard has been completed, and whether the API is still waiting for a database.</summary>
public sealed record SetupStatusResponse
{
    public required bool Completed { get; init; }
    /// <summary>No database is connected yet: the API serves only the setup page's connect step (DatabaseBootstrap).</summary>
    public required bool NeedsDatabase { get; init; }
    /// <summary>The connect under way, while there is no database; null when none was started.</summary>
    public required SetupConnectingResponse? Connecting { get; init; }
}

/// <summary>A first connect as the setup page follows it.</summary>
public sealed record SetupConnectingResponse
{
    /// <summary>checking, preparing (creating the tables), recording, starting, failed.</summary>
    public required string Phase { get; init; }
    public required string Server { get; init; }
    public required string Database { get; init; }
    public required string? Error { get; init; }
}

/// <summary>The wizard's prefill: each key's resolved value, and the keys whose value comes from the deployment's environment (editable; what is saved wins).</summary>
public sealed record SetupDefaultsResponse
{
    public required Dictionary<string, string?> Values { get; init; }
    public required List<string> FromEnvironment { get; init; }
    /// <summary>The database the API is on; null while it has none.</summary>
    public required SetupDatabaseInfo? Database { get; init; }
    /// <summary>No database is connected yet, and the page starts by connecting one.</summary>
    public required bool NeedsDatabase { get; init; }
    /// <summary>The connect form's starting point while there is no database; null once there is.</summary>
    public required DatabaseConnectionView? Connection { get; init; }
}
