using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Npgsql;
using WinnersPortal.Infrastructure.Data;

namespace WinnersPortal.Services.Database;

/// <summary>
/// The database an in-app move chose, written beside the data-protection
/// keys: the one persisted, uncommitted, already-backed-up directory both
/// deployments have. The provider, server, database and user are plain, so
/// a backup script can read where the data is and whom to sign in as; the
/// connection string, with its password, is protected under the same key
/// ring the settings use.
/// </summary>
public sealed record DatabaseOverride(
    DatabaseProvider Provider, string Server, string Database, string ConnectionString,
    DateTimeOffset WrittenAtUtc, string WrittenBy);

/// <summary>A file that exists but cannot be read; the portal refuses to guess which database it meant.</summary>
public sealed class DatabaseOverrideException(string message) : Exception(message);

public static class DatabaseOverrideFile
{
    public const string FileName = "database.json";

    /// <summary>The data-protection purpose; the same string the host and the pre-host reader both derive from.</summary>
    public const string Purpose = "WinnersPortal.Database";

    public static string PathIn(string keysDir) => Path.Combine(keysDir, FileName);

    private const string DeleteToReturn =
        "Delete it to return to the database ConnectionStrings__Db names — or, with none, to the setup page that asks for one.";

    // Version 2 added UserName (null for Windows sign-in); a version 1 file
    // reads with none.
    private sealed record Stored(
        int Version, string Provider, string Server, string Database, string ConnectionString,
        DateTimeOffset WrittenAtUtc, string WrittenBy, string? UserName = null);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Null when there is no file. A file that will not decrypt throws, naming the file and the way out.</summary>
    public static DatabaseOverride? Read(string path, IDataProtector protector)
    {
        if (!File.Exists(path)) return null;
        Stored? stored;
        try
        {
            stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(path), Json);
        }
        catch (JsonException e)
        {
            throw new DatabaseOverrideException($"{path} is not the database override this portal writes ({e.Message}). " + DeleteToReturn);
        }
        if (stored is null || string.IsNullOrWhiteSpace(stored.ConnectionString))
            throw new DatabaseOverrideException($"{path} names no database. " + DeleteToReturn);
        string connectionString;
        try
        {
            connectionString = protector.Unprotect(stored.ConnectionString);
        }
        catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or FormatException)
        {
            // A keys directory restored from a different backup cannot read
            // it. Booting on the environment's database instead would split
            // the portal's data between two servers, so this is a stop.
            throw new DatabaseOverrideException(
                $"{path} was written under a data-protection key ring this portal no longer has, so the database it "
                + "names cannot be read. Restore the keys directory it was written with, or delete the file: the API "
                + "then starts on the database ConnectionStrings__Db names, or with none, on the setup page's connect step.");
        }
        return new DatabaseOverride(
            DatabaseProviders.Parse(stored.Provider), stored.Server, stored.Database, connectionString,
            stored.WrittenAtUtc, stored.WrittenBy);
    }

    /// <summary>Written whole to a sibling, then moved over: a crash mid-write leaves the old file, never half of the new.</summary>
    public static void Write(string path, DatabaseOverride o, IDataProtector protector)
    {
        var stored = new Stored(2, DatabaseProviders.Name(o.Provider), o.Server, o.Database,
            protector.Protect(o.ConnectionString), o.WrittenAtUtc, o.WrittenBy,
            DatabaseConnections.UserOf(o.Provider, o.ConnectionString));
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(stored, Json));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>The server and database a connection string names, for the file and the screen — never the password.</summary>
    public static (string Server, string Database) Describe(DatabaseProvider provider, string connectionString)
    {
        try
        {
            if (provider == DatabaseProvider.SqlServer)
            {
                var b = new SqlConnectionStringBuilder(connectionString);
                return (b.DataSource, b.InitialCatalog);
            }
            var pg = new NpgsqlConnectionStringBuilder(connectionString);
            return ($"{pg.Host}:{pg.Port}", pg.Database ?? "");
        }
        catch (ArgumentException)
        {
            return ("?", "?");
        }
    }
}
