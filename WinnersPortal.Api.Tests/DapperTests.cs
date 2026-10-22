using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Domain;
using WinnersPortal.Services.Activity;
using WinnersPortal.Services.Admin;
using WinnersPortal.Services.Ai;
using WinnersPortal.Services.Chat;
using WinnersPortal.Services.Opportunities;
using WinnersPortal.Services.Dashboard;
using WinnersPortal.Services.Leaderboard;
using WinnersPortal.Services.Notifications;
using WinnersPortal.Services.Profiles;
using WinnersPortal.Services.Reports;
using WinnersPortal.Services.Settings;
using Xunit;

namespace WinnersPortal.Api.Tests;

/// <summary>
/// Dapper on SQL Server: where the switch sits, what every stored
/// procedure's script is held to, that the runner can be handed nothing
/// but a procedure, and the parameter builders behind the two filtered
/// reads — all without a database, the way the dialect tests hold the
/// model.
/// </summary>
public class DapperTests
{
    private static AppDbContext SqlServer(bool dapper = true) => new(AppDbContextOptions.Build(
        DatabaseProvider.SqlServer, "Server=nowhere;Database=none;User Id=x;Password=x;TrustServerCertificate=True", dapper));

    private static AppDbContext Postgres() => new(AppDbContextOptions.Build(
        DatabaseProvider.Postgres, "Host=nowhere;Database=none;Username=x;Password=x"));

    // ------------------------------------------------------------- switch

    [Fact]
    public void Sql_server_reads_through_dapper_unless_the_options_say_otherwise()
    {
        using var on = SqlServer();
        using var off = SqlServer(dapper: false);
        Assert.True(on.UseDapper);
        Assert.False(off.UseDapper);
    }

    [Fact]
    public void Postgres_never_does()
    {
        using var pg = Postgres();
        Assert.False(pg.UseDapper);
    }

