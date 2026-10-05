using System.Data.Common;
using System.Security.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Npgsql;
using WinnersPortal.Infrastructure.Data;

namespace WinnersPortal.Services.Database;

/// <summary>What a test is asked on behalf of: each wants something different of the same facts.</summary>
public enum ProbePurpose
{
    /// <summary>The first connection of a portal that has none: a new database, an empty one, or a portal's own.</summary>
    Connect,
    /// <summary>A move's target: empty, and not the database the portal is on.</summary>
    Move,
    /// <summary>An administrator's edit of the connection: this portal's data (save), or an empty database (move).</summary>
    Change,
}

/// <summary>What a database held when it was asked, for comparing a copy with the original.</summary>
public sealed record DatabaseFreshness(long Accounts, long Opportunities, DateTimeOffset? LatestActivityUtc);

/// <summary>What was found, before any purpose is applied to it.</summary>
/// <param name="Empty">No tables, or only the portal's own with no account, setting or opportunity in them.</param>
/// <param name="HoldsPortal">The portal's tables, with rows in them.</param>
/// <param name="SamePortal">Its secret settings open with this portal's keys; null when nothing could say.</param>
/// <param name="Problem">Why it cannot be used at all (unreachable, too old, no full-text, no database); null when it can.</param>
public sealed record DatabaseFacts(
    bool Reachable,
    string? ServerVersion,
    bool VersionOk,
    bool FullTextOk,
    bool Exists,
    bool Empty,
    bool HoldsPortal,
    bool? SamePortal,
    DatabaseFreshness? Freshness,
    string? Problem);

/// <summary>
/// What a candidate database is, before anything is staked on it:
/// reachable, new enough, with full-text where that matters, and what it
/// holds — nothing, the portal's own empty tables (what a move that failed
/// halfway leaves), this portal's data, another portal's, or another
/// application's. The facts are read once; <see cref="Verdict"/> then says
/// what the asking screen may do with them. Shared by the setup wizard's
/// connect and move steps, Admin → Database, and the mover's first step.
/// </summary>
public static class DatabaseProbe
{
    public static async Task<DatabaseTestResponse> TestAsync(
        DatabaseProvider provider, string connectionString, DatabaseSelection? current, IDataProtector? settings,
        ProbePurpose purpose, CancellationToken ct)
    {
        var isCurrent = current is not null && IsCurrent(provider, connectionString, current);
        if (purpose == ProbePurpose.Move && isCurrent)
            return Response(Unreachable("That is the database the portal is on now."), null, null, null, null);

        var facts = await FactsAsync(provider, connectionString, settings, ct);
        DatabaseFreshness? here = null;
        if (purpose == ProbePurpose.Change && current is not null && facts.HoldsPortal && !isCurrent)
        {
            try
            {
                here = (await FactsAsync(current.Provider, current.ConnectionString, null, ct)).Freshness;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // The comparison is a nicety; the verdict stands without it.
            }
        }
        var (action, problem, warning) = Verdict(facts, purpose, isCurrent, here);
        return Response(facts, action, problem, warning, here);
    }

    /// <summary>The same server and database as the portal is on, however the address is written.</summary>
    private static bool IsCurrent(DatabaseProvider provider, string connectionString, DatabaseSelection current)
    {
        if (provider != current.Provider) return false;
        var (server, database) = DatabaseOverrideFile.Describe(provider, connectionString);
        var (currentServer, currentDatabase) = current.Describe();
        return string.Equals(database, currentDatabase, StringComparison.OrdinalIgnoreCase)
            && string.Equals(ServerKey(provider, server), ServerKey(provider, currentServer), StringComparison.OrdinalIgnoreCase);
    }

