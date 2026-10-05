using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using WinnersPortal.Services.Ai;
using WinnersPortal.Services.Database;
using WinnersPortal.Services.Settings;

namespace WinnersPortal.AiEvals;

/// <summary>
/// Reads Settings → AI evals from the portal this machine runs: the keys
/// folder the API uses (<c>--keys</c>, else <c>DP_KEYS_DIR</c>, else the API
/// project's own <c>keys/</c>, where a run from Visual Studio or
/// <c>dotnet run</c> keeps it) and the database it names — the
/// <c>database.json</c> saved there by the connect page, else
/// <c>ConnectionStrings__Db</c> and <c>DATABASE__PROVIDER</c>, the API's
/// own order. Reading never writes: the ring is opened with key
/// generation off, so a folder with no keys is refused rather than given
/// one the API never made.
/// </summary>
public static class EvalPortal
{
    /// <summary>Where the portal's keys are, from the run's flag, the environment or the API project.</summary>
    public static string KeysDir(string? given) =>
        !string.IsNullOrWhiteSpace(given) ? given
        : Environment.GetEnvironmentVariable("DP_KEYS_DIR") is { Length: > 0 } fromEnv ? fromEnv
        : Path.Combine(EvalCases.RepoRoot(), "src", "WinnersPortal.Api", "keys");

    /// <summary>
    /// The saved pair, or null with the reason it could not be read — never
    /// an exception: a run without the portal falls back to the variables.
    /// </summary>
    public static async Task<(EvalSettings.Saved? Saved, string Where)> ReadAsync(string keysDir, CancellationToken ct)
    {
        if (!Directory.Exists(keysDir) || !Directory.EnumerateFiles(keysDir, "key-*.xml").Any())
            return (null, $"no key ring in {keysDir}");

        var ring = DataProtectionProvider.Create(new DirectoryInfo(keysDir),
            b => b.SetApplicationName("WinnersPortal").DisableAutomaticKeyGeneration());
        var config = new ConfigurationBuilder().AddEnvironmentVariables().Build();

        DatabaseSelection? database;
        try
        {
            database = DatabaseSelection.Resolve(config, keysDir, () => ring.CreateProtector(DatabaseOverrideFile.Purpose));
        }
        catch (Exception e)
        {
            return (null, $"the database saved in {keysDir} could not be read ({e.GetType().Name})");
        }
        if (database is null)
            return (null, $"no database is named: no {Path.GetFileName(DatabaseOverrideFile.PathIn(keysDir))} in {keysDir}, and ConnectionStrings__Db is not set");

        // Named by server and database, never by the string: it may carry a password.
        var where = Describe(database);
        try
        {
            var saved = await EvalSettings.ReadAsync(
                database.Provider, database.ConnectionString, ring.CreateProtector(SettingsService.ProtectorPurpose), ct);
            return (saved, where);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // EF wraps a network failure in a hint about retries; the reason is at the bottom.
            var root = e.GetBaseException();
            var first = root.Message.Split('\n')[0].Trim();
            return (null, $"{where} could not be read ({root.GetType().Name}: {first})");
        }
    }

    /// <summary>"the portal's sqlserver database winnersportal on mssql (from keys/database.json)" — no user, no password.</summary>
    private static string Describe(DatabaseSelection database)
    {
        var from = database.Source == DatabaseSource.Override
            ? Path.GetFileName(database.OverridePath)
            : "ConnectionStrings__Db";
        try
        {
            var view = DatabaseConnections.View(database.Provider, database.ConnectionString);
            var server = view.Port is { } port ? $"{view.Server}:{port}" : view.Server;
            return $"the portal's {view.Provider} database {view.Database} on {server} (from {from})";
        }
        catch (Exception)
        {
            return $"the portal's {database.Provider} database (from {from})";
        }
    }
}
