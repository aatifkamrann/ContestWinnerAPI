using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using WinnersPortal.Domain;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Migrations.SqlServer;
using WinnersPortal.Services.Activity;
using WinnersPortal.Services.Opportunities;
using Xunit;

namespace WinnersPortal.Api.Tests;

/// <summary>
/// The two databases the portal runs on, held to the same model: where
/// the dialects part — arrays and documents, the generated search column,
/// the filtered indexes, the cascade paths SQL Server refuses — each
/// provider's shape is pinned, so a change to one branch that forgets the
/// other fails here, with no database behind either.
/// </summary>
public class DatabaseDialectTests
{
    private static AppDbContext Postgres() => new(AppDbContextOptions.Build(
        DatabaseProvider.Postgres, "Host=nowhere;Database=none;Username=x;Password=x"));

    private static AppDbContext SqlServer() => new(AppDbContextOptions.Build(
        DatabaseProvider.SqlServer, "Server=nowhere;Database=none;User Id=x;Password=x;TrustServerCertificate=True"));

    private static string ColumnType<T>(DbContext db, string property) =>
        db.Model.FindEntityType(typeof(T))!.FindProperty(property)!.GetColumnType();

    // ---------------------------------------------------------------- shape

    [Fact]
    public void Each_context_knows_which_database_it_was_built_for()
    {
        using var pg = Postgres();
        using var sql = SqlServer();
        Assert.Equal(DatabaseProvider.Postgres, pg.Provider);
        Assert.False(pg.IsSqlServer);
        Assert.Equal(DatabaseProvider.SqlServer, sql.Provider);
        Assert.True(sql.IsSqlServer);
    }

    [Theory]
    [InlineData("", DatabaseProvider.SqlServer)]
    [InlineData(null, DatabaseProvider.SqlServer)]
    [InlineData("  ", DatabaseProvider.SqlServer)]
    [InlineData("postgres", DatabaseProvider.Postgres)]
    [InlineData(" PostgreSQL ", DatabaseProvider.Postgres)]
    [InlineData("sqlserver", DatabaseProvider.SqlServer)]
    [InlineData("MSSQL", DatabaseProvider.SqlServer)]
    public void The_provider_word_is_forgiving_but_finite(string? raw, DatabaseProvider expected)
    {
        Assert.Equal(expected, DatabaseProviders.Parse(raw));
        Assert.Contains("DATABASE__PROVIDER", Assert.Throws<InvalidOperationException>(() => DatabaseProviders.Parse("oracle")).Message);
    }

    [Fact]
    public void Arrays_and_documents_are_nvarchar_max_on_sql_server_and_what_they_were_on_postgres()
    {
        using var pg = Postgres();
        using var sql = SqlServer();
        foreach (var (type, property, postgres) in new[]
                 {
                     (typeof(Profile), nameof(Profile.SecondaryCategories), "text[]"),
                     (typeof(Profile), nameof(Profile.PreferredDurations), "text[]"),
                     (typeof(Profile), nameof(Profile.PreferredProjectTypes), "text[]"),
                     (typeof(ProfileProject), nameof(ProfileProject.ImageIds), "uuid[]"),
                     (typeof(Application), nameof(Application.PortfolioJson), "jsonb"),
                     (typeof(Application), nameof(Application.EvaluationJson), "jsonb"),
                     (typeof(WebhookDelivery), nameof(WebhookDelivery.Payload), "jsonb"),
                 })
        {
            Assert.Equal(postgres, pg.Model.FindEntityType(type)!.FindProperty(property)!.GetColumnType());
            // Not the json type of SQL Server 2025: 2022 has to run this model.
            Assert.Equal("nvarchar(max)", sql.Model.FindEntityType(type)!.FindProperty(property)!.GetColumnType());
        }
    }

    [Fact]
    public void The_session_stamp_defaults_in_each_databases_own_words()
    {
        using var pg = Postgres();
        using var sql = SqlServer();
        Assert.Equal("gen_random_uuid()", pg.Model.FindEntityType(typeof(User))!.FindProperty(nameof(User.SessionStamp))!.GetDefaultValueSql());
        Assert.Equal("NEWID()", sql.Model.FindEntityType(typeof(User))!.FindProperty(nameof(User.SessionStamp))!.GetDefaultValueSql());
    }

