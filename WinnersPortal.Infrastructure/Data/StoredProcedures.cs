using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.EntityFrameworkCore;

namespace WinnersPortal.Infrastructure.Data;

/// <summary>
/// The procedures' scripts, and their installation. Each script is an
/// embedded resource — <c>Data/Procedures/&lt;Name&gt;.sql</c>, one
/// <c>CREATE OR ALTER PROCEDURE</c> — and every one is run at each start
/// on SQL Server, after the migrations and before the first request, so
/// the procedures a build calls are the ones it shipped with: a changed
/// script takes effect at the next start, a new one exists before its
/// first call, and a database made by an older build catches up. The
/// scripts are not migrations: they hold no data and depend only on the
/// schema at the head of the tree, so they are applied whole every time
/// rather than versioned. One transaction under an application lock, so
/// two instances starting together do not alter the same procedure at
/// once.
/// </summary>
public static class StoredProcedures
{
    private const string ResourcePrefix = "WinnersPortal.Infrastructure.Data.Procedures.";

    /// <summary>The resource an installed procedure's definition is read from; a missing script is a build error.</summary>
    private static readonly ConcurrentDictionary<string, string> Definitions = new(StringComparer.Ordinal);

    /// <summary>The names of every script the assembly carries, whether or not a handle names it.</summary>
    public static IReadOnlyList<string> ScriptNames() =>
        typeof(StoredProcedures).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
            .Select(n => n[ResourcePrefix.Length..^4])
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

    public static string Definition(Procedure procedure) =>
        Definitions.GetOrAdd(procedure.Name, static name =>
        {
            var assembly = typeof(StoredProcedures).Assembly;
            using var stream = assembly.GetManifestResourceStream(ResourcePrefix + name + ".sql")
                ?? throw new InvalidOperationException($"The procedure {name} has no script under Data/Procedures.");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        });

    /// <summary>
    /// Installs or replaces every procedure on the context's SQL Server
    /// database. Called after <c>MigrateAsync</c> by the start-up and by
    /// the mover on its target; a no-op on Postgres, which runs the LINQ.
    /// </summary>
    public static async Task ApplyAsync(AppDbContext db, CancellationToken ct)
    {
        if (!db.IsSqlServer) return;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync(Lock, ct);
        foreach (var procedure in Procedures.All)
            await db.Database.ExecuteSqlRawAsync(procedure.Definition, ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// An exclusive lock held to the commit, so a second instance starting
    /// at the same moment waits its turn rather than altering a procedure
    /// under this one; a minute is longer than the whole install takes.
    /// </summary>
    private const string Lock = """
        DECLARE @held int;
        EXEC @held = sp_getapplock @Resource = N'WinnersPortal.Procedures', @LockMode = N'Exclusive',
                                   @LockOwner = N'Transaction', @LockTimeout = 60000;
        IF @held < 0 THROW 50000, N'Another portal instance is still installing the stored procedures.', 1;
        """;
}
