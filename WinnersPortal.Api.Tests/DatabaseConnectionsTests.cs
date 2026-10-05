using Microsoft.Data.SqlClient;
using Npgsql;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Services.Database;
using Xunit;

namespace WinnersPortal.Api.Tests;

/// <summary>
/// The connection form's fields to a connection string and back: what is
/// kept from the string being edited, what a field overwrites, and the one
/// rule that guards the stored password — a blank keeps it only while the
/// server and the user stay the same.
/// </summary>
public class DatabaseConnectionsTests
{
    private const string Running =
        "Server=tcp:127.0.0.1,1433;Database=winnersportal;User Id=portal;Password=s3cret;TrustServerCertificate=True;Application Name=wp;Connect Timeout=45";

    private static DatabaseConnectionFields Sql(
        string server = "tcp:127.0.0.1", int? port = 1433, string database = "winnersportal", string auth = "sql",
        string user = "portal", string password = "", bool encrypt = true, bool trust = true) =>
        new("sqlserver", server, port, database, auth, user, password, encrypt, trust);

    private static string Build(DatabaseConnectionFields f, (DatabaseProvider, string)? current = null)
    {
        var problem = DatabaseConnections.TryBuild(f, current, out _, out var cs);
        Assert.Null(problem);
        return cs;
    }

    private static string Problem(DatabaseConnectionFields f, (DatabaseProvider, string)? current = null)
    {
        var problem = DatabaseConnections.TryBuild(f, current, out _, out _);
        Assert.NotNull(problem);
        return problem!;
    }

    [Fact]
    public void The_running_string_reads_back_as_fields_without_its_password()
    {
        var v = DatabaseConnections.View(DatabaseProvider.SqlServer, Running);
        Assert.Equal("tcp:127.0.0.1", v.Server);
        Assert.Equal(1433, v.Port);
        Assert.Equal("winnersportal", v.Database);
        Assert.Equal("sql", v.Authentication);
        Assert.Equal("portal", v.UserId);
        Assert.True(v.PasswordSet);
        Assert.True(v.Encrypt); // SqlClient 6 encrypts unless told otherwise
        Assert.True(v.TrustServerCertificate);
        Assert.False(v.PortFixed);
        Assert.Null(v.Advanced);
        Assert.DoesNotContain("s3cret", System.Text.Json.JsonSerializer.Serialize(v));
    }

    [Theory]
    [InlineData("localhost", "localhost", null, false)]
    [InlineData("db.example.com,14330", "db.example.com", 14330, false)]
    [InlineData(@"HOST\SQLEXPRESS", @"HOST\SQLEXPRESS", null, false)]
    [InlineData(@"tcp:HOST\SQLEXPRESS,1500", @"tcp:HOST\SQLEXPRESS", 1500, false)]
    [InlineData(@"np:\\host\pipe\sql\query", @"np:\\host\pipe\sql\query", null, true)]
    [InlineData(@"(localdb)\MSSQLLocalDB", @"(localdb)\MSSQLLocalDB", null, true)]
    public void A_data_source_splits_into_server_and_port(string dataSource, string server, int? port, bool portFixed)
    {
        Assert.Equal((server, port, portFixed), DatabaseConnections.SplitDataSource(dataSource));
    }