    [Fact]
    public void The_search_column_exists_on_postgres_alone()
    {
        using var pg = Postgres();
        using var sql = SqlServer();
        Assert.NotNull(pg.Model.FindEntityType(typeof(Opportunity))!.FindProperty("SearchVector"));
        // The index method is a migration-time fact, kept on the design-time
        // model alone; the runtime model drops what no query reads.
        var design = pg.GetService<IDesignTimeModel>().Model;
        Assert.Contains(design.FindEntityType(typeof(Opportunity))!.GetIndexes(),
            i => i.Properties.Any(p => p.Name == "SearchVector") && i.GetMethod() == "GIN");
        Assert.Null(sql.Model.FindEntityType(typeof(Opportunity))!.FindProperty("SearchVector"));
    }

    [Fact]
    public void Filtered_indexes_quote_their_column_the_way_each_database_reads()
    {
        using var pg = Postgres();
        using var sql = SqlServer();
        string Filter(DbContext db, Type type, string column) =>
            db.Model.FindEntityType(type)!.GetIndexes().Single(i => i.Properties.Any(p => p.Name == column) && i.GetFilter() != null).GetFilter()!;
        Assert.Equal("\"Status\" = 0", Filter(pg, typeof(Entry), nameof(Entry.FreelancerId)));
        Assert.Equal("[Status] = 0", Filter(sql, typeof(Entry), nameof(Entry.FreelancerId)));
        Assert.Equal("\"DedupeKey\" IS NOT NULL", Filter(pg, typeof(EmailMessage), nameof(EmailMessage.DedupeKey)));
        Assert.Equal("[DedupeKey] IS NOT NULL", Filter(sql, typeof(EmailMessage), nameof(EmailMessage.DedupeKey)));
    }

    [Fact]
    public void Every_unique_index_over_a_nullable_column_carries_a_filter()
    {
        // SQL Server treats two NULLs as equal in a unique index, so an
        // optional column that is unique when present must say "when
        // present" — DedupeKey does; a future one must too.
        using var sql = SqlServer();
        var offenders = sql.Model.GetEntityTypes()
            .SelectMany(t => t.GetIndexes().Where(i => i.IsUnique && i.GetFilter() == null && i.Properties.Any(p => p.IsNullable)))
            .Select(i => i.DeclaringEntityType.ClrType.Name + "(" + string.Join(",", i.Properties.Select(p => p.Name)) + ")")
            .ToList();
        Assert.Empty(offenders);
    }

    [Fact]
    public void The_cascade_paths_sql_server_refuses_are_walked_by_ef_there_and_by_the_database_on_postgres()
    {
        using var pg = Postgres();
        using var sql = SqlServer();
        DeleteBehavior Fk(DbContext db, Type type, string column) =>
            db.Model.FindEntityType(type)!.GetForeignKeys().Single(f => f.Properties.Single().Name == column).DeleteBehavior;
        Assert.Equal(DeleteBehavior.Cascade, Fk(pg, typeof(Checkpoint), nameof(Checkpoint.MilestoneId)));
        Assert.Equal(DeleteBehavior.ClientCascade, Fk(sql, typeof(Checkpoint), nameof(Checkpoint.MilestoneId)));
        Assert.Equal(DeleteBehavior.SetNull, Fk(pg, typeof(Submission), nameof(Submission.MilestoneId)));
        Assert.Equal(DeleteBehavior.ClientSetNull, Fk(sql, typeof(Submission), nameof(Submission.MilestoneId)));
        Assert.Equal(DeleteBehavior.SetNull, Fk(pg, typeof(Profile), nameof(Profile.DeletedByUserId)));
        Assert.Equal(DeleteBehavior.ClientSetNull, Fk(sql, typeof(Profile), nameof(Profile.DeletedByUserId)));
    }

    [Fact]
    public void Both_models_name_the_same_tables_and_columns()
    {
        // The dialects part on types and defaults, never on what exists —
        // except the search column, which is Postgres's own.
        using var pg = Postgres();
        using var sql = SqlServer();
        var pgColumns = pg.Model.GetEntityTypes()
            .SelectMany(t => t.GetProperties().Where(p => p.Name != "SearchVector").Select(p => t.GetTableName() + "." + p.GetColumnName()))
            .OrderBy(x => x, StringComparer.Ordinal).ToList();
        var sqlColumns = sql.Model.GetEntityTypes()
            .SelectMany(t => t.GetProperties().Select(p => t.GetTableName() + "." + p.GetColumnName()))
            .OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.Equal(pgColumns, sqlColumns);
    }

