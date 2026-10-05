using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Npgsql;
using WinnersPortal.Infrastructure.Data;

namespace WinnersPortal.Services.Database;

/// <summary>
/// A connection as the screens edit it — the setup wizard's connect and
/// move steps and Admin → Database — so nobody types a connection string.
/// A blank password keeps the one already in use, while the server and the
/// user stay the same.
/// </summary>
/// <param name="Authentication">sql (a login and its password) or windows (the account the API runs as; SQL Server only).</param>
public sealed record DatabaseConnectionFields(
    string? Provider,
    string? Server,
    int? Port,
    string? Database,
    string? Authentication,
    string? UserId,
    string? Password,
    bool Encrypt,
    bool TrustServerCertificate);

/// <summary>A connection read back for the screens: its fields, never its password.</summary>
public sealed record DatabaseConnectionView
{
    /// <summary>postgres or sqlserver.</summary>
    public required string Provider { get; init; }
    public required string Server { get; init; }
    /// <summary>Null when the address names none: the provider's own port, or a named instance's.</summary>
    public required int? Port { get; init; }
    public required string Database { get; init; }
    /// <summary>sql or windows.</summary>
    public required string Authentication { get; init; }
    public required string UserId { get; init; }
    /// <summary>A password is in use, and a blank one on the screen keeps it.</summary>
    public required bool PasswordSet { get; init; }
    public required bool Encrypt { get; init; }
    public required bool TrustServerCertificate { get; init; }
    /// <summary>The address takes no port (a named pipe, shared memory, LocalDB, several PostgreSQL hosts).</summary>
    public required bool PortFixed { get; init; }
    /// <summary>A sign-in method the fields do not cover (Authentication=ActiveDirectory…), kept as it is; null when there is none.</summary>
    public required string? Advanced { get; init; }
}

/// <summary>
/// Fields to a connection string and back. A string being edited is the
/// starting point, so whatever the fields do not cover — a timeout, an
/// application name — is kept; only what a field names is written, and a
/// checkbox left as it was writes nothing, so a stricter setting than the
/// checkbox can say (Encrypt=Strict, SSL Mode=VerifyCA) survives a save.
/// </summary>
public static class DatabaseConnections
{
    public const string Sql = "sql";
    public const string Windows = "windows";

    /// <summary>What an empty form starts from: the database the portal names itself after, on this machine.</summary>
    public static DatabaseConnectionView Blank(DatabaseProvider provider) => new()
    {
        Provider = DatabaseProviders.Name(provider),
        Server = "localhost",
        Port = null,
        Database = "winnersportal",
        Authentication = Sql,
        UserId = "",
        PasswordSet = false,
        Encrypt = true,
        TrustServerCertificate = false,
        PortFixed = false,
        Advanced = null,
    };

    public static DatabaseConnectionView View(DatabaseProvider provider, string connectionString) =>
        provider == DatabaseProvider.SqlServer ? SqlServerView(connectionString) : PostgresView(connectionString);

    /// <summary>
    /// A driver's message with the connection string, and the password in
    /// it, taken out: messages go to the log and to the page.
    /// </summary>
    public static string Scrub(string message, string connectionString)
    {
        var scrubbed = message.Replace(connectionString, "[connection string]", StringComparison.OrdinalIgnoreCase);
        try
        {
            var password = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = connectionString }
                .TryGetValue("Password", out var p) ? p as string : null;
            if (password is { Length: >= 4 }) scrubbed = scrubbed.Replace(password, "[password]", StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
        }
        return scrubbed;
    }

