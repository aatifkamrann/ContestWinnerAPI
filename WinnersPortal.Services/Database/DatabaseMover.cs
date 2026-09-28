using System.Linq.Expressions;
using System.Reflection;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Services.Common;

namespace WinnersPortal.Services.Database;

/// <summary>
/// Moves the portal to another database: the schema made on the target,
/// every table copied in dependency order inside one transaction, the
/// counts checked, the choice written beside the keys, and the process
/// ended so the service manager brings it back on the new database. The
/// source is only ever read. While the copy runs the portal is paused —
/// the workers skip their cycles and the gate refuses writes — because a
/// row written to the source after its table was copied would be lost.
/// One move at a time; a failure rolls the target back, lifts the pause
/// and leaves the reason on the state for the page to show.
/// </summary>
public sealed class DatabaseMover(
    IServiceScopeFactory scopes,
    DatabaseSelection current,
    DatabaseMoveState state,
    AppPause pause,
    IDataProtectionProvider protection,
    IHostApplicationLifetime lifetime,
    ILogger<DatabaseMover> log)
{
    public const int BatchSize = 500;
    public static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Starts the move in the background; the answer is the state to poll.</summary>
    public Outcome<DatabaseMoveResponse> Start(DatabaseProvider target, string connectionString, string startedBy)
    {
        var (server, database) = DatabaseOverrideFile.Describe(target, connectionString);
        if (!state.TryBegin(target, server, database, startedBy))
            return Outcome.Conflict(state.RestartRequested
                ? "A move has already recorded its database; restart the portal to come up on it."
                : "A move is already running.");
        _ = Task.Run(() => RunAsync(target, connectionString, startedBy));
        return Outcome.Ok(state.Snapshot());
    }

    private async Task RunAsync(DatabaseProvider target, string connectionString, string startedBy)
    {
        var ct = lifetime.ApplicationStopping;
        var paused = false;
        try
        {
            // 1. The target, before anything is touched.
            var probe = await DatabaseProbe.TestAsync(target, connectionString, current, ct);
            if (probe.Problem is not null) throw new InvalidOperationException(probe.Problem);

            // 2. Nothing writes from here on.
            state.SetPhase(MovePhase.Pausing);
            pause.Pause("The portal is moving to another database and will restart in a moment.");
            paused = true;
            if (!await pause.DrainAsync(DrainTimeout, ct))
                throw new InvalidOperationException("A background worker did not finish its cycle in thirty seconds; nothing was moved.");

            // 3. The schema on the target, and on SQL Server the procedures.
            state.SetPhase(MovePhase.Preparing);
            await using var tgt = new AppDbContext(AppDbContextOptions.Build(target, connectionString));
            if (target == DatabaseProvider.SqlServer) await SqlServerRequirements.CheckAsync(tgt, ct);
            await tgt.Database.MigrateAsync(ct);
            await StoredProcedures.ApplyAsync(tgt, ct);

            // 4. Every table, parents first, in one transaction.
            state.SetPhase(MovePhase.Copying);
            using var scope = scopes.CreateScope();
            var src = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AssertCopyable(src.Model);
            var order = CopyOrder(src.Model);
            state.SetTables(order.Count);
            await using (var tx = await tgt.Database.BeginTransactionAsync(ct))
            {
                tgt.ChangeTracker.AutoDetectChangesEnabled = false;
                foreach (var entityType in order)
                {
                    state.TableStarted(entityType.GetTableName()!);
                    await (Task)CopyMethod.MakeGenericMethod(entityType.ClrType)
                        .Invoke(this, [src, tgt, entityType, ct])!;
                    state.TableDone();
                }
                if (target == DatabaseProvider.Postgres) await ResetSequencesAsync(tgt, src.Model, ct);

                // 5. The counts, table by table, before the commit.
                state.SetPhase(MovePhase.Verifying);
                foreach (var entityType in order)
                {
                    var expected = await (Task<long>)CountMethod.MakeGenericMethod(entityType.ClrType).Invoke(null, [src, ct])!;
                    var actual = await (Task<long>)CountMethod.MakeGenericMethod(entityType.ClrType).Invoke(null, [tgt, ct])!;
                    if (expected != actual)
                        throw new InvalidOperationException(
                            $"{entityType.GetTableName()} has {actual} rows on the target and {expected} on the source; nothing was kept.");
                }
                await tx.CommitAsync(ct);
            }

            // 6. The choice, written beside the keys.
            state.SetPhase(MovePhase.Recording);
            var (server, database) = DatabaseOverrideFile.Describe(target, connectionString);
            DatabaseOverrideFile.Write(current.OverridePath, new DatabaseOverride(
                target, server, database, connectionString, DateTimeOffset.UtcNow, startedBy),
                protection.CreateProtector(DatabaseOverrideFile.Purpose));
            state.Recorded();
            log.LogWarning("Database moved to {Provider} at {Server}/{Database} by {By}; restarting to come up on it.",
                DatabaseProviders.Name(target), server, database, startedBy);

            // 7. The process ends; the service manager brings it back. A
            // moment first, so the page's last poll reads "restarting".
            await Task.Delay(1500, ct);
            lifetime.StopApplication();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Never the connection string: the message names tables and
            // servers, and a driver's message can carry more than that.
            log.LogError(e, "The database move failed.");
            state.Fail(Scrub(e.Message, connectionString));
            if (paused) pause.Resume();
        }
    }

    // ------------------------------------------------------------ the copy

    private static readonly MethodInfo CopyMethod =
        typeof(DatabaseMover).GetMethod(nameof(CopyTableAsync), BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly MethodInfo CountMethod =
        typeof(DatabaseMover).GetMethod(nameof(CountAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    private async Task CopyTableAsync<T>(AppDbContext src, AppDbContext tgt, IEntityType entityType, CancellationToken ct)
        where T : class
    {
        var falses = DefaultedBools(src.Model).Where(d => d.EntityType == entityType).Select(d => d.Property).ToList();
        var falseKeys = falses.ToDictionary(p => p, _ => new List<Guid>());
        var keyReader = falses.Count > 0 ? KeyReader(entityType) : null;
        var query = OrderByKey(src.Set<T>().AsNoTracking().IgnoreQueryFilters(), entityType);

        // A table whose key the database counts takes the copied ids as they
        // are: SQL Server only with IDENTITY_INSERT on, which is a setting of
        // the session — the transaction holds that one connection open, so
        // it lasts across the batches. Postgres takes them as given and is
        // re-seeded after the copy instead. A session setting cannot be a
        // procedure's; the name it takes is the model's, delimited by the
        // provider, never a value from outside the process.
        var table = entityType.GetTableName()!;
        var identityInsert = tgt.IsSqlServer && IdentityTables(src.Model).Contains(table);
        var quoted = tgt.GetService<ISqlGenerationHelper>().DelimitIdentifier(table);
        if (identityInsert) await tgt.Database.ExecuteSqlRawAsync("SET IDENTITY_INSERT " + quoted + " ON", ct);
        try
        {
            await CopyBatchesAsync(tgt, query, falses, falseKeys, keyReader, ct);
        }
        finally
        {
            if (identityInsert) await tgt.Database.ExecuteSqlRawAsync("SET IDENTITY_INSERT " + quoted + " OFF", ct);
        }
    }

    private async Task CopyBatchesAsync<T>(
        AppDbContext tgt, IQueryable<T> query, List<IProperty> falses, Dictionary<IProperty, List<Guid>> falseKeys,
        Func<object, Guid>? keyReader, CancellationToken ct)
        where T : class
    {
        for (var offset = 0; ; offset += BatchSize)
        {
            var batch = await query.Skip(offset).Take(BatchSize).ToListAsync(ct);
            if (batch.Count == 0) break;
            // A false in a column whose default is true would be "not set"
            // to EF and come out true; those rows are noted and put right.
            foreach (var row in batch)
                foreach (var p in falses)
                    if (p.PropertyInfo!.GetValue(row) is false)
                        falseKeys[p].Add(keyReader!(row));
            tgt.AddRange(batch);
            await tgt.SaveChangesAsync(ct);
            tgt.ChangeTracker.Clear();
            state.RowsAdded(batch.Count);
            foreach (var (p, keys) in falseKeys)
            {
                if (keys.Count == 0) continue;
                await RestoreFalseAsync<T>(tgt, p, keys, ct);
                keys.Clear();
            }
            if (batch.Count < BatchSize) break;
        }
    }

    private static Task<long> CountAsync<T>(AppDbContext db, CancellationToken ct) where T : class =>
        db.Set<T>().IgnoreQueryFilters().LongCountAsync(ct);

    /// <summary>Ordered by the key, so paging is stable: EF.Property per key column, built for the key's own types.</summary>
    public static IQueryable<T> OrderByKey<T>(IQueryable<T> query, IEntityType entityType) where T : class
    {
        IOrderedQueryable<T>? ordered = null;
        foreach (var key in entityType.FindPrimaryKey()!.Properties)
        {
            var param = Expression.Parameter(typeof(T), "e");
            var access = Expression.Call(
                typeof(EF), nameof(EF.Property), [key.ClrType], param, Expression.Constant(key.Name));
            var lambda = Expression.Lambda(access, param);
            var method = (ordered is null ? nameof(Queryable.OrderBy) : nameof(Queryable.ThenBy));
            var call = Expression.Call(typeof(Queryable), method, [typeof(T), key.ClrType],
                ordered is null ? query.Expression : ordered.Expression, Expression.Quote(lambda));
            ordered = (IOrderedQueryable<T>)query.Provider.CreateQuery<T>(call);
        }
        return ordered ?? query;
    }

    private static async Task RestoreFalseAsync<T>(AppDbContext tgt, IProperty property, List<Guid> keys, CancellationToken ct)
        where T : class
    {
        var name = property.Name;
        await tgt.Set<T>()
            .Where(e => keys.Contains(EF.Property<Guid>(e, "Id")))
            .ExecuteUpdateAsync(s => s.SetProperty(e => EF.Property<bool>(e, name), false), ct);
    }

    private static Func<object, Guid> KeyReader(IEntityType entityType)
    {
        var key = entityType.FindPrimaryKey()!.Properties.Single();
        if (key.ClrType != typeof(Guid))
            throw new InvalidOperationException($"{entityType.GetTableName()} has a defaulted boolean but no Guid key; the mover cannot restore it.");
        return row => (Guid)key.PropertyInfo!.GetValue(row)!;
    }

    /// <summary>Postgres identity columns count from where the copied ids stop, or the next insert collides. The table names are the model's, delimited by the provider.</summary>
    private static async Task ResetSequencesAsync(AppDbContext tgt, IModel model, CancellationToken ct)
    {
        var helper = tgt.GetService<ISqlGenerationHelper>();
        foreach (var table in IdentityTables(model))
        {
            var quoted = helper.DelimitIdentifier(table);
            // Identifiers cannot be parameters, and these come from the model
            // (never from input) through the provider's delimiter.
#pragma warning disable EF1002
            await tgt.Database.ExecuteSqlRawAsync(
                $"SELECT setval(pg_get_serial_sequence('{quoted}', 'Id'), COALESCE((SELECT max(\"Id\") FROM {quoted}), 0) + 1, false)", ct);
#pragma warning restore EF1002
        }
    }

    // ----------------------------------------------------------- the model

    /// <summary>Every entity type, each after every type it references: Kahn's sort, ties by table name.</summary>
    public static IReadOnlyList<IEntityType> CopyOrder(IModel model)
    {
        var types = model.GetEntityTypes().Where(t => !t.IsOwned()).ToList();
        var remaining = types.ToDictionary(t => t, t => t.GetForeignKeys()
            .Select(fk => fk.PrincipalEntityType)
            .Where(p => p != t)
            .Distinct()
            .ToHashSet());
        foreach (var t in types)
            if (t.GetForeignKeys().Any(fk => fk.PrincipalEntityType == t))
                throw new InvalidOperationException($"The copy order cannot place {t.GetTableName()}: it references itself.");
        var order = new List<IEntityType>();
        while (remaining.Count > 0)
        {
            var ready = remaining.Where(kv => kv.Value.Count == 0).Select(kv => kv.Key)
                .OrderBy(t => t.GetTableName(), StringComparer.Ordinal).ToList();
            if (ready.Count == 0)
                throw new InvalidOperationException("The copy order cannot place: " + string.Join(", ", remaining.Keys.Select(t => t.GetTableName())));
            foreach (var t in ready)
            {
                order.Add(t);
                remaining.Remove(t);
                foreach (var deps in remaining.Values) deps.Remove(t);
            }
        }
        return order;
    }

    /// <summary>Tables whose key the database counts: copied with their ids, then re-seeded on Postgres.</summary>
    public static IReadOnlyList<string> IdentityTables(IModel model) =>
        model.GetEntityTypes()
            .Where(t => t.FindPrimaryKey()!.Properties is [{ ValueGenerated: ValueGenerated.OnAdd } p]
                && (p.ClrType == typeof(long) || p.ClrType == typeof(int)))
            .Select(t => t.GetTableName()!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

    /// <summary>Booleans whose column default is true: a copied false has to be written twice.</summary>
    public static IReadOnlyList<(IEntityType EntityType, IProperty Property)> DefaultedBools(IModel model) =>
        model.GetEntityTypes()
            .SelectMany(t => t.GetProperties().Where(p => p.ClrType == typeof(bool) && p.GetDefaultValue() is true)
                .Select(p => (t, p)))
            .ToList();

    /// <summary>
    /// A shadow property the entity does not carry would be lost in the
    /// copy — its value is not on the object EF loads. The one there is,
    /// the Postgres search column, is generated by the database on write;
    /// anything else added later fails here before a row is moved.
    /// </summary>
    public static void AssertCopyable(IModel model)
    {
        var shadows = model.GetEntityTypes()
            .SelectMany(t => t.GetProperties().Where(p => p.IsShadowProperty() && p.Name != "SearchVector")
                .Select(p => t.GetTableName() + "." + p.Name))
            .ToList();
        if (shadows.Count > 0)
            throw new InvalidOperationException("The mover cannot carry shadow columns: " + string.Join(", ", shadows));
    }

    private static string Scrub(string message, string connectionString) =>
        message.Replace(connectionString, "[connection string]", StringComparison.OrdinalIgnoreCase);
}
