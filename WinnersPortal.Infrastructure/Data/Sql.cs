using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace WinnersPortal.Infrastructure.Data;

/// <summary>
/// Dapper on the context's own connection. On SQL Server the day-to-day
/// reads and set-based writes are stored procedures, kept beside the LINQ
/// that serves Postgres, and every one of them runs through here: the
/// connection is the one EF Core opened, the transaction is EF Core's
/// current one if a service began one, so a request that mixes the two
/// never holds two connections or splits its work across two transactions.
/// A command is a <see cref="Procedure"/> and its parameters — there is
/// no way to hand this class SQL text, so nothing a caller supplies is
/// ever part of a statement; it is bound, typed, as a parameter. Each
/// command is timed and, past the threshold, reported to the same
/// slow-query log and tally as EF Core's commands, under the source
/// <c>Dapper</c>, as the procedure's name.
/// </summary>
/// <remarks>
/// The rules the procedures follow are in the technical reference: one
/// script each under <c>Data/Procedures</c>, every value a parameter,
/// identifiers bracketed, every statement that names <c>[Profiles]</c>
/// carries <c>[IsDeleted] = 0</c> because the global query filter EF Core
/// applies is not applied there, a set of ids travels as JSON through
/// <c>OPENJSON</c> rather than <c>IN @ids</c>, so the 2,100-parameter
/// limit is never met, and no procedure builds SQL of its own.
/// </remarks>
public sealed class Sql(AppDbContext db, SlowQueryInterceptor? slow)
{
    /// <summary>What the slow-query log and tally call a command that ran through here.</summary>
    public const string Source = "Dapper";

    private DbConnection Connection => db.Database.GetDbConnection();

    private DbTransaction? Transaction => db.Database.CurrentTransaction?.GetDbTransaction();

    /// <summary>Every row, buffered.</summary>
    public async Task<List<T>> QueryAsync<T>(Procedure procedure, object? param, CancellationToken ct)
    {
        var (command, clock) = await StartAsync(procedure, param, ct);
        var rows = (await Connection.QueryAsync<T>(command)).AsList();
        Note(clock, rows.Count, procedure);
        return rows;
    }

    /// <summary>The first row, or default when there is none.</summary>
    public async Task<T?> FirstOrDefaultAsync<T>(Procedure procedure, object? param, CancellationToken ct)
    {
        var (command, clock) = await StartAsync(procedure, param, ct);
        var row = await Connection.QueryFirstOrDefaultAsync<T>(command);
        Note(clock, row is null ? 0 : 1, procedure);
        return row;
    }

    /// <summary>The one row, default when there is none; throws when there are more.</summary>
    public async Task<T?> SingleOrDefaultAsync<T>(Procedure procedure, object? param, CancellationToken ct)
    {
        var (command, clock) = await StartAsync(procedure, param, ct);
        var row = await Connection.QuerySingleOrDefaultAsync<T>(command);
        Note(clock, row is null ? 0 : 1, procedure);
        return row;
    }

    /// <summary>The first column of the first row.</summary>
    public async Task<T?> ScalarAsync<T>(Procedure procedure, object? param, CancellationToken ct)
    {
        var (command, clock) = await StartAsync(procedure, param, ct);
        var value = await Connection.ExecuteScalarAsync<T>(command);
        Note(clock, null, procedure);
        return value;
    }

    /// <summary>A procedure that returns no rows; answers how many its statements affected.</summary>
    public async Task<int> ExecuteAsync(Procedure procedure, object? param, CancellationToken ct)
    {
        var (command, clock) = await StartAsync(procedure, param, ct);
        var affected = await Connection.ExecuteAsync(command);
        Note(clock, affected < 0 ? null : affected, procedure);
        return affected;
    }

    /// <summary>
    /// Several result sets in one round trip — a procedure that batches
    /// its reads returns them together. <paramref name="read"/> drains the
    /// grid; the whole batch is timed to its end.
    /// </summary>
    public async Task<T> MultipleAsync<T>(Procedure procedure, object? param, Func<SqlMapper.GridReader, Task<T>> read, CancellationToken ct)
    {
        var (command, clock) = await StartAsync(procedure, param, ct);
        using var grid = await Connection.QueryMultipleAsync(command);
        var result = await read(grid);
        Note(clock, null, procedure);
        return result;
    }

    /// <summary>
    /// A JSON list column read back as text — the four list columns
    /// (<c>Profiles.SecondaryCategories</c> and its kin) come off the wire
    /// as their JSON, and this is the list EF Core would have given.
    /// </summary>
    public static List<T> JsonList<T>(string? json) =>
        string.IsNullOrEmpty(json) ? [] : JsonSerializer.Deserialize<List<T>>(json) ?? [];

    /// <summary>A set of ids as the JSON an <c>OPENJSON</c> clause reads.</summary>
    public static string JsonIds(IEnumerable<Guid> ids) => JsonSerializer.Serialize(ids);

    private async Task<(CommandDefinition Command, Stopwatch Clock)> StartAsync(Procedure procedure, object? param, CancellationToken ct)
    {
        // EF Core opens what it owns and closes it with the context; a
        // connection a service opened itself is left as found.
        await db.Database.OpenConnectionAsync(ct);
        var command = new CommandDefinition(
            procedure.QualifiedName, param, Transaction, commandType: CommandType.StoredProcedure, cancellationToken: ct);
        return (command, Stopwatch.StartNew());
    }

    private void Note(Stopwatch clock, long? rows, Procedure procedure) =>
        slow?.Note(clock.Elapsed, rows, Source, "EXEC " + procedure.QualifiedName);
}
