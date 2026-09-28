using WinnersPortal.Infrastructure.Data;

namespace WinnersPortal.Services.Profiles;

/// <summary>
/// The SQL Server half of <see cref="MeritReader"/>: one person's record
/// from <c>Merit_Record</c>, and many people's reads as the four result
/// sets of <c>Merit_Reads</c> in one round trip. Ids travel as JSON;
/// profiles are read with <c>[IsDeleted] = 0</c>, the filter EF Core
/// applies for free.
/// </summary>
public static partial class MeritReader
{
    private static async Task<Merit.Record> RecordSqlAsync(Sql sql, Guid userId, CancellationToken ct) =>
        await sql.SingleOrDefaultAsync<Merit.Record>(Procedures.MeritRecord, new { userId }, ct)
        ?? new Merit.Record(0, 0, 0, 0, 0, 0);

    private static Task<MeritReads> MeritReadsSqlAsync(Sql sql, IReadOnlyCollection<Guid> userIds, CancellationToken ct) =>
        sql.MultipleAsync(Procedures.MeritReads, new { ids = Sql.JsonIds(userIds) }, async grid =>
        {
            var profiles = (await grid.ReadAsync<ProfileRow>()).ToList();
            var github = (await grid.ReadAsync<Guid>()).ToHashSet();
            var entries = (await grid.ReadAsync<EntryRow>()).ToList();
            var ratings = (await grid.ReadAsync<RatingRow>()).ToList();
            return new MeritReads(
                profiles.ToDictionary(x => x.UserId),
                github,
                entries,
                ratings.ToDictionary(x => x.UserId));
        }, ct);
}
