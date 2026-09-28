using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Npgsql;
using WinnersPortal.Infrastructure.Data;

namespace WinnersPortal.Services.Database;

/// <summary>Where the choice of database came from.</summary>
public enum DatabaseSource
{
    /// <summary>The file an in-app move wrote beside the keys.</summary>
    Override,
    /// <summary>DATABASE__PROVIDER and ConnectionStrings__Db.</summary>
    Environment,
    /// <summary>Nothing set: the development fallback to the stock compose database.</summary>
    Default,
}

/// <summary>
/// The database this process runs on, decided once before the host is
/// built, since the settings table is behind the very connection being
/// chosen. Precedence: the override file an in-app move wrote, then the
/// environment, then — in development only — the stock compose string
/// for the provider asked for, SQL Server unless it says postgres.
/// </summary>
public sealed record DatabaseSelection(
    DatabaseProvider Provider, string ConnectionString, DatabaseSource Source, string OverridePath, string KeysDir)
{
    /// <summary>The compose mssql service as a bare <c>dotnet run</c> reaches it, with the compose file's development password.</summary>
    public const string SqlServerDevelopmentFallback =
        "Server=localhost,1433;Database=winnersportal;User Id=sa;Password=WinnersPortal-Dev-1;TrustServerCertificate=True";

    /// <summary>The compose db service (profile postgres) as a bare <c>dotnet run</c> reaches it.</summary>
    public const string PostgresDevelopmentFallback =
        "Host=localhost;Port=5432;Database=winnersportal;Username=winnersportal;Password=winnersportal";

    public static string DevelopmentFallback(DatabaseProvider provider) =>
        provider == DatabaseProvider.SqlServer ? SqlServerDevelopmentFallback : PostgresDevelopmentFallback;

    public static DatabaseSelection Resolve(
        IConfiguration config, string keysDir, bool isDevelopment, Func<IDataProtector> protector, Action<string>? warn = null)
    {
        var overridePath = DatabaseOverrideFile.PathIn(keysDir);
        var rawProvider = config[DatabaseProviders.ConfigKey];
        var envProvider = DatabaseProviders.Parse(rawProvider);
        var envString = config.GetConnectionString("Db");

        // The protector is built only when there is a file to read: a fresh
        // install must not create a key before the host has its ring.
        if (File.Exists(overridePath))
        {
            var o = DatabaseOverrideFile.Read(overridePath, protector())!;
            if (!string.IsNullOrWhiteSpace(envString) && envProvider != o.Provider)
                warn?.Invoke($"DATABASE__PROVIDER says {DatabaseProviders.Name(envProvider)} but {overridePath} says "
                    + $"{DatabaseProviders.Name(o.Provider)} ({o.Server}/{o.Database}); the file wins, as the last move chose it.");
            return new DatabaseSelection(o.Provider, o.ConnectionString, DatabaseSource.Override, overridePath, keysDir);
        }

        if (!string.IsNullOrWhiteSpace(envString))
        {
            // Until SQL Server became the default a blank provider meant
            // PostgreSQL, so an install from then names nothing but its
            // connection string. A blank beside a string only PostgreSQL can
            // read is that install: it stays on its data, and says so.
            if (string.IsNullOrWhiteSpace(rawProvider) && IsPostgresOnly(envString))
            {
                warn?.Invoke("DATABASE__PROVIDER is unset, which means sqlserver, but ConnectionStrings__Db is a "
                    + "PostgreSQL connection string; running on PostgreSQL. Set DATABASE__PROVIDER=postgres to say so.");
                return new DatabaseSelection(DatabaseProvider.Postgres, envString, DatabaseSource.Environment, overridePath, keysDir);
            }
            return new DatabaseSelection(envProvider, envString, DatabaseSource.Environment, overridePath, keysDir);
        }

        if (!isDevelopment)
            throw new InvalidOperationException(
                "No database connection string: set the ConnectionStrings__Db environment variable "
                + "(docker-compose.yml for the container, deploy/windows/winnersportal-api.xml for the Windows service).");
        return new DatabaseSelection(envProvider, DevelopmentFallback(envProvider), DatabaseSource.Default, overridePath, keysDir);
    }

    /// <summary>
    /// True for a string SQL Server's client refuses — Host=, Port= and
    /// Username= are not its words — and PostgreSQL's accepts.
    /// </summary>
    public static bool IsPostgresOnly(string connectionString)
    {
        try
        {
            _ = new SqlConnectionStringBuilder(connectionString);
            return false;
        }
        catch (Exception e) when (e is ArgumentException or FormatException or KeyNotFoundException)
        {
        }
        try
        {
            _ = new NpgsqlConnectionStringBuilder(connectionString);
            return true;
        }
        catch (Exception e) when (e is ArgumentException or FormatException or KeyNotFoundException)
        {
            return false;
        }
    }

    /// <summary>The server and database, for the screen; never the password.</summary>
    public (string Server, string Database) Describe() => DatabaseOverrideFile.Describe(Provider, ConnectionString);
}