    private static string ServerKey(DatabaseProvider provider, string server)
    {
        if (provider != DatabaseProvider.SqlServer) return server.Trim();
        var (name, port, portFixed) = DatabaseConnections.SplitDataSource(server);
        if (portFixed) return name;
        var host = name.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase) ? name[4..] : name;
        return $"{host.Trim()}:{port?.ToString() ?? (host.Contains('\\') ? "" : "1433")}";
    }

    /// <summary>Pure: what the asking screen may do with the facts — an action, or the reason it may not, and a caution.</summary>
    public static (string? Action, string? Problem, string? Warning) Verdict(
        DatabaseFacts facts, ProbePurpose purpose, bool isCurrent, DatabaseFreshness? current)
    {
        if (facts.Problem is not null) return (null, facts.Problem, null);
        var foreign = !facts.Empty && !facts.HoldsPortal;
        switch (purpose)
        {
            case ProbePurpose.Connect:
                if (foreign) return (null, ForeignTables, null);
                if (facts.HoldsPortal && facts.SamePortal == false)
                    return ("connect", null,
                        "That database holds a portal whose secret settings do not open with the keys here, so they will read "
                        + "as unset and payment details as unreadable. Restore the keys directory that came with it first, "
                        + "if you have it.");
                return ("connect", null, null);

            case ProbePurpose.Move:
                if (isCurrent) return (null, "That is the database the portal is on now.", null);
                return facts.Empty ? ("move", null, null) : (null, NotEmpty, null);

            default:
                if (isCurrent || (facts.HoldsPortal && facts.SamePortal == true))
                    return ("switch", null, isCurrent ? null : Behind(facts.Freshness, current));
                if (facts.Empty) return ("move", null, null);
                if (facts.HoldsPortal)
                    return (null,
                        "That database holds another portal's data — its secret settings do not open with this portal's keys. "
                        + "Point at this portal's database, or at an empty one to move to.", null);
                return (null, ForeignTables, null);
        }
    }

    /// <summary>A caution when the copy looks older than the database in use; null when it does not, or nothing says.</summary>
    private static string? Behind(DatabaseFreshness? there, DatabaseFreshness? here)
    {
        if (there is null || here is null) return null;
        var older = there.LatestActivityUtc is { } t && here.LatestActivityUtc is { } h && t < h.AddMinutes(-1);
        if (there.Accounts >= here.Accounts && there.Opportunities >= here.Opportunities && !older) return null;
        return $"That copy looks older than the database in use: {there.Accounts} accounts and {there.Opportunities} "
            + $"opportunities there, {here.Accounts} and {here.Opportunities} here"
            + (older ? $", its last activity {there.LatestActivityUtc:yyyy-MM-dd HH:mm} UTC against {here.LatestActivityUtc:yyyy-MM-dd HH:mm}" : "")
            + ". Saving points the portal at it; whatever is newer here stays behind, unread.";
    }

    private const string NotEmpty =
        "That database already holds tables with data in them. A move only fills an empty database — "
        + "or the portal's own empty tables left by a move that failed — so point it at a new one.";

    private const string ForeignTables =
        "That database holds tables that are not the portal's. Point at a new or empty database, or at this portal's own.";

    // ---- the facts ------------------------------------------------------------

    public static async Task<DatabaseFacts> FactsAsync(
        DatabaseProvider provider, string connectionString, IDataProtector? settings, CancellationToken ct)
    {
        var probe = ForProbe(provider, connectionString);
        try
        {
            return provider == DatabaseProvider.SqlServer
                ? await SqlServerAsync(probe, settings, ct)
                : await PostgresAsync(probe, settings, ct);
        }
        // Whatever the driver throws on the way — a malformed string, a host
        // that does not resolve, a refused login, a timeout — is an answer
        // to the question "can the portal reach it", never a 500.
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return Unreachable("The server could not be reached with those settings: " + FirstLine(e.Message)
                + CertificateHint(e, provider, connectionString));
        }
    }

    /// <summary>
    /// The string a test connects with: unpooled, so a retest after a fix is
    /// not answered by the pool's memory of the last failure, and quick to
    /// give up on a server that is not there.
    /// </summary>
    private static string ForProbe(DatabaseProvider provider, string connectionString)
    {
        try
        {
            if (provider == DatabaseProvider.SqlServer)
            {
                var b = new SqlConnectionStringBuilder(connectionString) { Pooling = false };
                b.ConnectTimeout = Math.Min(b.ConnectTimeout, 10);
                return b.ConnectionString;
            }
            var pg = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false };
            pg.Timeout = Math.Min(pg.Timeout, 10);
            return pg.ConnectionString;
        }
        catch (ArgumentException)
        {
            return connectionString; // the driver's own open says what is wrong with it
        }
    }

    /// <summary>A certificate the client refused, with the box that would accept it still unticked.</summary>
    private static string CertificateHint(Exception e, DatabaseProvider provider, string connectionString)
    {
        var aboutCertificate = false;
        for (var x = e; x is not null; x = x.InnerException)
            if (x is AuthenticationException || x.Message.Contains("certificate", StringComparison.OrdinalIgnoreCase))
                aboutCertificate = true;
        if (!aboutCertificate) return "";
        try
        {
            var trusted = DatabaseConnections.View(provider, connectionString).TrustServerCertificate;
            return trusted ? "" : " Tick Trust Server Certificate if the server uses a self-signed certificate.";
        }
        catch (ArgumentException)
        {
            return "";
        }
    }

    private static async Task<DatabaseFacts> SqlServerAsync(string connectionString, IDataProtector? settings, CancellationToken ct)
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
        var contents = exists
            ? await ContentsAsync(() => new SqlConnection(connectionString), SqlServer, settings, ct)
            : Contents.Nothing;
        return new DatabaseFacts(
            Reachable: true,
            ServerVersion: $"SQL Server {report.ProductVersion} ({report.Edition})",
            VersionOk: report.MajorVersion >= SqlServerRequirements.MinMajorVersion,
            FullTextOk: report.FullTextInstalled,
            Exists: exists,
            Empty: contents.Kind is Holding.Nothing,
            HoldsPortal: contents.Kind is Holding.Portal,
            SamePortal: contents.SamePortal,
            Freshness: contents.Freshness,
            Problem: SqlServerRequirements.Problem(report));
    }

    private static async Task<DatabaseFacts> PostgresAsync(string connectionString, IDataProtector? settings, CancellationToken ct)
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
            return new DatabaseFacts(true, null, true, true, Exists: false, Empty: true, HoldsPortal: false,
                SamePortal: null, Freshness: null,
                Problem: "The server is reachable but that database does not exist. Create it first "
                    + "(CREATE DATABASE … OWNER …) and test again; the portal will make the tables.");
        }
        var contents = await ContentsAsync(() => new NpgsqlConnection(connectionString), Postgres, settings, ct);
        return new DatabaseFacts(true, FirstLine(version), true, true, Exists: true,
            Empty: contents.Kind is Holding.Nothing, HoldsPortal: contents.Kind is Holding.Portal,
            SamePortal: contents.SamePortal, Freshness: contents.Freshness, Problem: null);
    }

    // ---- what is in it --------------------------------------------------------

    private enum Holding { Nothing, Portal, Foreign }

    private sealed record Contents(Holding Kind, bool? SamePortal, DatabaseFreshness? Freshness)
    {
        public static readonly Contents Nothing = new(Holding.Nothing, null, null);
    }

    /// <summary>The queries, in each dialect's quoting.</summary>
    private sealed record Dialect(string CountTables, string CountRows, string Freshness, string Secrets);

    private static readonly Dialect SqlServer = new(
        "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'dbo' AND table_type = 'BASE TABLE'",
        "SELECT (SELECT count(*) FROM [Users]) + (SELECT count(*) FROM [Settings]) + (SELECT count(*) FROM [Opportunities])",
        "SELECT (SELECT count_big(*) FROM [Users]), (SELECT count_big(*) FROM [Opportunities]), (SELECT max([AtUtc]) FROM [ActivityEvents])",
        "SELECT TOP 5 [Value] FROM [Settings] WHERE [IsSecret] = 1 AND [Value] IS NOT NULL");

    private static readonly Dialect Postgres = new(
        "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE'",
        "SELECT (SELECT count(*) FROM \"Users\") + (SELECT count(*) FROM \"Settings\") + (SELECT count(*) FROM \"Opportunities\")",
        "SELECT (SELECT count(*) FROM \"Users\"), (SELECT count(*) FROM \"Opportunities\"), (SELECT max(\"AtUtc\") FROM \"ActivityEvents\")",
        "SELECT \"Value\" FROM \"Settings\" WHERE \"IsSecret\" AND \"Value\" IS NOT NULL LIMIT 5");

    /// <summary>
    /// No base tables at all, or the portal's own with no account, setting or
    /// opportunity in them, is nothing; the portal's with rows is a portal —
    /// this one when a secret setting opens with the keys here, the one proof
    /// a copy cannot fake and another install cannot share; tables of any
    /// other shape are someone else's.
    /// </summary>
    private static async Task<Contents> ContentsAsync(
        Func<DbConnection> open, Dialect sql, IDataProtector? settings, CancellationToken ct)
    {
        await using var connection = open();
        await connection.OpenAsync(ct);
        if (Convert.ToInt64(await ScalarAsync(connection, sql.CountTables, ct)) == 0) return Contents.Nothing;
        try
        {
            if (Convert.ToInt64(await ScalarAsync(connection, sql.CountRows, ct)) == 0) return Contents.Nothing;
        }
        catch (DbException)
        {
            return new Contents(Holding.Foreign, null, null); // tables, but not the portal's
        }

        DatabaseFreshness? freshness = null;
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql.Freshness;
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
                freshness = new DatabaseFreshness(
                    Convert.ToInt64(reader.GetValue(0)), Convert.ToInt64(reader.GetValue(1)),
                    reader.IsDBNull(2) ? null : ToOffset(reader.GetValue(2)));
        }
        catch (DbException)
        {
            // An older portal without the activity table still counts as a portal.
        }

        bool? same = null;
        if (settings is not null)
        {
            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql.Secrets;
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    same = false;
                    try
                    {
                        settings.Unprotect(reader.GetString(0));
                        same = true;
                        break;
                    }
                    catch (System.Security.Cryptography.CryptographicException)
                    {
                    }
                }
            }
            catch (DbException)
            {
            }
        }
        return new Contents(Holding.Portal, same, freshness);
    }

    private static DateTimeOffset ToOffset(object value) => value switch
    {
        DateTimeOffset o => o,
        DateTime d => new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Utc)),
        _ => DateTimeOffset.Parse(value.ToString()!),
    };

    private static async Task<object?> ScalarAsync(DbConnection connection, string text, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = text;
        return await command.ExecuteScalarAsync(ct);
    }

    private static DatabaseTestResponse Response(
        DatabaseFacts facts, string? action, string? problem, string? warning, DatabaseFreshness? current) => new()
    {
        Reachable = facts.Reachable,
        ServerVersion = facts.ServerVersion,
        VersionOk = facts.VersionOk,
        FullTextOk = facts.FullTextOk,
        DatabaseExists = facts.Exists,
        Empty = facts.Empty,
        HoldsPortal = facts.HoldsPortal,
        SamePortal = facts.SamePortal,
        Action = action,
        Problem = problem ?? facts.Problem,
        Warning = warning,
        Target = facts.Freshness,
        Current = current,
    };

    private static DatabaseFacts Unreachable(string problem) =>
        new(false, null, false, false, false, false, false, null, null, problem);

    private static string FirstLine(string s) => s.Split('\n')[0].Trim();
}
