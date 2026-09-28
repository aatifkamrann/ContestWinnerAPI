using System.Data.Common;
using Microsoft.Data.SqlClient;
using Npgsql;
using WinnersPortal.Infrastructure.Data;

namespace WinnersPortal.Services.Database;

/// <summary>
/// What a candidate database is, before a move stakes anything on it:
/// reachable, new enough, with full-text where that matters, and empty —
/// or holding only the portal's own schema with nothing in it, which is
/// what a move that failed halfway leaves behind and may be tried again.
/// Shared by the admin page's Test, the setup wizard's, and the mover's
/// first step.
/// </summary>
public static class DatabaseProbe
{
    public static async Task<DatabaseTestResponse> TestAsync(
        DatabaseProvider provider, string connectionString, DatabaseSelection current, CancellationToken ct)
    {
        var (server, database) = DatabaseOverrideFile.Describe(provider, connectionString);
        var (currentServer, currentDatabase) = current.Describe();
        if (provider == current.Provider
            && string.Equals(server, currentServer, StringComparison.OrdinalIgnoreCase)
            && string.Equals(database, currentDatabase, StringComparison.OrdinalIgnoreCase))
            return Unreachable("That is the database the portal is on now.");

        try
        {
            return provider == DatabaseProvider.SqlServer
                ? await SqlServerAsync(connectionString, ct)
                : await PostgresAsync(connectionString, ct);
        }
        // Whatever the driver throws on the way — a malformed string, a host
        // that does not resolve, a refused login, a timeout — is an answer
        // to the question "can the portal reach it", never a 500.
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return Unreachable("The server could not be reached with that connection string: " + FirstLine(e.Message));
        }
    }

    private static async Task<DatabaseTestResponse> SqlServerAsync(string connectionString, CancellationToken ct)
    {
        var exists = true;
        SqlServerRequirements.Report report;
        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(ct);
            report = await SqlServerRequirements.ReadAsync(connection, ct);
        }
        catch (SqlException e) when (e.Number == 4060)
        {
            exists = false;
            report = await SqlServerRequirements.ReadAsync(connectionString, ct); // retries against master
        }
        var problem = SqlServerRequirements.Problem(report);
        var versionOk = report.MajorVersion >= SqlServerRequirements.MinMajorVersion;
        var empty = !exists || await EmptyAsync(
            () => new SqlConnection(connectionString),
            "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'dbo' AND table_type = 'BASE TABLE'",
            "SELECT (SELECT count(*) FROM [Users]) + (SELECT count(*) FROM [Settings]) + (SELECT count(*) FROM [Opportunities])",
            ct);
        return new DatabaseTestResponse
        {
            Reachable = true,
            ServerVersion = $"SQL Server {report.ProductVersion} ({report.Edition})",
            VersionOk = versionOk,
            FullTextOk = report.FullTextInstalled,
            DatabaseExists = exists,
            Empty = empty,
            Problem = problem ?? (empty ? null : NotEmpty),
        };
    }

    private static async Task<DatabaseTestResponse> PostgresAsync(string connectionString, CancellationToken ct)
    {
        string version;
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT version()";
            version = (string)(await command.ExecuteScalarAsync(ct))!;
        }
        catch (PostgresException e) when (e.SqlState == "3D000")
        {
            // The portal does not create a Postgres database: the role that
            // may is not the portal's, and the encoding and owner are the
            // operator's choices.
            return new DatabaseTestResponse
            {
                Reachable = true, ServerVersion = null, VersionOk = true, FullTextOk = true,
                DatabaseExists = false, Empty = true,
                Problem = "The server is reachable but that database does not exist. Create it first "
                    + "(CREATE DATABASE … OWNER …) and test again; the portal will make the tables.",
            };
        }
        var empty = await EmptyAsync(
            () => new NpgsqlConnection(connectionString),
            "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE'",
            "SELECT (SELECT count(*) FROM \"Users\") + (SELECT count(*) FROM \"Settings\") + (SELECT count(*) FROM \"Opportunities\")",
            ct);
        return new DatabaseTestResponse
        {
            Reachable = true,
            ServerVersion = FirstLine(version),
            VersionOk = true,
            FullTextOk = true,
            DatabaseExists = true,
            Empty = empty,
            Problem = empty ? null : NotEmpty,
        };
    }

    private const string NotEmpty =
        "That database already holds tables with data in them. A move only fills an empty database — "
        + "or the portal's own empty tables left by a move that failed — so point it at a new one.";

    /// <summary>No base tables at all, or the portal's own schema with no account, setting or opportunity in it.</summary>
    private static async Task<bool> EmptyAsync(
        Func<DbConnection> open, string countTables, string countRows, CancellationToken ct)
    {
        await using var connection = open();
        await connection.OpenAsync(ct);
        await using (var tables = connection.CreateCommand())
        {
            tables.CommandText = countTables;
            if (Convert.ToInt64(await tables.ExecuteScalarAsync(ct)) == 0) return true;
        }
        try
        {
            await using var rows = connection.CreateCommand();
            rows.CommandText = countRows;
            return Convert.ToInt64(await rows.ExecuteScalarAsync(ct)) == 0;
        }
        catch (DbException)
        {
            return false; // tables, but not the portal's
        }
    }

    private static DatabaseTestResponse Unreachable(string problem) => new()
    {
        Reachable = false, ServerVersion = null, VersionOk = false, FullTextOk = false,
        DatabaseExists = false, Empty = false, Problem = problem,
    };

    private static string FirstLine(string s) => s.Split('\n')[0].Trim();
}
