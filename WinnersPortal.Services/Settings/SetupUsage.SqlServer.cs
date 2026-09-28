using WinnersPortal.Infrastructure.Data;

namespace WinnersPortal.Services.Settings;

/// <summary>The SQL Server half of <see cref="SetupUsage"/>'s two reads: <c>Setup_FileCounts</c> and <c>Setup_Repos</c>.</summary>
public static partial class SetupUsage
{
    private static Task<List<(string? Setup, int Count)>> FileCountsSqlAsync(Sql sql, CancellationToken ct) =>
        sql.MultipleAsync(Procedures.SetupFileCounts, null, async grid =>
        {
            var rows = new List<(string? Setup, int Count)>();
            for (var i = 0; i < 3; i++)
                rows.AddRange((await grid.ReadAsync<SetupCount>()).Select(r => (r.Setup, r.Count)));
            return rows;
        }, ct);

    private static Task<List<string>> ReposSqlAsync(Sql sql, CancellationToken ct) =>
        sql.QueryAsync<string>(Procedures.SetupRepos, null, ct);
}
