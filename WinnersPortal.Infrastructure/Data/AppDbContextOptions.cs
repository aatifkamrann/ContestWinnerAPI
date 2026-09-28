using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace WinnersPortal.Infrastructure.Data;

/// <summary>
/// The one place that knows how to build the context's options for either
/// provider: the API at startup, the mover for its target, and the tests
/// for a model with no database behind it. Each provider reads its own
/// migration tree — Postgres's beside the context in this assembly, SQL
/// Server's in an assembly of its own — so neither ever sees the other's
/// DDL. On SQL Server the day-to-day reads run through Dapper unless the
/// <c>dapper</c> flag says otherwise (<c>Database:Dapper</c>, a diagnostic
/// that serves the same database through the LINQ instead).
/// </summary>
public static class AppDbContextOptions
{
    /// <summary>Where the SQL Server migrations live; the scaffolder is pointed at that project.</summary>
    public const string SqlServerMigrationsAssembly = "WinnersPortal.Migrations.SqlServer";

    public static void Configure(DbContextOptionsBuilder builder, DatabaseProvider provider, string connectionString, bool dapper = true)
    {
        switch (provider)
        {
            case DatabaseProvider.Postgres:
                builder.UseNpgsql(connectionString);
                break;
            case DatabaseProvider.SqlServer:
                // No retry-on-failure: the mover runs its copy in one explicit
                // transaction, which an execution strategy refuses to wrap.
                builder.UseSqlServer(connectionString, o => o.MigrationsAssembly(SqlServerMigrationsAssembly));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(provider), provider, "A database provider with no configuration.");
        }
        ((IDbContextOptionsBuilderInfrastructure)builder).AddOrUpdateExtension(new DapperExtension(dapper));
    }

    public static DbContextOptions<AppDbContext> Build(DatabaseProvider provider, string connectionString, bool dapper = true)
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        Configure(builder, provider, connectionString, dapper);
        return builder.Options;
    }

    /// <summary>Whether the options ask for Dapper on SQL Server; options built elsewhere say yes.</summary>
    public static bool DapperEnabled(DbContextOptions options) =>
        options.FindExtension<DapperExtension>()?.Enabled ?? true;

    /// <summary>The Dapper flag, carried on the options so a context knows it without a static.</summary>
    public sealed class DapperExtension(bool enabled) : IDbContextOptionsExtension
    {
        public bool Enabled { get; } = enabled;

        public DbContextOptionsExtensionInfo Info => new ExtensionInfo(this);

        public void ApplyServices(IServiceCollection services) { }

        public void Validate(IDbContextOptions options) { }

        private sealed class ExtensionInfo(DapperExtension extension) : DbContextOptionsExtensionInfo(extension)
        {
            public override bool IsDatabaseProvider => false;

            public override string LogFragment => extension.Enabled ? "Dapper=on " : "Dapper=off ";

            public override int GetServiceProviderHashCode() => 0;

            public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other) => other is ExtensionInfo;

            public override void PopulateDebugInfo(IDictionary<string, string> debugInfo) =>
                debugInfo["Dapper"] = extension.Enabled ? "on" : "off";
        }
    }
}
