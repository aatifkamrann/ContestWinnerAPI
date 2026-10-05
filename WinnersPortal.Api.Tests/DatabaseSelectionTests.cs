using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Services.Database;
using Xunit;

namespace WinnersPortal.Api.Tests;

/// <summary>
/// Where the database choice comes from, and the file an in-app move
/// writes: read before the host exists, protected under its key ring, and
/// never a guess — a file that cannot be read stops the process rather
/// than booting on the other database.
/// </summary>
public class DatabaseSelectionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wp-db-" + Guid.NewGuid().ToString("N"));
    private readonly IDataProtectionProvider _ring = new EphemeralDataProtectionProvider();

    public DatabaseSelectionTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private IDataProtector Protector() => _ring.CreateProtector(DatabaseOverrideFile.Purpose);

    private static IConfiguration Config(params (string Key, string? Value)[] pairs) =>
        new ConfigurationBuilder().AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value))).Build();

    [Fact]
    public void The_override_round_trips_and_keeps_the_password_out_of_the_file()
    {
        var path = DatabaseOverrideFile.PathIn(_dir);
        var written = new DatabaseOverride(DatabaseProvider.SqlServer, "sqlhost,1433", "winnersportal",
            "Server=sqlhost,1433;Database=winnersportal;User Id=portal;Password=s3cret;TrustServerCertificate=True",
            new DateTimeOffset(2026, 9, 18, 10, 0, 0, TimeSpan.Zero), "anwar@example");
        DatabaseOverrideFile.Write(path, written, Protector());

        var text = File.ReadAllText(path);
        Assert.DoesNotContain("s3cret", text);
        Assert.Contains("\"provider\": \"sqlserver\"", text);
        Assert.Contains("\"server\": \"sqlhost,1433\"", text);
        // Plain, for backup.ps1, which cannot read the protected string.
        Assert.Contains("\"userName\": \"portal\"", text);
        Assert.False(File.Exists(path + ".tmp"));

        var read = DatabaseOverrideFile.Read(path, Protector());
        Assert.Equal(written, read);
    }

    [Fact]
    public void A_file_written_under_another_key_ring_stops_the_process_and_names_the_way_out()
    {
        var path = DatabaseOverrideFile.PathIn(_dir);
        DatabaseOverrideFile.Write(path, new DatabaseOverride(DatabaseProvider.Postgres, "h:5432", "d", "Host=h;Password=p",
            DateTimeOffset.UtcNow, "x"), Protector());
        var other = new EphemeralDataProtectionProvider().CreateProtector(DatabaseOverrideFile.Purpose);
        var e = Assert.Throws<DatabaseOverrideException>(() => DatabaseOverrideFile.Read(path, other));
        Assert.Contains(path, e.Message);
        Assert.Contains("delete the file", e.Message);
        Assert.Null(DatabaseOverrideFile.Read(Path.Combine(_dir, "absent.json"), Protector()));
    }

    [Fact]
    public void Describe_names_the_server_and_database_never_the_password()
    {
        Assert.Equal(("sqlhost,1433", "wp"), DatabaseOverrideFile.Describe(DatabaseProvider.SqlServer, "Server=sqlhost,1433;Database=wp;User Id=u;Password=p"));
        Assert.Equal(("db:5432", "winnersportal"), DatabaseOverrideFile.Describe(DatabaseProvider.Postgres, "Host=db;Port=5432;Database=winnersportal;Username=u;Password=p"));
        Assert.Equal(("?", "?"), DatabaseOverrideFile.Describe(DatabaseProvider.Postgres, "not a connection string ;;"));
    }

    [Fact]
    public void The_environment_is_read_when_no_file_has_been_written()
    {
        var s = DatabaseSelection.Resolve(
            Config((DatabaseProviders.ConfigKey, "sqlserver"), ("ConnectionStrings:Db", "Server=x;Database=y;User Id=u;Password=p")),
            _dir, Protector)!;
        Assert.Equal(DatabaseProvider.SqlServer, s.Provider);
        Assert.Equal(DatabaseSource.Environment, s.Source);
        Assert.Equal(DatabaseOverrideFile.PathIn(_dir), s.OverridePath);
    }

    [Fact]
    public void The_file_outranks_the_environment_and_says_so()
    {
        DatabaseOverrideFile.Write(DatabaseOverrideFile.PathIn(_dir), new DatabaseOverride(
            DatabaseProvider.SqlServer, "s", "d", "Server=s;Database=d;User Id=u;Password=p", DateTimeOffset.UtcNow, "x"), Protector());
        var warnings = new List<string>();
        var s = DatabaseSelection.Resolve(
            Config((DatabaseProviders.ConfigKey, "postgres"), ("ConnectionStrings:Db", "Host=db;Database=old")),
            _dir, Protector, warnings.Add)!;
        Assert.Equal(DatabaseProvider.SqlServer, s.Provider);
        Assert.Equal(DatabaseSource.Override, s.Source);
        Assert.Equal("Server=s;Database=d;User Id=u;Password=p", s.ConnectionString);
        Assert.Single(warnings);
        Assert.Contains("the file wins", warnings[0]);
    }

    [Fact]
    public void Nothing_set_is_no_database_rather_than_a_guess()
    {
        // No file and no string: the API starts on the setup page's connect
        // step instead of trying whatever answers on localhost.
        foreach (var provider in new string?[] { null, "sqlserver", "postgres" })
            Assert.Null(DatabaseSelection.Resolve(Config((DatabaseProviders.ConfigKey, provider)), _dir, Protector));
        Assert.Null(DatabaseSelection.Resolve(Config(("ConnectionStrings:Db", "  ")), _dir, Protector));
        Assert.False(File.Exists(DatabaseOverrideFile.PathIn(_dir)));
    }

    [Fact]
    public void The_design_time_placeholder_parses_and_names_no_real_server()
    {
        foreach (var provider in Enum.GetValues<DatabaseProvider>())
        {
            var s = DatabaseSelection.DesignTime(provider, _dir);
            Assert.Equal(DatabaseSource.DesignTime, s.Source);
            Assert.EndsWith(".invalid", s.Describe().Server.Split(':')[0]);
        }
    }

    [Theory]
    // An install from before SQL Server was the default: no provider, a
    // PostgreSQL string. It stays on PostgreSQL and says so.
    [InlineData(null, "Host=127.0.0.1;Port=5432;Database=winnersportal;Username=winnersportal;Password=p", DatabaseProvider.Postgres, true)]
    [InlineData("", "Host=db;Database=winnersportal;Username=u;Password=p", DatabaseProvider.Postgres, true)]
    [InlineData(null, "Server=db;Port=5432;Database=winnersportal;User Id=u;Password=p", DatabaseProvider.Postgres, true)]
    // A string both could read is SQL Server's, the default.
    [InlineData(null, "Server=sqlhost;Database=winnersportal;User Id=u;Password=p;TrustServerCertificate=True", DatabaseProvider.SqlServer, false)]
    [InlineData(null, "Server=localhost;Database=winnersportal;Integrated Security=True;TrustServerCertificate=True", DatabaseProvider.SqlServer, false)]
    // A provider that is said is never second-guessed.
    [InlineData("postgres", "Host=db;Database=winnersportal;Username=u;Password=p", DatabaseProvider.Postgres, false)]
    [InlineData("sqlserver", "Host=db;Database=winnersportal;Username=u;Password=p", DatabaseProvider.SqlServer, false)]
    public void A_blank_provider_is_sql_server_unless_the_string_is_postgres_only(
        string? provider, string connectionString, DatabaseProvider expected, bool warned)
    {
        var warnings = new List<string>();
        var s = DatabaseSelection.Resolve(
            Config((DatabaseProviders.ConfigKey, provider), ("ConnectionStrings:Db", connectionString)),
            _dir, Protector, warnings.Add)!;
        Assert.Equal(expected, s.Provider);
        Assert.Equal(DatabaseSource.Environment, s.Source);
        Assert.Equal(connectionString, s.ConnectionString);
        Assert.Equal(warned, warnings.Count == 1);
        if (warned) Assert.Contains("DATABASE__PROVIDER=postgres", warnings[0]);
    }
}
