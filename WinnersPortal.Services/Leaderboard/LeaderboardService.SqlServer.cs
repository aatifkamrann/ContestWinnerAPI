using WinnersPortal.Domain;
using WinnersPortal.Infrastructure.Data;

namespace WinnersPortal.Services.Leaderboard;

/// <summary>
/// The SQL Server half of the board's reads: the LINQ's three queries as
/// the procedures <c>Leaderboard_Users</c>, <c>Leaderboard_Profiles</c>
/// and <c>Leaderboard_History</c>, filling the same rows. The profile's
/// categories column is JSON text, parsed; the snapshot's day is a date,
/// read as a DateTime and narrowed.
/// </summary>
public sealed partial class LeaderboardService
{
    private sealed record ProfileRaw(
        Guid UserId, string? Location, string? TimeZone, string? PrimaryCategory, string? SecondaryCategories,
        int? Availability, int? YearsExperience);

    private sealed record SnapshotRaw(Guid UserId, DateTime DayUtc, int Score);

    private static Task<List<UserRow>> UsersSqlAsync(Sql sql, CancellationToken ct) =>
        sql.QueryAsync<UserRow>(Procedures.LeaderboardUsers, new { role = Roles.Freelancer }, ct);

    private static async Task<Dictionary<Guid, ProfileRow>> ProfilesSqlAsync(Sql sql, List<Guid> ids, CancellationToken ct) =>
        (await sql.QueryAsync<ProfileRaw>(Procedures.LeaderboardProfiles, new { ids = Sql.JsonIds(ids) }, ct))
            .ToDictionary(p => p.UserId, p => new ProfileRow(
                p.UserId, p.Location, p.TimeZone, p.PrimaryCategory, Sql.JsonList<string>(p.SecondaryCategories),
                (Availability?)p.Availability, p.YearsExperience));

    private static async Task<List<SnapshotRow>> HistorySqlAsync(
        Sql sql, List<Guid> ids, DateOnly floor, DateOnly today, CancellationToken ct) =>
        (await sql.QueryAsync<SnapshotRaw>(Procedures.LeaderboardHistory, new { ids = Sql.JsonIds(ids), floor, today }, ct))
            .Select(s => new SnapshotRow(s.UserId, DateOnly.FromDateTime(s.DayUtc), s.Score))
            .ToList();
}
