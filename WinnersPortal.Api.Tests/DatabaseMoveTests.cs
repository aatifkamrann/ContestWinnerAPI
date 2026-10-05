using Microsoft.EntityFrameworkCore;
using WinnersPortal.Domain;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Services.Common;
using WinnersPortal.Services.Database;
using WinnersPortal.Services.Settings;
using Xunit;

namespace WinnersPortal.Api.Tests;

/// <summary>
/// The pure half of moving the portal to another database: the order the
/// tables are copied in, the two kinds of column the copy has to treat
/// specially, the pause the workers honour and the gate that holds writes
/// while it does.
/// </summary>
public class DatabaseMoveTests
{
    private static AppDbContext Postgres() => new(AppDbContextOptions.Build(
        DatabaseProvider.Postgres, "Host=nowhere;Database=none;Username=x;Password=x"));

    // ---------------------------------------------------------------- order

    [Fact]
    public void Every_table_is_copied_once_and_after_every_table_it_references()
    {
        using var db = Postgres();
        var order = DatabaseMover.CopyOrder(db.Model);
        var all = db.Model.GetEntityTypes().ToList();
        Assert.Equal(all.Count, order.Count);
        Assert.Equal(all.Count, order.Distinct().Count());
        var index = order.Select((t, i) => (t, i)).ToDictionary(x => x.t, x => x.i);
        foreach (var t in all)
            foreach (var fk in t.GetForeignKeys())
                Assert.True(index[fk.PrincipalEntityType] < index[t],
                    $"{fk.PrincipalEntityType.GetTableName()} must be copied before {t.GetTableName()}");
    }

    [Theory]
    [InlineData("Users", "Opportunities")]
    [InlineData("Opportunities", "Entries")]
    [InlineData("Entries", "Checkpoints")]
    [InlineData("Milestones", "Checkpoints")]
    [InlineData("Awards", "Ratings")]
    [InlineData("Profiles", "ProfileProjects")]
    [InlineData("PushDevices", "PushMessages")]
    [InlineData("Users", "IdentityVerifications")]
    public void The_named_pairs_come_parent_first(string parent, string child)
    {
        using var db = Postgres();
        var names = DatabaseMover.CopyOrder(db.Model).Select(t => t.GetTableName()).ToList();
        Assert.True(names.IndexOf(parent) < names.IndexOf(child), $"{parent} before {child}: {string.Join(", ", names)}");
    }

    [Fact]
    public void No_table_references_itself_and_no_column_hides_in_the_shadows()
    {
        using var db = Postgres();
        Assert.All(db.Model.GetEntityTypes(),
            t => Assert.DoesNotContain(t.GetForeignKeys(), fk => fk.PrincipalEntityType == t));
        DatabaseMover.AssertCopyable(db.Model); // only SearchVector, which the database writes itself
    }

    // -------------------------------------------------------------- columns

    [Fact]
    public void The_two_counting_keys_and_the_two_defaulted_booleans_are_known()
    {
        using var db = Postgres();
        Assert.Equal(["ActivityEvents", "SettingAudits"], DatabaseMover.IdentityTables(db.Model));
        var bools = DatabaseMover.DefaultedBools(db.Model)
            .Select(d => d.EntityType.GetTableName() + "." + d.Property.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(["ProfileProjects.MayShowPublicly", "Users.NotifyByEmail"], bools);
        // Each of those tables is keyed by a Guid named Id, which the restore relies on.
        foreach (var (t, _) in DatabaseMover.DefaultedBools(db.Model))
        {
            var key = Assert.Single(t.FindPrimaryKey()!.Properties);
            Assert.Equal(("Id", typeof(Guid)), (key.Name, key.ClrType));
        }
    }

    [Fact]
    public void Paging_orders_by_the_whole_key()
    {
        using var db = Postgres();
        var composite = db.Model.FindEntityType(typeof(MeritSnapshot))!;
        var sql = DatabaseMover.OrderByKey(db.MeritSnapshots.AsQueryable(), composite).ToQueryString();
        Assert.Contains("ORDER BY", sql);
        Assert.Contains("\"UserId\"", sql);
        Assert.Contains("\"DayUtc\"", sql);
        var simple = DatabaseMover.OrderByKey(db.Users.AsQueryable(), db.Model.FindEntityType(typeof(User))!).ToQueryString();
        Assert.Contains("ORDER BY u.\"Id\"", simple);
    }

    // ---------------------------------------------------------------- pause

    [Fact]
    public async Task A_paused_portal_refuses_new_cycles_and_drains_the_ones_running()
    {
        var pause = new AppPause();
        var lease = pause.TryEnter();
        Assert.NotNull(lease);
        Assert.Equal(1, pause.Active);

        pause.Pause("moving");
        Assert.True(pause.IsPaused);
        Assert.Null(pause.TryEnter());
        Assert.False(await pause.DrainAsync(TimeSpan.FromMilliseconds(250), CancellationToken.None));

        lease!.Dispose();
        lease.Dispose(); // twice is once
        Assert.Equal(0, pause.Active);
        Assert.True(await pause.DrainAsync(TimeSpan.FromSeconds(1), CancellationToken.None));

        pause.Resume();
        Assert.False(pause.IsPaused);
        Assert.NotNull(pause.TryEnter());
    }

    [Theory]
    [InlineData("GET", "/api/opportunities", true)]
    [InlineData("HEAD", "/api/health", true)]
    [InlineData("GET", "/api/admin/database/move", true)]
    [InlineData("POST", "/api/admin/database/move", true)]
    [InlineData("POST", "/api/setup", true)]
    [InlineData("POST", "/api/auth/logout", true)]
    [InlineData("POST", "/api/opportunities", false)]
    [InlineData("PUT", "/api/settings", false)]
    [InlineData("POST", "/api/webhooks/github", false)]
    [InlineData("POST", "/api/auth/login", false)]
    [InlineData("POST", "/somewhere-in-next", true)]
    public void While_paused_reads_pass_and_writes_wait_except_the_doors_that_drive_the_move(string method, string path, bool allowed) =>
        Assert.Equal(allowed, MaintenanceGate.AllowsWhilePaused(path, method));

    // ---------------------------------------------------------------- state

    [Fact]
    public void One_move_at_a_time_and_none_after_one_has_recorded()
    {
        var state = new DatabaseMoveState();
        Assert.Equal("idle", state.Snapshot().Phase);
        Assert.True(state.TryBegin(DatabaseProvider.SqlServer, "s", "d", "admin"));
        Assert.False(state.TryBegin(DatabaseProvider.SqlServer, "s", "d", "admin"));
        state.SetTables(3);
        state.TableStarted("Users");
        state.RowsAdded(5);
        state.TableDone();
        var mid = state.Snapshot();
        Assert.Equal(("checking", 1, 3, 5L, "sqlserver"), (mid.Phase, mid.TablesDone, mid.TablesTotal, mid.RowsCopied, mid.Target));

        state.Fail("the target vanished");
        Assert.Equal("failed", state.Snapshot().Phase);
        Assert.True(state.TryBegin(DatabaseProvider.Postgres, "s", "d", "admin")); // a failure frees the slot

        state.Recorded();
        Assert.True(state.RestartRequested);
        Assert.Equal("restarting", state.Snapshot().Phase);
        Assert.False(state.TryBegin(DatabaseProvider.Postgres, "s", "d", "admin")); // a recorded move does not
    }
}