    [Fact]
    public void Options_built_without_the_flag_mean_dapper()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=nowhere;Database=none;User Id=x;Password=x;TrustServerCertificate=True")
            .Options;
        Assert.True(AppDbContextOptions.DapperEnabled(options));
    }

    [Fact]
    public void The_runner_is_one_per_context_and_needs_no_logger()
    {
        using var db = SqlServer();
        Assert.Same(db.Sql, db.Sql);
    }

    // ------------------------------------------------------------ helpers

    [Fact]
    public void A_json_list_column_reads_back_as_the_list_and_nothing_as_empty()
    {
        Assert.Equal(["design", "copy"], Sql.JsonList<string>("[\"design\",\"copy\"]"));
        Assert.Empty(Sql.JsonList<string>(null));
        Assert.Empty(Sql.JsonList<string>(""));
        var id = Guid.NewGuid();
        Assert.Equal([id], Sql.JsonList<Guid>($"[\"{id}\"]"));
    }

    [Fact]
    public void A_set_of_ids_travels_as_json()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        Assert.Equal($"[\"{a}\",\"{b}\"]", Sql.JsonIds([a, b]));
        Assert.Equal("[]", Sql.JsonIds([]));
    }

    // ------------------------------------------------- the procedures

    /// <summary>Every procedure the portal calls, with its script.</summary>
    internal static IEnumerable<(string Name, string Script)> Scripts() =>
        Procedures.All.Select(p => (p.Name, p.Definition));

    private static readonly Regex Header = new(
        @"\A(?:\s*--[^\n]*\n)*\s*CREATE OR ALTER PROCEDURE \[dbo\]\.\[(\w+)\]\s*(?<parameters>[^\n]*(?:\n(?!AS\b)[^\n]*)*)\nAS\b",
        RegexOptions.Compiled);

    [Fact]
    public void Every_handle_has_a_script_and_every_script_a_handle()
    {
        var handles = Procedures.All.Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.Equal(handles, StoredProcedures.ScriptNames());
        Assert.Equal(handles.Count, handles.Distinct(StringComparer.Ordinal).Count());
        Assert.True(handles.Count >= 50, "the catalogue lost most of its procedures");
    }

    [Fact]
    public void Standing_Batch_and_the_feed_select_every_column_their_rows_read()
    {
        // Dapper builds a positional record from columns by name, and a
        // column the script forgot is a 500 on the opportunity page, not a
        // compile error: every constructor parameter of the standing
        // reader's rows, and every property of a card row, must be a
        // bracketed name in the script that fills it.
        static HashSet<string> Columns(Procedure p) =>
            Regex.Matches(StoredProcedures.Definition(p), @"\[(\w+)\]").Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

        var standing = Columns(Procedures.StandingBatch);
        foreach (var raw in new[] { "OpportunityRaw", "MilestoneRaw", "EntryRaw", "ClaimRaw", "FileRaw", "ProfileRaw", "SkillRaw" })
        {
            var type = typeof(StandingReader).GetNestedType(raw, BindingFlags.NonPublic)!;
            var ctor = type.GetConstructors().Single(c => c.GetParameters().Length > 0);
            foreach (var parameter in ctor.GetParameters())
                Assert.True(standing.Contains(parameter.Name!), $"Standing_Batch selects no [{parameter.Name}] for {raw}");
        }

        foreach (var p in new[] { Procedures.OpportunityFeed, Procedures.OpportunityOpen })
        {
            var columns = Columns(p);
            foreach (var property in typeof(OpportunityCards.Row).GetProperties())
                if (property.Name is not ("Skills" or "ApplicationCount")) // filled by the second result set and a count of its own
                    Assert.True(columns.Contains(property.Name), $"{p.Name} selects no [{property.Name}]");
        }
    }

    [Fact]
    public void Every_script_creates_or_alters_the_one_procedure_it_is_named_for()
    {
        foreach (var (name, script) in Scripts())
        {
            var m = Header.Match(script);
            Assert.True(m.Success, $"{name}: the script does not begin with CREATE OR ALTER PROCEDURE [dbo].[{name}] … AS");
            Assert.Equal(name, m.Groups[1].Value);
            Assert.Equal(1, Regex.Matches(script, @"\bCREATE\b", RegexOptions.IgnoreCase).Count);
            Assert.Equal($"[dbo].[{name}]", Procedures.All.Single(p => p.Name == name).QualifiedName);
            Assert.DoesNotContain("\nGO", script);
        }
    }

    [Fact]
    public void Every_script_that_reads_profiles_leaves_the_deleted_ones_out()
    {
        foreach (var (name, script) in Scripts())
        {
            if (!script.Contains("[Profiles]")) continue;
            Assert.True(script.Contains("[IsDeleted] = 0"), $"{name} names [Profiles] without [IsDeleted] = 0");
        }
    }

    [Fact]
    public void Every_script_is_t_sql_with_bound_parameters_and_builds_no_sql_of_its_own()
    {
        foreach (var (name, script) in Scripts())
        {
            Assert.False(script.Contains('"'), $"{name} quotes an identifier the Postgres way");
            Assert.False(script.Contains(" IN @"), $"{name} expands a list into parameters; ids travel through OPENJSON");
            Assert.False(script.Contains('{') || script.Contains('}'), $"{name} carries a placeholder nothing fills");
            Assert.False(Regex.IsMatch(script, @"sp_executesql|\bEXEC(?:UTE)?\s*\(", RegexOptions.IgnoreCase), $"{name} runs SQL it built");
            Assert.False(Regex.IsMatch(script, @"\+\s*@\w+|@\w+\s*\+"), $"{name} joins a parameter into text");
        }
    }

    [Fact]
    public void Every_parameter_a_script_uses_is_one_it_declares_and_every_one_it_declares_is_used()
    {
        foreach (var (name, script) in Scripts())
        {
            var m = Header.Match(script);
            var declared = Regex.Matches(m.Groups["parameters"].Value, @"@(\w+)").Select(x => x.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
            var body = script[(m.Index + m.Length)..];
            var locals = Regex.Matches(body, @"DECLARE\s+@(\w+)").Select(x => x.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
            var used = Regex.Matches(body, @"@(\w+)").Select(x => x.Groups[1].Value).Where(p => !locals.Contains(p)).ToHashSet(StringComparer.Ordinal);
            Assert.True(used.IsSubsetOf(declared), $"{name} uses @{string.Join(", @", used.Except(declared))} without declaring it");
            Assert.True(declared.IsSubsetOf(used), $"{name} declares @{string.Join(", @", declared.Except(used))} and never uses it");
        }
    }

    [Fact]
    public void Every_table_a_script_names_is_one_the_model_has()
    {
        using var db = SqlServer();
        var tables = db.Model.GetEntityTypes().Select(e => e.GetTableName()).Where(t => t is not null).ToHashSet();
        foreach (var (name, script) in Scripts())
        foreach (Match m in Regex.Matches(script, @"(?:FROM|JOIN|UPDATE|INTO|DELETE)\s+\[(\w+)\]", RegexOptions.IgnoreCase))
            Assert.True(tables.Contains(m.Groups[1].Value), $"{name} names a table the model has not: [{m.Groups[1].Value}]");
    }

    /// <summary>
    /// SQL Server cuts a value to its parameter's declared width without a
    /// word, so a parameter narrower than the column it is written to or
    /// compared with loses text (SetupTest_Record kept 240 characters of a
    /// 1000-character detail). Every text parameter, and every text column
    /// an OPENJSON row declares, is held to the widest column of that name
    /// in the tables its script names.
    /// </summary>
    [Fact]
    public void No_text_parameter_is_narrower_than_the_column_it_meets()
    {
        using var db = SqlServer();
        var model = db.Model.GetRelationalModel();
        static int Width(string type) =>
            Regex.Match(type, @"varchar\((\d+|max)\)", RegexOptions.IgnoreCase) is { Success: true } m
                ? m.Groups[1].Value.Equals("max", StringComparison.OrdinalIgnoreCase) ? int.MaxValue : int.Parse(m.Groups[1].Value)
                : 0;

        var checks = 0;
        foreach (var (name, script) in Scripts())
        {
            var tables = Regex.Matches(script, @"(?:FROM|JOIN|UPDATE|INTO)\s+\[(\w+)\]", RegexOptions.IgnoreCase)
                .Select(m => model.FindTable(m.Groups[1].Value, null)).OfType<ITable>().Distinct().ToList();
            int ColumnWidth(string column) =>
                tables.Select(t => t.FindColumn(column)?.StoreType).OfType<string>().Select(Width).DefaultIfEmpty(0).Max();

            var header = Header.Match(script).Groups["parameters"].Value;
            var parameters = Regex.Matches(header, @"@(\w+)\s+(n?varchar\((?:\d+|max)\))", RegexOptions.IgnoreCase)
                .ToDictionary(m => m.Groups[1].Value, m => Width(m.Groups[2].Value), StringComparer.Ordinal);

            var meetings = new List<(string Column, string Parameter)>();
            foreach (Match m in Regex.Matches(script, @"\[(\w+)\]\s*=\s*\(?@(\w+)|@(\w+)\s*=\s*(?:\[\w+\]\.)?\[(\w+)\]"))
                meetings.Add(m.Groups[1].Success ? (m.Groups[1].Value, m.Groups[2].Value) : (m.Groups[4].Value, m.Groups[3].Value));
            foreach (Match m in Regex.Matches(script, @"INSERT INTO \[\w+\]\s*\(([^)]*)\)\s*VALUES\s*\(([^)]*)\)", RegexOptions.IgnoreCase))
            {
                var columns = m.Groups[1].Value.Split(',').Select(c => c.Trim().Trim('[', ']')).ToList();
                var values = m.Groups[2].Value.Split(',').Select(v => v.Trim()).ToList();
                for (var i = 0; i < Math.Min(columns.Count, values.Count); i++)
                    if (values[i].StartsWith('@')) meetings.Add((columns[i], values[i][1..]));
            }

            foreach (var (column, parameter) in meetings.Distinct())
            {
                if (!parameters.TryGetValue(parameter, out var declared)) continue;
                var needed = ColumnWidth(column);
                if (needed == 0) continue;
                checks++;
                Assert.True(declared >= needed, $"{name}: @{parameter} holds {declared} characters and [{column}] {needed}");
            }

            // An OPENJSON row's text columns, inserted by name.
            foreach (Match m in Regex.Matches(script, @"\[(\w+)\]\s+(n?varchar\((?:\d+|max)\))\s+'\$", RegexOptions.IgnoreCase))
            {
                var needed = ColumnWidth(m.Groups[1].Value);
                if (needed == 0) continue;
                checks++;
                Assert.True(Width(m.Groups[2].Value) >= needed,
                    $"{name}: the OPENJSON [{m.Groups[1].Value}] holds {Width(m.Groups[2].Value)} characters and the column {needed}");
            }
        }
        Assert.True(checks >= 30, $"only {checks} parameters were matched to a column; the patterns stopped finding them");
    }

    /// <summary>
    /// A procedure whose first statement writes is a write, and its caller
    /// learns how many rows it touched from the count SQL Server sends —
    /// so it must not say NOCOUNT. Every other one reads, and says it.
    /// </summary>
    [Fact]
    public void A_procedure_that_reads_says_nocount_and_one_that_writes_reports_its_rows()
    {
        var writers = 0;
        foreach (var (name, script) in Scripts())
        {
            var first = Regex.Match(script, @"\bBEGIN\s+(?:SET NOCOUNT ON;\s*)?(\w+)").Groups[1].Value.ToUpperInvariant();
            if (first is "UPDATE" or "DELETE" or "INSERT")
            {
                writers++;
                Assert.False(script.Contains("SET NOCOUNT ON"), $"{name} writes, and its caller reads how many rows it touched");
            }
            else
            {
                Assert.True(script.Contains("SET NOCOUNT ON"), $"{name} reads and should say SET NOCOUNT ON");
            }
        }
        Assert.Equal(23, writers);
    }

    [Fact]
    public void The_runner_takes_only_procedures_and_lends_no_connection()
    {
        foreach (var method in typeof(Sql).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            var first = method.GetParameters().FirstOrDefault();
            Assert.True(first?.ParameterType == typeof(Procedure), $"Sql.{method.Name} takes a {first?.ParameterType.Name ?? "nothing"} first; only a Procedure may be run");
        }
        Assert.Empty(typeof(Sql).GetProperties(BindingFlags.Public | BindingFlags.Instance));
        Assert.Null(typeof(Procedure).GetConstructors(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault());
    }

    [Fact]
    public void No_reader_carries_sql_of_its_own_any_more()
    {
        var assemblies = new[] { typeof(LeaderboardService).Assembly, typeof(AppDbContext).Assembly };
        var leftovers = assemblies.SelectMany(a => a.GetTypes()).Where(t => t.IsNested && t.Name == "Tsql").Select(t => t.FullName).ToList();
        Assert.Empty(leftovers);
    }

    // ------------------------------------------ the LINQ beside them

    /// <summary>
    /// A read's LINQ body against a Postgres that answers every query with
    /// no rows (<see cref="EmptyPostgres"/>): EF Core translates and renders
    /// each query in turn, so the read must run to its end. An
    /// untranslatable shape anywhere in it fails saying so. This is the
    /// check that the LINQ still serves Postgres after it was reshaped to
    /// fill the same rows as the procedure.
    /// </summary>
    private static async Task TranslatesOnPostgres(Func<AppDbContext, Task> read)
    {
        using var postgres = new EmptyPostgres();
        var e = await Record.ExceptionAsync(() => read(postgres.Db));
        Assert.True(e is null, $"stopped after {postgres.Commands.Count} queries: {e}");
        Assert.NotEmpty(postgres.Commands);    }

    public static IEnumerable<object[]> LinqReads()
    {
        var id = Guid.NewGuid();
        var ids = new List<Guid> { id };
        var now = DateTimeOffset.UtcNow;
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var ct = CancellationToken.None;
        Func<AppDbContext, Task>[] reads =
        [
            db => LeaderboardService.UsersLinqAsync(db, ct),
            db => LeaderboardService.ProfilesLinqAsync(db, ids, ct),
            db => LeaderboardService.HistoryLinqAsync(db, ids, today.AddDays(-30), today, ct),
            db => MeritReader.RecordLinqAsync(db, id, now, ct),
            db => MeritReader.MeritReadsLinqAsync(db, ids, now, ct),
            db => StandingReader.ReadLinqAsync(db, ids, ct),
            db => StandingReader.PeopleLinqAsync(db, ids, ids, ct),
            db => Welcome.AccountLinqAsync(db, id, ct),
            db => Welcome.ProfileLinqAsync(db, id, ct),
            db => Welcome.FreelancersLinqAsync(db, ct),
            db => Welcome.PeersLinqAsync(db, "software", ids, ct),
            db => ProfileReview.AccountLinqAsync(db, id, ct),
            db => ProfileReview.ProfileLinqAsync(db, id, ct),
            db => ProfileReview.OpenLinqAsync(db, ct),
            db => DashboardService.ClientReadsLinqAsync(db, id, now, ct),
            db => DashboardService.FreelancerReadsLinqAsync(db, id, now, ct),
            db => DashboardService.WaitingLinqAsync(db, id, ct),
            db => DashboardService.WaitingLinqAsync(db, null, ct),
            db => DashboardService.AdminReadsLinqAsync(db, now, now.AddDays(-30), ct),
            db => ReportService.OpportunityFiguresLinqAsync(db, ids, ct),
            db => ReportService.MyPartsLinqAsync(db, ids, id, ct),
            db => ReportService.ApplicationOutcomesLinqAsync(db, ids, ct),
            db => ReportService.EntryFactsLinqAsync(db, ids, ids, ct),
            db => SetupUsage.FileCountsLinqAsync(db, ct),
            db => CategoryCatalog.ListLinqAsync(db, ct),
            db => CategoryAdminService.UsageLinqAsync(db, ct),
            db => AdminService.OperationsLinqAsync(db, ct),
            db => OpportunityCards.FeedLinqAsync(db, Feed(), ct),
            db => OpportunityCards.FeedLinqAsync(db, Feed() with { OpenOnly = false, Q = "inventory", CategoryKey = "software", SubcategoryKey = "web" }, ct),
            db => OpportunityCards.FeedLinqAsync(db, Feed() with { Ending = true, Cursor = new Cursor(now, id) }, ct),
            db => OpportunityCards.FeedLinqAsync(db, Feed() with { ByAward = true, Cursor = new Cursor(now, id, 500m) }, ct),
            db => OpportunityCards.FeedLinqAsync(db, Feed() with { Cursor = new Cursor(now, id) }, ct),
            db => OpportunityCards.OpenLinqAsync(db, now, ct),
            db => FitReader.ProfileLinqAsync(db, id, ct),
            db => FitReader.EntriesLinqAsync(db, id, ct),
            db => AiService.ModeratedLinqAsync(db, id, id, false, ct),
            db => AiService.EntryGateLinqAsync(db, id, id, ct),
            db => NotificationService.InboxLinqAsync(db, id, null, 21, ct),
            db => NotificationService.InboxLinqAsync(db, id, new Cursor(now, id), 21, ct),
            db => ChatService.ThreadsLinqAsync(db, id, 200, ct),
            db => ChatService.ThreadLinqAsync(db, id, id, ct),
            db => ChatService.PageLinqAsync(db, id, new Cursor(now, id), 51, ct),
            db => ChatModerationService.ListLinqAsync(db, null, null, false, null, 51, ct),
            db => ChatModerationService.ListLinqAsync(db, "%sam%", id, true, new Cursor(now, id), 51, ct),
            db => ChatModerationService.ReportsLinqAsync(db, id, ct),
            db => ChatModerationService.OpenReportsLinqAsync(db, ct),
            db => ChatModerationService.OneLinqAsync(db, id, ct),
        ];
        return reads.Select((r, i) => new object[] { i, r });

        OpportunityCards.FeedQuery Feed() => new(
            OpenOnly: true, Q: null, CategoryKey: null, SubcategoryKey: null, Ending: false, ByAward: false,
            Cursor: null, Take: 21, Now: now);
    }

    [Theory]
    [MemberData(nameof(LinqReads))]
    public Task Every_linq_read_beside_a_procedure_still_translates_on_postgres(int index, Func<AppDbContext, Task> read)
    {
        _ = index;
        return TranslatesOnPostgres(read);
    }

    [Fact]
    public async Task The_translation_check_reaches_a_reads_later_queries()
    {
        // The shape that first sent Category_Usage's twin to a 500: EF Core
        // cannot flatten the text[] column. Behind a query that translates,
        // a refused connection never got this far.
        using var postgres = new EmptyPostgres();
        var e = await Record.ExceptionAsync(async () =>
        {
            await postgres.Db.Opportunities.CountAsync();
            await postgres.Db.Profiles.SelectMany(p => p.SecondaryCategories).GroupBy(k => k)
                .Select(g => new { g.Key, Count = g.Count() }).ToListAsync();
        });
        Assert.Contains("could not be translated", e?.Message);
        Assert.Single(postgres.Commands);
    }

    // --------------------------------------- the feed's parameters

    private static readonly string[] FeedParameterNames =
        ["openOnly", "sort", "now", "take", "words", "pattern", "category", "subcategory", "cursorAt", "cursorId", "cursorAmount"];

    private static OpportunityCards.FeedQuery FeedOf(
        bool open = true, string? q = null, string? category = null, string? sub = null, bool ending = false, bool byAward = false,
        Cursor? cursor = null) =>
        new(open, q, category, sub, ending, byAward, cursor, Take: 21, Now: DateTimeOffset.UtcNow);

    [Fact]
    public void The_plain_feed_is_open_opportunities_newest_first_with_every_filter_null()
    {
        var p = OpportunityCards.FeedParameters(FeedOf());
        Assert.Equal(FeedParameterNames, p.ParameterNames);
        Assert.True(p.Get<bool>("openOnly"));
        Assert.Equal((byte)0, p.Get<byte>("sort"));
        Assert.Equal(21, p.Get<int>("take"));
        Assert.Null(p.Get<string?>("words"));
        Assert.Null(p.Get<string?>("pattern"));
        Assert.Null(p.Get<string?>("category"));
        Assert.Null(p.Get<string?>("subcategory"));
        Assert.Null(p.Get<DateTimeOffset?>("cursorAt"));
        Assert.Null(p.Get<Guid?>("cursorId"));
        Assert.Null(p.Get<decimal?>("cursorAmount"));
    }

    [Fact]
    public void All_opportunities_drops_the_open_condition()
    {
        Assert.False(OpportunityCards.FeedParameters(FeedOf(open: false)).Get<bool>("openOnly"));
    }

    [Fact]
    public void Words_go_through_the_full_text_index_and_the_title_as_typed()
    {
        var p = OpportunityCards.FeedParameters(FeedOf(q: " 50% off "));
        Assert.Equal("50% off", p.Get<string>("words"));
        Assert.Equal("%50\\% off%", p.Get<string>("pattern"));
    }

    [Fact]
    public void A_subcategory_narrows_only_within_its_category()
    {
        var within = OpportunityCards.FeedParameters(FeedOf(category: "software", sub: "web"));
        Assert.Equal("software", within.Get<string>("category"));
        Assert.Equal("web", within.Get<string>("subcategory"));
        var alone = OpportunityCards.FeedParameters(FeedOf(sub: "web"));
        Assert.Null(alone.Get<string?>("category"));
        Assert.Null(alone.Get<string?>("subcategory"));
    }

    [Fact]
    public void Each_order_has_its_own_number_and_keyset()
    {
        var id = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow;
        var newest = OpportunityCards.FeedParameters(FeedOf(cursor: new Cursor(at, id)));
        Assert.Equal((byte)0, newest.Get<byte>("sort"));
        Assert.Equal(at, newest.Get<DateTimeOffset?>("cursorAt"));
        Assert.Equal(id, newest.Get<Guid?>("cursorId"));
        Assert.Null(newest.Get<decimal?>("cursorAmount"));

        var ending = OpportunityCards.FeedParameters(FeedOf(ending: true, cursor: new Cursor(at, id)));
        Assert.Equal((byte)1, ending.Get<byte>("sort"));
        Assert.Equal(id, ending.Get<Guid?>("cursorId"));
        Assert.Null(ending.Get<decimal?>("cursorAmount"));

        var byAward = OpportunityCards.FeedParameters(FeedOf(byAward: true, cursor: new Cursor(at, id, 750m)));
        Assert.Equal((byte)2, byAward.Get<byte>("sort"));
        Assert.Equal(750m, byAward.Get<decimal?>("cursorAmount"));

        // An award cursor without an amount reads as nought, as the LINQ does.
        Assert.Equal(0m, OpportunityCards.FeedParameters(FeedOf(byAward: true, cursor: new Cursor(at, id))).Get<decimal?>("cursorAmount"));
        // The order wins over the ending flag, as the LINQ does.
        Assert.Equal((byte)2, OpportunityCards.FeedParameters(FeedOf(ending: true, byAward: true)).Get<byte>("sort"));
    }

    [Fact]
    public void The_feeds_parameters_are_the_procedures()
    {
        var declared = Regex.Matches(Header.Match(Procedures.OpportunityFeed.Definition).Groups["parameters"].Value, @"@(\w+)")
            .Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(FeedParameterNames.ToHashSet(StringComparer.Ordinal), declared);
    }

    // ---------------------------------------- the activity log's filter

    private static readonly string[] ActivityParameterNames =
        ["skip", "take", "userId", "visitor", "kind", "service", "members", "since", "until", "q", "aiProvider", "aiModel"];

    private static ActivityService.ActivityFilter Filter(
        Guid? user = null, string? visitor = null, string? kind = null, string? who = null, string? q = null,
        DateTimeOffset? from = null, DateTimeOffset? to = null, string? service = null, string? aiProvider = null,
        string? aiModel = null) => new(user, visitor, kind, who, q, from, to, service, aiProvider, aiModel);

    [Fact]
    public void An_empty_filter_applies_nothing_but_the_page()
    {
        var p = ActivityService.Parameters(Filter(), 50, 25);
        Assert.Equal(ActivityParameterNames, p.ParameterNames);
        Assert.Equal(50, p.Get<int>("skip"));
        Assert.Equal(25, p.Get<int>("take"));
        Assert.Null(p.Get<Guid?>("userId"));
        Assert.Null(p.Get<string?>("visitor"));
        Assert.Null(p.Get<string?>("kind"));
        Assert.Null(p.Get<bool?>("members"));
        Assert.Null(p.Get<DateTimeOffset?>("since"));
        Assert.Null(p.Get<DateTimeOffset?>("until"));
        Assert.Null(p.Get<string?>("q"));
        Assert.Null(p.Get<string?>("aiProvider"));
        Assert.Null(p.Get<string?>("aiModel"));
    }

    [Fact]
    public void Every_filter_becomes_one_bound_parameter()
    {
        var user = Guid.NewGuid();
        var from = DateTimeOffset.UtcNow.AddDays(-1);
        var to = DateTimeOffset.UtcNow;
        var p = ActivityService.Parameters(Filter(user, "0123456789abcdef", ActivityKinds.Visit, "members", "50% off_[x]", from, to), 0, 50);
        Assert.Equal(user, p.Get<Guid?>("userId"));
        Assert.Equal("0123456789abcdef", p.Get<string>("visitor"));
        Assert.Equal(ActivityKinds.Visit, p.Get<string>("kind"));
        Assert.True(p.Get<bool?>("members"));
        Assert.Equal(from, p.Get<DateTimeOffset?>("since"));
        Assert.Equal(to, p.Get<DateTimeOffset?>("until"));
        // The typed words become the LIKE pattern with its wildcards escaped, as on the feed.
        Assert.Equal("%50\\% off\\_\\[x]%", p.Get<string>("q"));
    }

    [Theory]
    [InlineData("visitors", false)]
    [InlineData("members", true)]
    [InlineData("everyone", null)]
    [InlineData(null, null)]
    public void Who_narrows_to_accounts_or_none(string? who, bool? expected)
    {
        Assert.Equal(expected, ActivityService.Parameters(Filter(who: who), 0, 50).Get<bool?>("members"));
    }

    [Fact]
    public void A_visitor_id_that_is_not_one_a_kind_that_is_not_one_and_blank_words_are_not_applied()
    {
        var p = ActivityService.Parameters(Filter(visitor: "not-a-visitor", kind: "other", q: "  "), 0, 50);
        Assert.Null(p.Get<string?>("visitor"));
        Assert.Null(p.Get<string?>("kind"));
        Assert.Null(p.Get<string?>("q"));
    }

    [Fact]
    public void Third_party_calls_are_a_kind_and_a_service_narrows_them()
    {
        var p = ActivityService.Parameters(Filter(kind: ActivityKinds.External, service: "identity"), 0, 50);
        Assert.Equal(ActivityKinds.External, p.Get<string>("kind"));
        Assert.Equal("identity", p.Get<string>("service"));
        // A service the portal does not call is not a filter.
        Assert.Null(ActivityService.Parameters(Filter(service: "stripe"), 0, 50).Get<string?>("service"));
    }

    [Fact]
    public void An_AI_call_narrows_by_provider_and_by_one_exact_model()
    {
        var p = ActivityService.Parameters(Filter(aiProvider: "OpenAI", aiModel: " openai/gpt-5.5 "), 0, 50);
        Assert.Equal("openai/%", p.Get<string>("aiProvider"));
        Assert.Equal("openai/gpt-5.5", p.Get<string>("aiModel"));
        // A provider the portal does not call, and a model without its provider, are not filters.
        var q = ActivityService.Parameters(Filter(aiProvider: "mistral", aiModel: "gpt-5.5"), 0, 50);
        Assert.Null(q.Get<string?>("aiProvider"));
        Assert.Null(q.Get<string?>("aiModel"));
    }

    [Fact]
    public void The_activity_logs_parameters_are_the_procedures()
    {
        var declared = Regex.Matches(Header.Match(Procedures.ActivityPage.Definition).Groups["parameters"].Value, @"@(\w+)")
            .Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(ActivityParameterNames.ToHashSet(StringComparer.Ordinal), declared);
    }

    // --------------------------------------------- the batches' JSON

    [Fact]
    public void A_days_snapshots_go_in_as_json_with_the_day_as_a_date()
    {
        var id = Guid.NewGuid();
        var json = MeritSnapshotWorker.RowsJson([new MeritSnapshot { UserId = id, DayUtc = new DateOnly(2026, 9, 21), Score = 42 }]);
        Assert.Equal($"[{{\"UserId\":\"{id}\",\"DayUtc\":\"2026-09-21\",\"Score\":42}}]", json);
    }

    [Fact]
    public void A_batch_goes_in_as_the_columns_json_without_the_id()
    {
        var at = new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
        var json = ActivityWriter.RowsJson([new WinnersPortal.Domain.ActivityEvent
        {
            Id = 42, Kind = ActivityKinds.Visit, Method = "GET", Path = "/opportunities", Action = "Browse opportunities", AtUtc = at, Status = 200,
        }]);
        Assert.Contains("\"Path\":\"/opportunities\"", json);
        Assert.Contains("\"AtUtc\":\"2026-09-21T12:00:00+00:00\"", json);
        Assert.Contains("\"UserId\":null", json);
        Assert.DoesNotContain("\"Id\":", json);
    }

    [Fact]
    public void A_third_party_calls_texts_go_in_with_the_batch()
    {
        var json = ActivityWriter.RowsJson([new WinnersPortal.Domain.ActivityEvent
        {
            Kind = ActivityKinds.External, Service = "ai", Method = "POST", Path = "api.openai.com/v1/chat/completions",
            Action = "Called OpenAI", Status = 200, DurationMs = 842, Request = "POST …", Response = "200 OK …", AtUtc = DateTimeOffset.UtcNow,
        }]);
        Assert.Contains("\"Service\":\"ai\"", json);
        Assert.Contains("\"DurationMs\":842", json);
        Assert.Contains("\"Request\":", json);
        Assert.Contains("\"Response\":", json);
    }
}
