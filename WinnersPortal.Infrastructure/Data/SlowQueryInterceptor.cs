using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace WinnersPortal.Infrastructure.Data;

/// <summary>
/// Logs every database command that runs as long as the threshold in
/// <see cref="SlowQueryStats"/> or longer — the <c>limits.slowQueryMs</c>
/// setting, 0 for off, changeable while the process runs: how long,
/// how many rows, what issued it (a query, SaveChanges, raw SQL), the
/// request it ran for, and the SQL itself — placeholders only, never the
/// parameter values, so no member's data reaches the log. These are the
/// numbers to decide from before any query is hand-tuned or rewritten: one
/// that never shows up here is not worth touching. Each one is also tallied
/// in <see cref="SlowQueryStats"/>, which the operations screen shows.
/// A command that served no request — the workers' — is left out of both
/// while the <c>limits.slowQueryBackground</c> switch is off.
/// </summary>
/// <remarks>
/// A query that returns rows is timed from the command's start to its
/// reader's disposal, so the time spent reading and materialising a large
/// result counts too; statements without a reader are timed to their
/// completion. Works the same on both databases.
/// </remarks>
public sealed class SlowQueryInterceptor(SlowQueryStats stats, ILogger<SlowQueryInterceptor> logger, Func<string?> during)
    : DbCommandInterceptor
{
    /// <summary>SQL longer than this is cut; the start names the tables and the shape.</summary>
    public const int MaxSqlLength = 4000;

    /// <summary>What the log and the tally say a command ran during when it served no request.</summary>
    public const string BackgroundWork = "background work";

    /// <summary>
    /// Logs one finished command if it crossed the threshold in force, and
    /// served a request or background work is counted; answers whether it did.
    /// </summary>
    public bool Note(TimeSpan duration, long? rows, string source, string sql)
    {
        var thresholdMs = stats.ThresholdMs;
        if (thresholdMs == 0 || duration.TotalMilliseconds < thresholdMs) return false;
        var request = during();
        if (request is null && !stats.IncludesBackground) return false;
        var text = sql.Length > MaxSqlLength ? sql[..MaxSqlLength] + " …" : sql;
        var where = request ?? BackgroundWork;
        stats.Record(text, source, (long)duration.TotalMilliseconds, rows, where);
        logger.LogWarning(
            "Slow query: {ElapsedMs} ms, {Rows} rows, {Source}, during {During} (threshold {ThresholdMs} ms){NewLine}{Sql}",
            (long)duration.TotalMilliseconds, rows?.ToString() ?? "n/a", source, where,
            thresholdMs, Environment.NewLine, text);
        return true;
    }

    // The disposing event, which times a reader to its end, carries no
    // command source — and a reader is not always a query: on Postgres every
    // insert SaveChanges makes reads its new ids back through one. So the
    // source is kept from the executed event, held only as long as the
    // command itself lives.
    private readonly ConditionalWeakTable<DbCommand, StrongBox<CommandSource>> _readerSources = new();

    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    {
        _readerSources.AddOrUpdate(command, new StrongBox<CommandSource>(eventData.CommandSource));
        return result;
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
    {
        _readerSources.AddOrUpdate(command, new StrongBox<CommandSource>(eventData.CommandSource));
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult DataReaderDisposing(
        DbCommand command, DataReaderDisposingEventData eventData, InterceptionResult result)
    {
        var source = _readerSources.TryGetValue(command, out var box) ? ReaderSource(box.Value) : "Query";
        _readerSources.Remove(command);
        Note(eventData.Duration, eventData.ReadCount, source, command.CommandText);
        return result;
    }

    /// <summary>A LINQ query's reader is simply a query; anything else keeps EF's name for what issued it.</summary>
    public static string ReaderSource(CommandSource source) =>
        source is CommandSource.LinqQuery or CommandSource.Unknown ? "Query" : source.ToString();

    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        Note(eventData.Duration, result < 0 ? null : result, eventData.CommandSource.ToString(), command.CommandText);
        return result;
    }

    public override ValueTask<int> NonQueryExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Note(eventData.Duration, result < 0 ? null : result, eventData.CommandSource.ToString(), command.CommandText);
        return ValueTask.FromResult(result);
    }

    public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
    {
        Note(eventData.Duration, null, eventData.CommandSource.ToString(), command.CommandText);
        return result;
    }

    public override ValueTask<object?> ScalarExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, object? result, CancellationToken cancellationToken = default)
    {
        Note(eventData.Duration, null, eventData.CommandSource.ToString(), command.CommandText);
        return ValueTask.FromResult(result);
    }
}
