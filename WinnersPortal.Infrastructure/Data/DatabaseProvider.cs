namespace WinnersPortal.Infrastructure.Data;

/// <summary>The two databases the portal runs on. SQL Server 2022 or later is the default; PostgreSQL is the other.</summary>
public enum DatabaseProvider
{
    Postgres = 0,
    SqlServer = 1,
}

public static class DatabaseProviders
{
    /// <summary>The configuration key the choice is read from — <c>DATABASE__PROVIDER</c> in the environment.</summary>
    public const string ConfigKey = "Database:Provider";

    /// <summary>What a blank <c>DATABASE__PROVIDER</c> means.</summary>
    public const DatabaseProvider Default = DatabaseProvider.SqlServer;

    /// <summary>A blank means SQL Server; the aliases are the words people actually type.</summary>
    public static DatabaseProvider Parse(string? raw) => (raw ?? "").Trim().ToLowerInvariant() switch
    {
        "" => Default,
        "sqlserver" or "mssql" or "sql-server" or "sql server" => DatabaseProvider.SqlServer,
        "postgres" or "postgresql" or "npgsql" or "pg" => DatabaseProvider.Postgres,
        var other => throw new InvalidOperationException(
            $"Unknown database provider '{other}': DATABASE__PROVIDER takes sqlserver (the default) or postgres."),
    };

    /// <summary>The word the portal writes: postgres or sqlserver.</summary>
    public static string Name(DatabaseProvider provider) =>
        provider == DatabaseProvider.SqlServer ? "sqlserver" : "postgres";

    /// <summary>The name a person reads.</summary>
    public static string Label(DatabaseProvider provider) =>
        provider == DatabaseProvider.SqlServer ? "SQL Server" : "PostgreSQL";
}
