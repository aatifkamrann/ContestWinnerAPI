using Microsoft.EntityFrameworkCore;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Services.Common;

namespace WinnersPortal.Services.Database;

/// <summary>
/// The administrator's view of the database and the door to moving it:
/// where the portal is, what a candidate is, and the move itself, which
/// the mover runs in the background and the state reports on.
/// </summary>
public sealed class DatabaseAdminService(
    DatabaseSelection current, DatabaseMoveState state, DatabaseMover mover, AppDbContext db)
{
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
            Move = state.Snapshot(),
        });
    }

    public async Task<Outcome<DatabaseTestResponse>> TestAsync(DatabaseTargetRequest request, CancellationToken ct)
    {
        if (Parse(request, out var provider, out var connectionString) is { } problem)
            return Outcome.Invalid(problem);
        return Outcome.Ok(await DatabaseProbe.TestAsync(provider, connectionString, current, ct));
    }

    public Outcome<DatabaseMoveResponse> Move(DatabaseTargetRequest request, string startedBy)
    {
        if (Parse(request, out var provider, out var connectionString) is { } problem)
            return Outcome.Invalid(problem);
        return mover.Start(provider, connectionString, startedBy);
    }

    public Outcome<DatabaseMoveResponse> Progress() => Outcome.Ok(state.Snapshot());

    private static string? Parse(DatabaseTargetRequest request, out DatabaseProvider provider, out string connectionString)
    {
        provider = DatabaseProvider.Postgres;
        connectionString = request.ConnectionString?.Trim() ?? "";
        try
        {
            provider = DatabaseProviders.Parse(request.Provider);
        }
        catch (InvalidOperationException e)
        {
            return e.Message;
        }
        return connectionString.Length == 0 ? "A connection string is required." : null;
    }

    private async Task<string> SqlServerVersionAsync(CancellationToken ct)
    {
        var report = await SqlServerRequirements.ReadAsync(db.Database.GetConnectionString()!, ct);
        return $"SQL Server {report.ProductVersion} ({report.Edition})";
    }
}
