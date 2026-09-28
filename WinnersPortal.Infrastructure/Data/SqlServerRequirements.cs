using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace WinnersPortal.Infrastructure.Data;

/// <summary>A database the portal will not run on, and the sentence that says what to install.</summary>
public sealed class DatabaseRequirementException(string message) : Exception(message);

/// <summary>
/// What SQL Server has to be before the portal will use it: 2022 or later
/// (the model asks nothing of the server that 2022 lacks — its JSON
/// documents are nvarchar(max), not the json type of 2025 — and older
/// servers are out of mainstream support), and Full-Text Search, which
/// the opportunity feed's search runs on. Checked once at startup before the
/// first migration, and by the tests an administrator runs against a
/// database they are about to move to.
/// </summary>
public static class SqlServerRequirements
{
    /// <summary>SQL Server 2022 reports ProductMajorVersion 16; 2025 reports 17.</summary>
    public const int MinMajorVersion = 16;

    public sealed record Report(string ProductVersion, int MajorVersion, string Edition, bool FullTextInstalled);

    public const string Query = """
        SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128)),
               CAST(SERVERPROPERTY('ProductMajorVersion') AS int),
               CAST(SERVERPROPERTY('Edition') AS nvarchar(128)),
               CAST(SERVERPROPERTY('IsFullTextInstalled') AS int)
        """;

    /// <summary>Pure: why this server will not do, or null when it will.</summary>
    public static string? Problem(Report r)
    {
        if (r.MajorVersion < MinMajorVersion)
            return $"SQL Server {r.ProductVersion} ({r.Edition}) is too old: the portal needs SQL Server 2022 or later "
                + $"(ProductMajorVersion {MinMajorVersion} or above; this server reports {r.MajorVersion}). "
                + "Install SQL Server 2022 or 2025 — Express will do in its Advanced download, which carries Full-Text Search — or point ConnectionStrings__Db at one.";
        if (!r.FullTextInstalled)
            return $"SQL Server {r.ProductVersion} has no Full-Text Search (SERVERPROPERTY('IsFullTextInstalled') = 0), "
                + "and opportunity search runs on it. Windows: re-run SQL Server setup, Add features, tick "
                + "\"Full-Text and Semantic Extractions for Search\", then start the SQL Full-text Filter Daemon "
                + "Launcher service. Linux: install the mssql-server-fts package and restart SQL Server. Compose: "
                + "build the mssql service from deploy/mssql/Dockerfile, which installs it.";
        return null;
    }

    public static async Task<Report> ReadAsync(DbConnection open, CancellationToken ct)
    {
        await using var command = open.CreateCommand();
        command.CommandText = Query;
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new DatabaseRequirementException("SQL Server answered the version query with no row.");
        return new Report(reader.GetString(0), reader.GetInt32(1), reader.GetString(2), reader.GetInt32(3) == 1);
    }

    /// <summary>
    /// Reads the server behind a connection string. A database that does
    /// not exist yet (error 4060) is not "too old": the read is retried
    /// against master, since the first migration will create it.
    /// </summary>
    public static async Task<Report> ReadAsync(string connectionString, CancellationToken ct)
    {
        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(ct);
            return await ReadAsync(connection, ct);
        }
        catch (SqlException e) when (e.Number == 4060)
        {
            var master = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master" };
            await using var connection = new SqlConnection(master.ConnectionString);
            await connection.OpenAsync(ct);
            return await ReadAsync(connection, ct);
        }
    }

    /// <summary>The startup check: throws with the named problem, so the service manager's log says what to install.</summary>
    public static async Task<Report> CheckAsync(AppDbContext db, CancellationToken ct)
    {
        var report = await ReadAsync(db.Database.GetConnectionString()!, ct);
        if (Problem(report) is { } problem) throw new DatabaseRequirementException(problem);
        return report;
    }
}