    [Fact]
    public void A_new_sql_login_is_built_whole()
    {
        var cs = Build(Sql(server: "db.example.com", port: null, password: "pw", trust: false));
        var b = new SqlConnectionStringBuilder(cs);
        Assert.Equal("db.example.com", b.DataSource);
        Assert.Equal("winnersportal", b.InitialCatalog);
        Assert.Equal("portal", b.UserID);
        Assert.Equal("pw", b.Password);
        Assert.False(b.IntegratedSecurity);
        Assert.False(b.TrustServerCertificate);
        Assert.DoesNotContain("Trust", cs, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Windows_sign_in_drops_the_login_and_its_password()
    {
        var cs = Build(Sql(auth: "windows", user: ""), (DatabaseProvider.SqlServer, Running));
        var b = new SqlConnectionStringBuilder(cs);
        Assert.True(b.IntegratedSecurity);
        Assert.DoesNotContain("s3cret", cs);
        Assert.DoesNotContain("portal", cs.Replace("winnersportal", ""), StringComparison.OrdinalIgnoreCase);
        var v = DatabaseConnections.View(DatabaseProvider.SqlServer, cs);
        Assert.Equal("windows", v.Authentication);
        Assert.False(v.PasswordSet);
        Assert.Null(DatabaseConnections.UserOf(DatabaseProvider.SqlServer, cs));
    }

    [Fact]
    public void A_blank_password_keeps_the_current_one_on_the_same_server_and_user()
    {
        // Only the database changes: the move-to-a-new-database case.
        var cs = Build(Sql(database: "wp_next"), (DatabaseProvider.SqlServer, Running));
        var b = new SqlConnectionStringBuilder(cs);
        Assert.Equal("s3cret", b.Password);
        Assert.Equal("wp_next", b.InitialCatalog);
        // What the fields do not cover is kept.
        Assert.Equal("wp", b.ApplicationName);
        Assert.Equal(45, b.ConnectTimeout);
    }

    [Fact]
    public void The_same_server_written_another_way_still_keeps_the_password()
    {
        var cs = Build(Sql(server: "127.0.0.1", port: null), (DatabaseProvider.SqlServer, Running));
        Assert.Equal("s3cret", new SqlConnectionStringBuilder(cs).Password);
    }

    [Fact]
    public void Another_server_or_user_needs_the_password_typed()
    {
        // Otherwise a Test pointed at a host of one's own would be handed the stored password.
        Assert.Contains("Enter the password", Problem(Sql(server: "evil.example.com", port: null), (DatabaseProvider.SqlServer, Running)));
        Assert.Contains("Enter the password", Problem(Sql(port: 1434), (DatabaseProvider.SqlServer, Running)));
        Assert.Contains("Enter the password", Problem(Sql(user: "sa"), (DatabaseProvider.SqlServer, Running)));
        Assert.Contains("Enter the password", Problem(Sql(password: "")));
    }

    [Fact]
    public void A_new_server_drops_what_belonged_to_the_old_one()
    {
        var current = Running + ";Failover Partner=mirror;Host Name In Certificate=cert.example.com;Server SPN=MSSQLSvc/x";
        var cs = Build(Sql(server: "other.example.com", port: null, password: "pw"), (DatabaseProvider.SqlServer, current));
        var b = new SqlConnectionStringBuilder(cs);
        Assert.Equal("", b.FailoverPartner);
        Assert.Equal("", b.HostNameInCertificate);
        Assert.Equal("", b.ServerSPN);
        Assert.Equal("wp", b.ApplicationName);
    }

    [Fact]
    public void A_checkbox_left_as_it_was_keeps_a_stricter_setting()
    {
        var strict = "Server=db;Database=wp;User Id=u;Password=p;Encrypt=Strict";
        var kept = Build(Sql(server: "db", port: null, database: "wp", user: "u", trust: false), (DatabaseProvider.SqlServer, strict));
        Assert.True(new SqlConnectionStringBuilder(kept).Encrypt.Equals(SqlConnectionEncryptOption.Strict));

        var off = Build(Sql(server: "db", port: null, database: "wp", user: "u", encrypt: false, trust: false), (DatabaseProvider.SqlServer, strict));
        Assert.True(new SqlConnectionStringBuilder(off).Encrypt.Equals(SqlConnectionEncryptOption.Optional));
    }

    [Fact]
    public void Unticking_trust_removes_it()
    {
        var cs = Build(Sql(trust: false), (DatabaseProvider.SqlServer, Running));
        Assert.False(new SqlConnectionStringBuilder(cs).TrustServerCertificate);
        Assert.DoesNotContain("Trust", cs, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_advanced_sign_in_is_shown_and_left_alone()
    {
        var entra = "Server=x.database.windows.net;Database=wp;Authentication=ActiveDirectoryPassword;User Id=u@x;Password=p";
        var v = DatabaseConnections.View(DatabaseProvider.SqlServer, entra);
        Assert.Equal("Authentication=ActiveDirectoryPassword", v.Advanced);
        Assert.Contains("cannot change", Problem(
            Sql(server: "x.database.windows.net", port: null, database: "wp", auth: "windows", user: ""), (DatabaseProvider.SqlServer, entra)));
    }

    [Theory]
    [InlineData("", 1433, "server is required")]
    [InlineData("db", 70000, "port")]
    [InlineData(@"np:\\h\pipe\sql\query", 1433, "takes no port")]
    public void Bad_fields_are_named(string server, int port, string words)
    {
        Assert.Contains(words, Problem(Sql(server: server, port: port, password: "pw")), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_sql_login_needs_its_user_and_an_unknown_provider_is_named()
    {
        Assert.Contains("user ID", Problem(Sql(user: "", password: "pw")));
        Assert.Contains("Unknown database provider", Problem(Sql() with { Provider = "oracle" }));
    }

    // ---- PostgreSQL ------------------------------------------------------

    private const string Pg = "Host=pg.example.com;Port=5433;Database=winnersportal;Username=portal;Password=s3cret;Timeout=30";

    private static DatabaseConnectionFields Postgres(
        string server = "pg.example.com", int? port = 5433, string user = "portal", string password = "",
        bool encrypt = false, bool trust = false, string auth = "sql") =>
        new("postgres", server, port, "winnersportal", auth, user, password, encrypt, trust);

    [Fact]
    public void A_postgres_string_reads_back_and_keeps_its_extras_and_password()
    {
        var v = DatabaseConnections.View(DatabaseProvider.Postgres, Pg);
        Assert.Equal(("pg.example.com", 5433, "portal", true, false), (v.Server, v.Port, v.UserId, v.PasswordSet, v.Encrypt));

        var cs = Build(Postgres(), (DatabaseProvider.Postgres, Pg));
        var b = new NpgsqlConnectionStringBuilder(cs);
        Assert.Equal("s3cret", b.Password);
        Assert.Equal(30, b.Timeout);
    }

    [Theory]
    [InlineData(false, false, SslMode.Prefer)]
    [InlineData(true, true, SslMode.Require)]
    [InlineData(true, false, SslMode.VerifyFull)]
    public void Postgres_encryption_maps_to_ssl_mode(bool encrypt, bool trust, SslMode mode)
    {
        var cs = Build(Postgres(password: "pw", encrypt: encrypt, trust: trust));
        Assert.Equal(mode, new NpgsqlConnectionStringBuilder(cs).SslMode);
        Assert.DoesNotContain("Trust Server Certificate", cs, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Postgres_keeps_verify_ca_and_refuses_windows_and_a_new_host_without_a_password()
    {
        var ca = Pg + ";SSL Mode=VerifyCA;Root Certificate=/etc/ca.pem";
        var kept = new NpgsqlConnectionStringBuilder(Build(Postgres(encrypt: true), (DatabaseProvider.Postgres, ca)));
        Assert.Equal(SslMode.VerifyCA, kept.SslMode);
        Assert.Equal("/etc/ca.pem", kept.RootCertificate);

        Assert.Contains("Windows authentication is for SQL Server", Problem(Postgres(auth: "windows", password: "pw")));
        Assert.Contains("Enter the password", Problem(Postgres(server: "elsewhere"), (DatabaseProvider.Postgres, Pg)));

        var moved = new NpgsqlConnectionStringBuilder(Build(Postgres(server: "elsewhere", password: "pw", encrypt: true), (DatabaseProvider.Postgres, ca)));
        Assert.Null(moved.RootCertificate);
    }

    [Fact]
    public void Switching_provider_starts_a_fresh_string()
    {
        var cs = Build(Postgres(password: "pw"), (DatabaseProvider.SqlServer, Running));
        Assert.DoesNotContain("s3cret", cs);
        Assert.DoesNotContain("Application Name", cs, StringComparison.OrdinalIgnoreCase);
    }
}
