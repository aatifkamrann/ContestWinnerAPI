using WinnersPortal.Services.Ai;
using WinnersPortal.Services.Opportunities;
using Microsoft.EntityFrameworkCore;
using WinnersPortal.Domain;
using WinnersPortal.Infrastructure.Data;

namespace WinnersPortal.Services.Profiles;

/// <summary>
/// The reads behind a merit score: the earned half for one person, and the
/// whole summary for many at once. The arithmetic is <see cref="Merit"/>;
/// this is only where the figures come from. Each read has two bodies
/// feeding one shaping — the LINQ here, which Postgres runs, and the T-SQL
/// in <c>MeritReader.SqlServer.cs</c>, which SQL Server runs through Dapper.
/// </summary>
public static partial class MeritReader
{
    /// <summary>
    /// The half of the score the portal watched happen. Every number here is
    /// counted from rows this person cannot write.
    /// </summary>
    public static Task<Merit.Record> RecordAsync(AppDbContext db, Guid userId, CancellationToken ct) =>
        db.UseDapper ? RecordSqlAsync(db.Sql, userId, ct) : RecordLinqAsync(db, userId, ct);

    internal static async Task<Merit.Record> RecordLinqAsync(AppDbContext db, Guid userId, CancellationToken ct)
    {
        // Entries that were really contested — a withdrawal is not an opportunity
        // entered, and neither is an entry the client removed.
        var entries = await db.Entries.AsNoTracking()
            .Where(e => e.FreelancerId == userId && e.Status == EntryStatus.Active)
            .Select(e => new
            {
                e.Id,
                Claimed = e.Checkpoints.Select(cp => new { cp.Milestone!.DueUtc, cp.ClaimedAtUtc }).ToList(),
                Dated = e.Opportunity!.Milestones.Count(m => m.DueUtc != null),
            })
            .ToListAsync(ct);

        // Only milestones whose opportunity gave them a date can be met on time,
        // and only claims count against them — an unclaimed dated milestone
        // is a miss, which is what makes the ratio worth reading.
        var onTime = entries.Sum(e => e.Claimed.Count(c => c.DueUtc != null && c.ClaimedAtUtc <= c.DueUtc));
        var dated = entries.Sum(e => e.Dated);

        var wins = await db.Awards.CountAsync(a => a.Entry!.FreelancerId == userId, ct);
        var ratings = await db.Ratings.AsNoTracking()
            .Where(r => r.OfUserId == userId)
            .GroupBy(_ => 1)
            .Select(g => new { Count = g.Count(), Sum = g.Sum(r => r.Stars) })
            .SingleOrDefaultAsync(ct);

        return new Merit.Record(
            Entries: entries.Count,
            Wins: wins,
            MilestonesOnTime: onTime,
            MilestonesDated: dated,
            RatingCount: ratings?.Count ?? 0,
            RatingSum: ratings?.Sum ?? 0);
    }

    /// <summary>
    /// Merit for a set of people at once — the entrant list on an opportunity
    /// page, where one query per row would be the shape the blueprint's
    /// performance rules warn about. Four batched reads regardless of how
    /// many entrants there are.
    /// </summary>
    public static async Task<Dictionary<Guid, (int Score, string Band, string? Headline)>> MeritForAsync(
        AppDbContext db, IReadOnlyCollection<Guid> userIds, CancellationToken ct) =>
        (await MeritRowsAsync(db, userIds, ct))
            .ToDictionary(x => x.Key, x => (x.Value.Score, x.Value.Band, x.Value.Headline));

    /// <summary>A summary and the record it was scored from — the leaderboard shows the record's figures beside the score.</summary>
    public sealed record MeritRow(int Score, string Band, string? Headline, Merit.Record Record);

    // ---- the rows the summary is scored from, whichever database read them

    /// <summary>A profile's written half, as counts — the profile is not read, only measured.</summary>
    internal sealed record ProfileRow(
        Guid UserId, string? Headline, string? Bio, string? Location, int? HoursPerWeek, int? YearsExperience,
        int Skills, int Projects, int Linked);

    /// <summary>One contested entry: milestones met on time, milestones that had a date, and whether it won.</summary>
    internal sealed record EntryRow(Guid FreelancerId, int OnTime, int Dated, bool Won);

    /// <summary>A person's ratings, summed.</summary>
    internal sealed record RatingRow(Guid UserId, int Count, int Sum);