    /// <summary>The user a string signs in as, for the file beside the keys; null for Windows sign-in.</summary>
    public static string? UserOf(DatabaseProvider provider, string connectionString)
    {
        try
        {
            var view = View(provider, connectionString);
            return view.Authentication == Windows || view.UserId.Length == 0 ? null : view.UserId;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// The connection string the fields describe, built on <paramref name="current"/>
    /// when it is the same kind of database; the problem in a sentence, or null.
    /// </summary>
    public static string? TryBuild(
        DatabaseConnectionFields fields,
        (DatabaseProvider Provider, string ConnectionString)? current,
        out DatabaseProvider provider,
        out string connectionString)
    {
        connectionString = "";
        provider = DatabaseProviders.Default;
        try
        {
            provider = DatabaseProviders.Parse(fields.Provider);
        }
        catch (InvalidOperationException e)
        {
            return e.Message;
        }
        var server = fields.Server?.Trim() ?? "";
        var database = fields.Database?.Trim() ?? "";
        var authentication = string.IsNullOrWhiteSpace(fields.Authentication) ? Sql : fields.Authentication.Trim().ToLowerInvariant();
        if (server.Length == 0) return "The server is required — a host name or address, such as localhost or db.example.com.";
        if (database.Length == 0) return "The database name is required.";
        if (fields.Port is { } p && (p < 1 || p > 65535)) return "The port is a number from 1 to 65535.";
        if (authentication is not (Sql or Windows)) return "Authentication is either a SQL login (sql) or Windows (windows).";
        if (authentication == Sql && string.IsNullOrWhiteSpace(fields.UserId))
            return provider == DatabaseProvider.SqlServer
                ? "A SQL login needs its user ID — or choose Windows authentication."
                : "PostgreSQL needs the user to sign in as.";

        var same = current is { } c && c.Provider == provider ? c.ConnectionString : null;
        try
        {
            return provider == DatabaseProvider.SqlServer
                ? BuildSqlServer(fields with { Server = server, Database = database, Authentication = authentication }, same, out connectionString)
                : BuildPostgres(fields with { Server = server, Database = database, Authentication = authentication }, same, out connectionString);
        }
        catch (Exception e) when (e is ArgumentException or FormatException or KeyNotFoundException)
        {
            return "Those fields do not make a connection the driver accepts: " + e.Message;
        }
    }

    // ---- SQL Server ---------------------------------------------------------

    private static readonly Regex Protocol = new("^(tcp|np|lpc|admin):", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The server field and port of a Data Source: <c>tcp:host\INSTANCE,1433</c> → (<c>tcp:host\INSTANCE</c>, 1433).</summary>
    public static (string Server, int? Port, bool PortFixed) SplitDataSource(string dataSource)
    {
        var ds = dataSource.Trim();
        var prefix = Protocol.Match(ds);
        var rest = prefix.Success ? ds[prefix.Length..] : ds;
        var portFixed = prefix.Success && prefix.Value.ToLowerInvariant() is "np:" or "lpc:"
            || rest.StartsWith("(localdb)", StringComparison.OrdinalIgnoreCase);
        if (portFixed) return (ds, null, true);
        var comma = rest.LastIndexOf(',');
        if (comma > 0 && int.TryParse(rest[(comma + 1)..].Trim(), out var port))
            return ((prefix.Success ? prefix.Value : "") + rest[..comma].Trim(), port, false);
        return (ds, null, false);
    }

    /// <summary>
    /// The same server however it is written: no tcp: prefix, lower case,
    /// the default port filled in where no instance name stands in for it.
    /// </summary>
    private static string SqlServerKey(string dataSource)
    {
        var (server, port, portFixed) = SplitDataSource(dataSource);
        if (portFixed) return server.ToLowerInvariant();
        var host = Protocol.Replace(server, "").Trim().ToLowerInvariant();
        return $"{host}:{port?.ToString() ?? (host.Contains('\\') ? "" : "1433")}";
    }

    private static DatabaseConnectionView SqlServerView(string connectionString)
    {
        var b = new SqlConnectionStringBuilder(connectionString);
        var (server, port, portFixed) = SplitDataSource(b.DataSource ?? "");
        var advanced = b.Authentication is SqlAuthenticationMethod.NotSpecified or SqlAuthenticationMethod.SqlPassword
            ? null
            : $"Authentication={b.Authentication}";
        return new DatabaseConnectionView
        {
            Provider = DatabaseProviders.Name(DatabaseProvider.SqlServer),
            Server = server,
            Port = port,
            Database = b.InitialCatalog ?? "",
            Authentication = b.IntegratedSecurity ? Windows : Sql,
            UserId = b.IntegratedSecurity ? "" : b.UserID ?? "",
            PasswordSet = !b.IntegratedSecurity && !string.IsNullOrEmpty(b.Password),
            Encrypt = !b.Encrypt.Equals(SqlConnectionEncryptOption.Optional),
            TrustServerCertificate = b.TrustServerCertificate,
            PortFixed = portFixed,
            Advanced = advanced,
        };
    }

    /// <summary>Settings that belong to one server and would mislead the client about another.</summary>
    private static readonly string[] SqlServerBound =
        ["Failover Partner", "Failover Partner SPN", "Host Name In Certificate", "Server Certificate", "Server SPN"];

    private static string? BuildSqlServer(DatabaseConnectionFields f, string? current, out string connectionString)
    {
        connectionString = "";
        var b = current is null ? new SqlConnectionStringBuilder() : new SqlConnectionStringBuilder(current);
        var before = current is null ? null : SqlServerView(current);

        var (_, _, portFixed) = SplitDataSource(f.Server!);
        if (portFixed && f.Port is not null)
            return "A named pipe, shared memory or LocalDB address takes no port — leave the port blank.";
        var dataSource = f.Port is { } port ? $"{f.Server},{port}" : f.Server!;
        var sameServer = current is not null && SqlServerKey(dataSource) == SqlServerKey(b.DataSource ?? "");
        if (before?.Advanced is { } advanced && f.Authentication != before.Authentication)
            return $"This connection signs in with {advanced}, which these fields cannot change; keep its authentication as it is.";

        var sameUser = before is not null && before.Authentication == Sql && f.Authentication == Sql
            && string.Equals(before.UserId, f.UserId?.Trim(), StringComparison.OrdinalIgnoreCase);
        var keptPassword = sameServer && sameUser && before!.PasswordSet ? b.Password : null;

        if (!sameServer)
            foreach (var keyword in SqlServerBound)
                b.Remove(keyword);
        b.DataSource = dataSource;
        b.InitialCatalog = f.Database!;

        if (f.Authentication == Windows)
        {
            b.Remove("User ID");
            b.Remove("Password");
            b.IntegratedSecurity = true;
        }
        else
        {
            var password = string.IsNullOrEmpty(f.Password) ? keptPassword : f.Password;
            if (string.IsNullOrEmpty(password))
                return before?.PasswordSet == true
                    ? "Enter the password: a blank one keeps the current password only while the server and the user stay the same."
                    : "Enter the password for that login.";
            b.Remove("Integrated Security");
            b.UserID = f.UserId!.Trim();
            b.Password = password;
        }

        var encryptNow = !b.Encrypt.Equals(SqlConnectionEncryptOption.Optional);
        if (encryptNow != f.Encrypt)
            b.Encrypt = f.Encrypt ? SqlConnectionEncryptOption.Mandatory : SqlConnectionEncryptOption.Optional;
        if (b.TrustServerCertificate != f.TrustServerCertificate)
        {
            if (f.TrustServerCertificate) b.TrustServerCertificate = true;
            else b.Remove("Trust Server Certificate");
        }
        connectionString = b.ConnectionString;
        return null;
    }

    // ---- PostgreSQL ---------------------------------------------------------

    private static DatabaseConnectionView PostgresView(string connectionString)
    {
        var b = new NpgsqlConnectionStringBuilder(connectionString);
        var host = b.Host ?? "";
        var multi = host.Contains(',');
        return new DatabaseConnectionView
        {
            Provider = DatabaseProviders.Name(DatabaseProvider.Postgres),
            Server = host,
            Port = !multi && b.ContainsKey("Port") ? b.Port : null,
            Database = b.Database ?? "",
            Authentication = Sql,
            UserId = b.Username ?? "",
            PasswordSet = !string.IsNullOrEmpty(b.Password),
            Encrypt = b.SslMode >= SslMode.Require,
            TrustServerCertificate = b.SslMode == SslMode.Require,
            PortFixed = multi,
            Advanced = null,
        };
    }

    private static string PostgresKey(string host, int port) => $"{host.Trim().ToLowerInvariant()}:{port}";

    private static string? BuildPostgres(DatabaseConnectionFields f, string? current, out string connectionString)
    {
        connectionString = "";
        if (f.Authentication == Windows)
            return "Windows authentication is for SQL Server; PostgreSQL signs in with a user and password.";
        var b = current is null ? new NpgsqlConnectionStringBuilder() : new NpgsqlConnectionStringBuilder(current);
        var before = current is null ? null : PostgresView(current);
        if (f.Server!.Contains(',') && f.Port is not null)
            return "Several hosts carry their own ports (host1:5432,host2:5432) — leave the port blank.";

        var sameServer = current is not null
            && PostgresKey(f.Server, f.Port ?? 5432) == PostgresKey(b.Host ?? "", b.Port);
        var sameUser = before is not null && string.Equals(before.UserId, f.UserId?.Trim(), StringComparison.Ordinal);
        var keptPassword = sameServer && sameUser && before!.PasswordSet ? b.Password : null;

        if (!sameServer)
        {
            b.Remove("Root Certificate");
            b.Remove("Target Session Attributes");
        }
        b.Host = f.Server;
        if (f.Port is { } port) b.Port = port;
        else b.Remove("Port");
        b.Database = f.Database;
        b.Username = f.UserId!.Trim();
        var password = string.IsNullOrEmpty(f.Password) ? keptPassword : f.Password;
        if (string.IsNullOrEmpty(password))
            return before?.PasswordSet == true
                ? "Enter the password: a blank one keeps the current password only while the server and the user stay the same."
                : "Enter the password for that user.";
        b.Password = password;

        // Require encrypts without checking the certificate; the Verify modes
        // check it. A stricter mode already chosen stays while its box is ticked.
        var now = b.SslMode;
        var wanted = !f.Encrypt
            ? (now <= SslMode.Prefer ? now : SslMode.Prefer)
            : f.TrustServerCertificate
                ? SslMode.Require
                : (now is SslMode.VerifyCA or SslMode.VerifyFull ? now : SslMode.VerifyFull);
        if (wanted != now) b.SslMode = wanted;
        connectionString = b.ConnectionString;
        return null;
    }
}
