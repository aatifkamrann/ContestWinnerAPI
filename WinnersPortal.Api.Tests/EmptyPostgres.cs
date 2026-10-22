using System.Data;
using System.Data.Common;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.Internal;
using Microsoft.EntityFrameworkCore.Storage;
using WinnersPortal.Infrastructure.Data;

namespace WinnersPortal.Api.Tests;

#pragma warning disable EF1001 // QueryCompiler is EF's own; the stand-in only wraps it.

/// <summary>
/// A Postgres context that never reaches a server: the connection "opens"
/// without opening, and every command EF sends comes back as an empty
/// result, its SQL kept in <see cref="Commands"/>. EF still translates and
/// renders each query, so a read with several queries in a row runs every
/// one of them, not only the first. A refused connection, the earlier way,
/// stopped a read at its first await, and an untranslatable second query
/// went unseen. A query that must answer one row (a count, an Any, a
/// First) gets its type's default rather than "Sequence contains no
/// elements", so the read carries on to the next.
/// </summary>
internal sealed class EmptyPostgres : IDisposable
{
    private readonly Recorder _recorder = new();

    public AppDbContext Db { get; }

    public IReadOnlyList<string> Commands => _recorder.Commands;

    public EmptyPostgres()
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        AppDbContextOptions.Configure(builder, DatabaseProvider.Postgres, "Host=nowhere;Database=none;Username=x;Password=x");
        builder.AddInterceptors(_recorder).ReplaceService<IQueryCompiler, DefaultingQueryCompiler>();
        Db = new AppDbContext(builder.Options);
    }

    public void Dispose() => Db.Dispose();

    private sealed class Recorder : DbConnectionInterceptor, IDbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData, InterceptionResult result) =>
            InterceptionResult.Suppress();

        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(
            DbConnection connection, ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(InterceptionResult.Suppress());

        public override InterceptionResult ConnectionClosing(DbConnection connection, ConnectionEventData eventData, InterceptionResult result) =>
            InterceptionResult.Suppress();

        public override ValueTask<InterceptionResult> ConnectionClosingAsync(DbConnection connection, ConnectionEventData eventData, InterceptionResult result) =>
            ValueTask.FromResult(InterceptionResult.Suppress());

        public InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result) =>
            InterceptionResult<DbDataReader>.SuppressWithResult(Empty(command));

        public ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(InterceptionResult<DbDataReader>.SuppressWithResult(Empty(command)));

        public InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result) =>
            InterceptionResult<object>.SuppressWithResult(Kept(command, DBNull.Value));

        public ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(InterceptionResult<object>.SuppressWithResult(Kept(command, DBNull.Value)));

        public InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result) =>
            InterceptionResult<int>.SuppressWithResult(Kept(command, 0));

        public ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(InterceptionResult<int>.SuppressWithResult(Kept(command, 0)));

        private DbDataReader Empty(DbCommand command) => Kept(command, new DataTable().CreateDataReader());

        private T Kept<T>(DbCommand command, T answer)
        {
            lock (Commands) Commands.Add(command.CommandText);
            return answer;
        }
    }

    /// <summary>EF's compiler, answering a one-row query that found no row with the type's default.</summary>
    private sealed class DefaultingQueryCompiler(
        IQueryContextFactory queryContextFactory,
        ICompiledQueryCache compiledQueryCache,
        ICompiledQueryCacheKeyGenerator compiledQueryCacheKeyGenerator,
        IDatabase database,
        IDiagnosticsLogger<DbLoggerCategory.Query> logger,
        ICurrentDbContext currentContext,
        IEvaluatableExpressionFilter evaluatableExpressionFilter,
        IModel model)
        : QueryCompiler(queryContextFactory, compiledQueryCache, compiledQueryCacheKeyGenerator, database, logger, currentContext,
            evaluatableExpressionFilter, model)
    {
        private static readonly MethodInfo DefaultingMethod =
            typeof(DefaultingQueryCompiler).GetMethod(nameof(Defaulting), BindingFlags.NonPublic | BindingFlags.Static)!;

        public override TResult ExecuteAsync<TResult>(Expression query, CancellationToken cancellationToken = default)
        {
            var result = base.ExecuteAsync<TResult>(query, cancellationToken);
            var type = typeof(TResult);
            if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(Task<>)) return result;
            return (TResult)DefaultingMethod.MakeGenericMethod(type.GetGenericArguments()[0]).Invoke(null, [result])!;
        }

        private static async Task<T?> Defaulting<T>(Task<T> answer)
        {
            try { return await answer; }
            catch (InvalidOperationException e) when (e.Message.StartsWith("Sequence contains no elements", StringComparison.Ordinal)) { return default; }
        }
    }
}