    /// <summary>The four batched reads behind <see cref="MeritRowsAsync"/>.</summary>
    internal sealed record MeritReads(
        Dictionary<Guid, ProfileRow> Profiles,
        HashSet<Guid> GithubConnected,
        List<EntryRow> Entries,
        Dictionary<Guid, RatingRow> Ratings);

    /// <summary>The read behind <see cref="MeritForAsync"/>, keeping the earned half's facts.</summary>
    public static async Task<Dictionary<Guid, MeritRow>> MeritRowsAsync(
        AppDbContext db, IReadOnlyCollection<Guid> userIds, CancellationToken ct)
    {
        var result = new Dictionary<Guid, MeritRow>();
        if (userIds.Count == 0) return result;

        var reads = db.UseDapper
            ? await MeritReadsSqlAsync(db.Sql, userIds, ct)
            : await MeritReadsLinqAsync(db, userIds, ct);

        foreach (var id in userIds.Distinct())
        {
            reads.Profiles.TryGetValue(id, out var p);
            reads.Ratings.TryGetValue(id, out var r);
            var mine = reads.Entries.Where(e => e.FreelancerId == id).ToList();

            var portfolio = new Merit.Portfolio(
                HasHeadline: !string.IsNullOrWhiteSpace(p?.Headline),
                HasBio: !string.IsNullOrWhiteSpace(p?.Bio),
                Skills: p?.Skills ?? 0,
                Projects: p?.Projects ?? 0,
                ProjectsWithLinks: p?.Linked ?? 0,
                HasDetails: p is not null
                    && !string.IsNullOrWhiteSpace(p.Location)
                    && p.HoursPerWeek is > 0
                    && p.YearsExperience is not null,
                // A summary is a score and a band; neither moves with the
                // flag, since an unconnected account earns nothing here
                // either way. Only the denominator does, and no summary
                // shows one — so this need not ask the settings.
                GithubConnected: reads.GithubConnected.Contains(id),
                GithubOffered: true);
            var record = new Merit.Record(
                Entries: mine.Count,
                Wins: mine.Count(e => e.Won),
                MilestonesOnTime: mine.Sum(e => e.OnTime),
                MilestonesDated: mine.Sum(e => e.Dated),
                RatingCount: r?.Count ?? 0,
                RatingSum: r?.Sum ?? 0);

            var score = Merit.Score(portfolio, record);
            result[id] = new MeritRow(score, Merit.Band(score), p?.Headline, record);
        }
        return result;
    }

    internal static async Task<MeritReads> MeritReadsLinqAsync(
        AppDbContext db, IReadOnlyCollection<Guid> userIds, CancellationToken ct)
    {
        var profiles = await db.Profiles.AsNoTracking()
            .Where(p => userIds.Contains(p.UserId))
            .Select(p => new ProfileRow(
                p.UserId, p.Headline, p.Bio, p.Location, p.HoursPerWeek, p.YearsExperience,
                p.Skills.Count,
                p.Projects.Count,
                p.Projects.Count(x => x.Url != null || x.RepoUrl != null)))
            .ToDictionaryAsync(p => p.UserId, ct);

        var githubConnected = await db.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id) && u.GithubLogin != null)
            .Select(u => u.Id)
            .ToListAsync(ct);

        var entryRows = await db.Entries.AsNoTracking()
            .Where(e => userIds.Contains(e.FreelancerId) && e.Status == EntryStatus.Active)
            .Select(e => new EntryRow(
                e.FreelancerId,
                e.Checkpoints.Count(cp =>
                    cp.Milestone!.DueUtc != null && cp.ClaimedAtUtc <= cp.Milestone.DueUtc),
                e.Opportunity!.Milestones.Count(m => m.DueUtc != null),
                db.Awards.Any(a => a.EntryId == e.Id)))
            .ToListAsync(ct);

        var ratingRows = await db.Ratings.AsNoTracking()
            .Where(r => userIds.Contains(r.OfUserId))
            .GroupBy(r => r.OfUserId)
            .Select(g => new RatingRow(g.Key, g.Count(), g.Sum(r => r.Stars)))
            .ToDictionaryAsync(r => r.UserId, ct);

        return new MeritReads(profiles, githubConnected.ToHashSet(), entryRows, ratingRows);
    }
}
