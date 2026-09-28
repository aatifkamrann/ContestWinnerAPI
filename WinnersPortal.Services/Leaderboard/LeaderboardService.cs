using Board = WinnersPortal.Services.Leaderboard.Leaderboard;
using Microsoft.EntityFrameworkCore;
using WinnersPortal.Domain;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Services.Common;
using WinnersPortal.Services.Opportunities;
using WinnersPortal.Services.Profiles;

namespace WinnersPortal.Services.Leaderboard;

/// <summary>
/// The public leaderboard. Anonymous, under <c>/api/public</c> like the
/// branding and the terms: a visitor deciding whether to join is exactly
/// who it is for, and nothing on it is anybody's private data — the name,
/// title and location a member wrote to be read, and the figures the
/// portal recorded about them. The profile a row links to stays
/// members-only; the board is the shop window, not the shop.
/// </summary>
public sealed partial class LeaderboardService(AppDbContext db, PublicReads publicReads)
{
    /// <summary>Everybody the board may list, and when the database said so.</summary>
    internal sealed record Listing(List<Board.Member> Members, DateTimeOffset ReadAtUtc);

    /// <summary>
    /// <see cref="MembersAsync"/>, held for a few seconds (PublicReads): the
    /// one expensive read the board and the Talent page make, and the same
    /// for every tab, filter and visitor. Keyed by the day as well, so the
    /// trend window moves at midnight whatever was held a second before.
    /// </summary>
    internal static Task<Listing> ListingAsync(PublicReads reads, AppDbContext db, DateTimeOffset now, CancellationToken ct) =>
        reads.GetAsync(("board.members", DateOnly.FromDateTime(now.UtcDateTime)), db,
            async (reader, token) => new Listing(await MembersAsync(reader, now, token), now), ct);

    /// <summary>The regions with somebody on them, in the order the picker lists them.</summary>
    internal static List<PickerOption> RegionOptions(IReadOnlyCollection<Board.Member> members) =>
        Board.Regions
            .Select(r => new PickerOption(r.Key, r.Label, members.Count(m => m.Region == r.Key)))
            .Where(r => r.Count > 0)
            .ToList();

    /// <summary>The kinds of work with somebody on them, in the order the portal lists them.</summary>
    internal static List<PickerOption> CategoryOptions(IReadOnlyCollection<Board.Member> members) =>
        OpportunityCategories.All
            .Select(c => new PickerOption(c.Key, c.Label, members.Count(m => m.Categories.Contains(c.Key))))
            .Where(c => c.Count > 0)
            .ToList();

    public async Task<Outcome<LeaderboardResponse>> ReadAsync(string? tab, string? by, string? region, string? category, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var listing = await ListingAsync(publicReads, db, now, ct);
        var members = listing.Members;
        var chosenTab = Board.TabOf(tab);
        var chosenBy = Board.RankByOf(by);

        // The pickers' options, with how many each would list, so the
        // page never offers a region or a kind of work with nobody on
        // it — and the tab that needs one opens on the fullest.
        var regions = RegionOptions(members);
        var categories = CategoryOptions(members);
        var regionKey = regions.Any(r => r.Key == region) ? region
            : chosenTab == Board.Tab.Regional ? regions.OrderByDescending(r => r.Count).FirstOrDefault()?.Key
            : null;
        var categoryKey = categories.Any(c => c.Key == category) ? category
            : chosenTab == Board.Tab.Category ? categories.OrderByDescending(c => c.Count).FirstOrDefault()?.Key
            : null;

        var listed = Board.Order(
            members.Where(m => Board.Listed(m, chosenTab, regionKey, categoryKey, now)), chosenBy);

        // The podium: the tab's top three by merit, whatever orders the
        // table under it. Merit is the one score every board shares, and
        // a podium that reshuffled with the ordering would say three
        // different things about the same three people.
        var byMerit = Board.Order(listed, Board.RankBy.Merit);
        var podium = byMerit.Take(3).Select(m => new PodiumPlace
        {
            Rank = Board.Rank(m, byMerit, Board.RankBy.Merit),
            UserId = m.UserId,
            Name = m.Name,
            AvatarUrl = m.AvatarUrl,
            Headline = m.Headline,
            Merit = m.Merit,
            Band = Merit.Band(m.Merit),
        });

        return Outcome.Ok(new LeaderboardResponse
        {
            Tab = Board.Key(chosenTab),
            By = Board.Key(chosenBy),
            Region = regionKey,
            Category = categoryKey,
            TrendDays = Board.TrendDays,
            RisingDays = Board.RisingDays,
            AsOfUtc = listing.ReadAtUtc,
            Total = listed.Count,
            Podium = podium,
            Rows = listed.Take(Board.MaxRows).Select(m => new LeaderboardRow
            {
                Rank = Board.Rank(m, listed, chosenBy),
                UserId = m.UserId,
                Name = m.Name,
                AvatarUrl = m.AvatarUrl,
                Headline = m.Headline,
                Location = m.Location,
                Merit = m.Merit,
                Band = Merit.Band(m.Merit),
                Rating = Board.Rating(m),
                RatingCount = m.RatingCount,
                Delivery = Board.Delivery(m),
                DeliveryOf = m.MilestonesDated,
                Wins = m.Wins,
                Trend = m.Trend,
                JoinedAtUtc = m.JoinedAtUtc,
            }),
            Regions = regions,
            Categories = categories,
        });
    }

