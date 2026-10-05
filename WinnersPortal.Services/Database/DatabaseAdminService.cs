using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Services.Common;
using WinnersPortal.Services.Settings;

namespace WinnersPortal.Services.Database;

/// <summary>
/// The administrator's view of the database and the doors out of it: where
/// the portal is, what an edited connection names, a save onto this
/// portal's own data (a new password, a new host, a restored copy), and the
/// move into an empty database, which the mover runs in the background.
/// Both end the same way — the choice written beside the keys, the process
/// restarting onto it — and the state reports on either.
/// </summary>
public sealed class DatabaseAdminService(
    DatabaseSelection current, DatabaseMoveState state, DatabaseMover mover, AppDbContext db,
    IDataProtectionProvider protection, AppPause pause, IHostApplicationLifetime lifetime, ILogger<DatabaseAdminService> log)
{
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(30);

    public async Task<Outcome<DatabaseStatusResponse>> StatusAsync(CancellationToken ct)
    {
        var (server, database) = current.Describe();
        string? version = null;
        try
        {
            version = current.Provider == DatabaseProvider.SqlServer
                ? await SqlServerVersionAsync(ct)
                : await db.Database.SqlQueryRaw<string>("SELECT version() AS \"Value\"").FirstOrDefaultAsync(ct);
        }
        catch (Exception e) when (e is System.Data.Common.DbException or InvalidOperationException)
        {
            // The page still says where the portal is; the version is a nicety.
        }
        // A file newer than the process: written by a move whose restart
        // has not happened yet, or by a move the service manager brought
        // the process back from without reading — either way, pending.
        var restartPending = state.RestartRequested
            || (File.Exists(current.OverridePath) && current.Source != DatabaseSource.Override);
        return Outcome.Ok(new DatabaseStatusResponse
        {
            Provider = DatabaseProviders.Name(current.Provider),
            ProviderLabel = DatabaseProviders.Label(current.Provider),
            Source = current.Source.ToString().ToLowerInvariant(),
            Server = server,
            Database = database,
            ServerVersion = version,
            RestartPending = restartPending,
            OverridePath = current.OverridePath,
            Connection = DatabaseConnections.View(current.Provider, current.ConnectionString),
            Move = state.Snapshot(),
        });
    }

    public async Task<Outcome<DatabaseTestResponse>> TestAsync(DatabaseConnectionFields request, CancellationToken ct)
    {
        if (Build(request, out var provider, out var connectionString) is { } problem)
            return Outcome.Invalid(problem);
        return Outcome.Ok(await DatabaseProbe.TestAsync(provider, connectionString, current,
            protection.CreateProtector(SettingsService.ProtectorPurpose), ProbePurpose.Change, ct));
    }

    /// <summary>
    /// Saves an edited connection that reaches this portal's own data and
    /// restarts onto it. Anything else is refused with the reason: an empty
    /// database is a move's, and another portal's or application's is
    /// nobody's to save onto.
    /// </summary>
    public async Task<Outcome<DatabaseMoveResponse>> ChangeAsync(DatabaseConnectionFields request, string by, CancellationToken ct)
    {
        if (Build(request, out var provider, out var connectionString) is { } problem)
            return Outcome.Invalid(problem);
        var probe = await DatabaseProbe.TestAsync(provider, connectionString, current,
            protection.CreateProtector(SettingsService.ProtectorPurpose), ProbePurpose.Change, ct);
        if (probe.Action != "switch")
            return Outcome.Conflict(probe.Problem ?? (probe.Action == "move"
                ? "That database is empty — move the portal's data there rather than saving onto it."
                : "That database cannot be saved onto."));

        var (server, database) = DatabaseOverrideFile.Describe(provider, connectionString);
        if (!state.TryBegin(provider, server, database, by))
            return Outcome.Conflict(state.RestartRequested
                ? "Another database is recorded already; restart the portal to come up on it."
                : "A move is running; wait for it to finish.");

        // As a move ends: nothing writes while the choice changes, the file
        // is written, and the process ends so the service manager brings it
        // back on the new connection.
        state.SetPhase(MovePhase.Pausing);
        pause.Pause("The portal is changing its database connection and will restart in a moment.");
        try
        {
            if (!await pause.DrainAsync(DrainTimeout, ct))
                throw new InvalidOperationException("A background worker did not finish its cycle in thirty seconds; nothing was changed.");
            state.SetPhase(MovePhase.Recording);
            DatabaseOverrideFile.Write(current.OverridePath,
                new DatabaseOverride(provider, server, database, connectionString, DateTimeOffset.UtcNow, by),
                protection.CreateProtector(DatabaseOverrideFile.Purpose));
            state.Recorded();
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            pause.Resume();
            state.Fail(DatabaseConnections.Scrub(e.Message, connectionString));
            return Outcome.Conflict(DatabaseConnections.Scrub(e.Message, connectionString));
        }
        log.LogWarning("Database connection changed to {Provider} at {Server}/{Database} by {By}; restarting on it.",
            DatabaseProviders.Name(provider), server, database, by);
        _ = Task.Run(async () =>
        {
            // A moment first, so the page's next poll reads "restarting".
            await Task.Delay(1500);
            lifetime.StopApplication();
        });
        return Outcome.Ok(state.Snapshot());
    }

    public Outcome<DatabaseMoveResponse> Move(DatabaseConnectionFields request, string startedBy)
    {
        if (Build(request, out var provider, out var connectionString) is { } problem)
            return Outcome.Invalid(problem);
        return mover.Start(provider, connectionString, startedBy);
    }

    public Outcome<DatabaseMoveResponse> Progress() => Outcome.Ok(state.Snapshot());

    /// <summary>The fields as a string, built on the running one so a blank password can keep it.</summary>
    private string? Build(DatabaseConnectionFields request, out DatabaseProvider provider, out string connectionString) =>
        DatabaseConnections.TryBuild(request, (current.Provider, current.ConnectionString), out provider, out connectionString);

    private async Task<string> SqlServerVersionAsync(CancellationToken ct)
    {
        var report = await SqlServerRequirements.ReadAsync(db.Database.GetConnectionString()!, ct);
        return $"SQL Server {report.ProductVersion} ({report.Edition})";
    }
}