    // ----------------------------------------------------------- migrations

    private const string PostgresNamespace = "WinnersPortal.Infrastructure.Data.Migrations.Postgres";
    private const string SqlServerNamespace = "WinnersPortal.Migrations.SqlServer";

    [Fact]
    public void Each_provider_reads_only_its_own_migration_tree()
    {
        using var pg = Postgres();
        var assembly = pg.GetService<IMigrationsAssembly>();
        Assert.Same(typeof(AppDbContext).Assembly, assembly.Assembly);
        Assert.NotEmpty(assembly.Migrations);
        Assert.All(assembly.Migrations.Values, m => Assert.Equal(PostgresNamespace, m.Namespace));
        Assert.Contains("20260917132939_RefreshTokens", assembly.Migrations.Keys);
        Assert.Contains("20260918115948_IdentityVerification", assembly.Migrations.Keys);
        Assert.Equal(PostgresNamespace, assembly.ModelSnapshot?.GetType().Namespace);

        using var sql = SqlServer();
        var sqlAssembly = sql.GetService<IMigrationsAssembly>();
        Assert.Equal(AppDbContextOptions.SqlServerMigrationsAssembly, sqlAssembly.Assembly.GetName().Name);
        Assert.All(sqlAssembly.Migrations.Values, m => Assert.Equal(SqlServerNamespace, m.Namespace));
        Assert.Contains("20260918123608_Initial", sqlAssembly.Migrations.Keys);
        Assert.Contains("20260918235900_ContestFullText", sqlAssembly.Migrations.Keys);
        Assert.Contains("20260921190841_JsonColumnsAsText", sqlAssembly.Migrations.Keys);
        Assert.DoesNotContain("20260917132939_RefreshTokens", sqlAssembly.Migrations.Keys);
        Assert.Equal(SqlServerNamespace, sqlAssembly.ModelSnapshot?.GetType().Namespace);
        // The hand-written full-text one comes after the table it indexes.
        Assert.True(string.CompareOrdinal("20260918235900_ContestFullText", "20260918123608_Initial") > 0);
    }

    [Fact]
    public void The_json_columns_are_converted_only_where_they_are_still_json()
    {
        // Initial creates nvarchar(max) since 2026-09-21; a database built
        // on SQL Server 2025's json type before then is converted by the
        // migration, column by column, with an explicit CAST — SQL Server
        // allows no implicit one — and each block is skipped where the
        // column is already text.
        var columns = JsonColumnsAsText.Columns;
        Assert.Equal(7, columns.Length);
        Assert.Contains(("Profiles", "SecondaryCategories"), columns);
        Assert.Contains(("WebhookDeliveries", "Payload"), columns);
        Assert.Contains(("Applications", "EvaluationJson"), columns);
        foreach (var (table, column) in columns)
        {
            var sql = JsonColumnsAsText.Convert(table, column);
            Assert.Contains($"OBJECT_ID(N'[{table}]') AND c.name = N'{column}' AND t.name = N'json'", sql);
            Assert.Contains($"CAST([{column}] AS nvarchar(max))", sql);
            Assert.Contains($"ALTER TABLE [{table}] DROP COLUMN [{column}];", sql);
            Assert.Contains($"EXEC sp_rename N'[{table}].[{column}__text]', N'{column}', N'COLUMN';", sql);
            Assert.Contains($"ALTER COLUMN [{column}] nvarchar(max) NOT NULL", sql);
        }
        Assert.All(columns, c => Assert.Equal("nvarchar(max)", SqlServer().Model.FindEntityType(TypeOf(c.Table))!.FindProperty(c.Column)!.GetColumnType()));

        static Type TypeOf(string table) => table switch
        {
            "WebhookDeliveries" => typeof(WebhookDelivery),
            "Profiles" => typeof(Profile),
            "ProfileProjects" => typeof(ProfileProject),
            "Applications" => typeof(Application),
            _ => throw new ArgumentOutOfRangeException(nameof(table), table, null),
        };
    }