    /// <summary>
    /// Every freelancer the board may list, with the facts the tabs and the
    /// figures read. A member is on the board once they have confirmed the
    /// account and earned a score at all; a locked or erased account is
    /// not. Batched reads however many members there are — the merit read
    /// the entrant list and the Welcome screen share, plus the profile's
    /// place and kinds of work, plus the snapshots in the trend window.
    /// </summary>
    internal static async Task<List<Board.Member>> MembersAsync(
        AppDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var users = db.UseDapper ? await UsersSqlAsync(db.Sql, ct) : await UsersLinqAsync(db, ct);
        var ids = users.Select(u => u.Id).ToList();
        if (ids.Count == 0) return [];

        var merit = await MeritReader.MeritRowsAsync(db, ids, ct);
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var floor = today.AddDays(-Board.TrendDays);
        var profiles = db.UseDapper
            ? await ProfilesSqlAsync(db.Sql, ids, ct)
            : await ProfilesLinqAsync(db, ids, ct);
        var history = (db.UseDapper
                ? await HistorySqlAsync(db.Sql, ids, floor, today, ct)
                : await HistoryLinqAsync(db, ids, floor, today, ct))
            .ToLookup(s => s.UserId, s => (s.DayUtc, s.Score));

        var members = new List<Board.Member>(users.Count);
        foreach (var u in users)
        {
            if (!merit.TryGetValue(u.Id, out var m) || m.Score == 0) continue;
            profiles.TryGetValue(u.Id, out var p);
            var categories = new List<string>();
            if (OpportunityCategories.Find(p?.PrimaryCategory) is { } primary) categories.Add(primary.Key);
            foreach (var key in p?.SecondaryCategories ?? [])
                if (OpportunityCategories.Find(key) is { } secondary && !categories.Contains(secondary.Key))
                    categories.Add(secondary.Key);

            members.Add(new Board.Member(
                UserId: u.Id,
                Name: u.DisplayName,
                AvatarUrl: AvatarRules.Url(u.Id, u.AvatarUpdatedAtUtc),
                Headline: string.IsNullOrWhiteSpace(m.Headline) ? null : m.Headline,
                Location: string.IsNullOrWhiteSpace(p?.Location) ? null : p!.Location,
                Region: Board.RegionOf(p?.TimeZone),
                Categories: categories,
                Merit: m.Score,
                RatingCount: m.Record.RatingCount,
                RatingSum: m.Record.RatingSum,
                MilestonesOnTime: m.Record.MilestonesOnTime,
                MilestonesDated: m.Record.MilestonesDated,
                Wins: m.Record.Wins,
                Trend: Board.Trend(m.Score, history[u.Id], today),
                JoinedAtUtc: u.CreatedAtUtc,
                Availability: p?.Availability,
                YearsExperience: p?.YearsExperience));
        }
        return members;
    }

    // ---- the three reads behind the members, whichever database ran them;
    // the LINQ is here, the T-SQL in LeaderboardService.SqlServer.cs.

    /// <summary>A freelancer the board may list.</summary>
    internal sealed record UserRow(Guid Id, string DisplayName, DateTimeOffset? AvatarUpdatedAtUtc, DateTimeOffset CreatedAtUtc);

    /// <summary>The profile facts the tabs and pickers read.</summary>
    internal sealed record ProfileRow(
        Guid UserId, string? Location, string? TimeZone, string? PrimaryCategory, List<string> SecondaryCategories,
        Availability? Availability, int? YearsExperience);

    /// <summary>One day's score, for the trend.</summary>
    internal sealed record SnapshotRow(Guid UserId, DateOnly DayUtc, int Score);

    internal static Task<List<UserRow>> UsersLinqAsync(AppDbContext db, CancellationToken ct) =>
        db.Users.AsNoTracking()
            .Where(u => u.Role == Roles.Freelancer && u.ErasedAtUtc == null && u.LockedAtUtc == null
                && (u.EmailConfirmedAtUtc != null || u.PhoneConfirmedAtUtc != null))
            .Select(u => new UserRow(u.Id, u.DisplayName, u.AvatarUpdatedAtUtc, u.CreatedAtUtc))
            .ToListAsync(ct);

    internal static Task<Dictionary<Guid, ProfileRow>> ProfilesLinqAsync(AppDbContext db, List<Guid> ids, CancellationToken ct) =>
        db.Profiles.AsNoTracking()
            .Where(p => ids.Contains(p.UserId) && !p.IsDeleted)
            .Select(p => new ProfileRow(
                p.UserId, p.Location, p.TimeZone, p.PrimaryCategory, p.SecondaryCategories, p.Availability, p.YearsExperience))
            .ToDictionaryAsync(p => p.UserId, ct);

    internal static Task<List<SnapshotRow>> HistoryLinqAsync(
        AppDbContext db, List<Guid> ids, DateOnly floor, DateOnly today, CancellationToken ct) =>
        db.MeritSnapshots.AsNoTracking()
            .Where(s => ids.Contains(s.UserId) && s.DayUtc >= floor && s.DayUtc < today)
            .Select(s => new SnapshotRow(s.UserId, s.DayUtc, s.Score))
            .ToListAsync(ct);
}