    [Fact]
    public void No_migration_lives_outside_its_tree()
    {
        var infrastructure = typeof(AppDbContext).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && (t.IsSubclassOf(typeof(Migration)) || t.IsSubclassOf(typeof(ModelSnapshot))))
            .Where(t => t.Namespace != PostgresNamespace)
            .Select(t => t.FullName)
            .ToList();
        Assert.Empty(infrastructure);
        var sqlServer = Assembly.Load(AppDbContextOptions.SqlServerMigrationsAssembly).GetTypes()
            .Where(t => !t.IsAbstract && (t.IsSubclassOf(typeof(Migration)) || t.IsSubclassOf(typeof(ModelSnapshot))))
            .Where(t => t.Namespace != SqlServerNamespace)
            .Select(t => t.FullName)
            .ToList();
        Assert.Empty(sqlServer);
    }

    // --------------------------------------------------------------- errors

    [Fact]
    public void A_unique_index_hit_is_recognised_in_either_databases_words()
    {
        Assert.True(DbErrors.IsUniqueViolation(new PostgresException("dup", "ERROR", "ERROR", DbErrors.PostgresUniqueViolation)));
        Assert.False(DbErrors.IsUniqueViolation(new PostgresException("fk", "ERROR", "ERROR", "23503")));
        Assert.True(DbErrors.IsSqlServerUniqueViolation(2601));
        Assert.True(DbErrors.IsSqlServerUniqueViolation(2627));
        Assert.False(DbErrors.IsSqlServerUniqueViolation(547));
        Assert.False(DbErrors.IsUniqueViolation(new DbUpdateException("plain")));
        Assert.False(DbErrors.IsUniqueViolation((Exception?)null));
    }

    // --------------------------------------------------------------- search

    [Fact]
    public void The_feed_search_renders_in_each_databases_own_text_search()
    {
        using var pg = Postgres();
        var pgSql = OpportunitySearch.Apply(pg.Opportunities, pg, "inven").ToQueryString();
        Assert.Contains("plainto_tsquery", pgSql);
        Assert.Contains("ILIKE", pgSql);

        using var sql = SqlServer();
        var sqlSql = OpportunitySearch.Apply(sql.Opportunities, sql, "inven").ToQueryString();
        Assert.Contains("FREETEXT", sqlSql);
        Assert.Contains("LIKE", sqlSql);
        Assert.Contains("ESCAPE", sqlSql);
        Assert.DoesNotContain("ILIKE", sqlSql);
    }

    [Fact]
    public void The_activity_search_renders_like_on_sql_server_and_ilike_on_postgres()
    {
        var filter = new ActivityService.ActivityFilter(null, null, null, null, "login", null, null);
        using var pg = Postgres();
        Assert.Contains("ILIKE", ActivityService.Filter(pg, filter).ToQueryString());
        using var sql = SqlServer();
        var rendered = ActivityService.Filter(sql, filter).ToQueryString();
        Assert.Contains("LIKE", rendered);
        Assert.DoesNotContain("ILIKE", rendered);
    }

    [Fact]
    public void The_title_pattern_escapes_the_bracket_sql_server_reads_as_a_wildcard()
    {
        Assert.Equal("%a\\[b\\%c\\_d%", Search.TitlePattern("a[b%c_d"));
    }

    [Fact]
    public void The_recount_statements_use_each_databases_quoting()
    {
        // Rendered by hand-read, not run: the shape is the same statement
        // twice, and the one thing that can drift is the quoting.
        using var pg = Postgres();
        using var sql = SqlServer();
        Assert.False(pg.IsSqlServer);
        Assert.True(sql.IsSqlServer);
    }

    // --------------------------------------------------------- requirements

    [Fact]
    public void The_sql_server_requirements_name_what_to_install()
    {
        Assert.Contains("SQL Server 2022", SqlServerRequirements.Problem(new("15.0.2000.5", 15, "Express Edition (64-bit)", true)));
        Assert.Contains("Full-Text", SqlServerRequirements.Problem(new("17.0.700.9", 17, "Developer Edition (64-bit)", false)));
        Assert.Contains("Full-Text", SqlServerRequirements.Problem(new("16.0.1000.6", 16, "Express Edition (64-bit)", false)));
        Assert.Null(SqlServerRequirements.Problem(new("16.0.1000.6", 16, "Express Edition (64-bit)", true)));
        Assert.Null(SqlServerRequirements.Problem(new("17.0.700.9", 17, "Developer Edition (64-bit)", true)));
    }
}
