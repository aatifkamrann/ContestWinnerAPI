using Board = WinnersPortal.Services.Leaderboard.Leaderboard;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Services.Common;
using WinnersPortal.Services.Opportunities;
using WinnersPortal.Services.Profiles;

namespace WinnersPortal.Services.Leaderboard;

/// <summary>
/// The public Talent page: four panels of four cards over the same members
/// the leaderboard lists, narrowed by six filters. Anonymous under
/// <c>/api/public</c> for the reason the board is — a visitor deciding
/// whether to hire here, or to join, is who it is for, and every card
/// carries only what a member wrote to be read and what the portal
/// recorded about them. A card opens the profile, which stays members-only.
/// </summary>
public sealed class TalentService(AppDbContext db, PublicReads publicReads)
{

    public async Task<Outcome<TalentResponse>> ReadAsync(string? category, string? region, string? merit, string? availability, string? experience, string? wins, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var listing = await LeaderboardService.ListingAsync(publicReads, db, now, ct);
        var members = listing.Members;
        var regions = LeaderboardService.RegionOptions(members);
        var categories = LeaderboardService.CategoryOptions(members);

        // A filter names only what the page offers: a region or a kind
        // of work with somebody on it, a floor from the offered list, a
        // word for availability the profile form uses. Anything else
        // reads as "any" rather than as an error a visitor cannot fix.
        var filter = new Talent.Filter(
            Category: categories.Any(c => c.Key == category) ? category : null,
            Region: regions.Any(r => r.Key == region) ? region : null,
            MinMerit: Talent.FloorOf(merit, Talent.MeritFloors),
            Availability: ProfileRules.ParseAvailability(availability),
            MinYears: Talent.FloorOf(experience, Talent.ExperienceFloors),
            MinWins: Talent.FloorOf(wins, Talent.WinFloors));
        var pool = members.Where(m => Talent.Matches(m, filter)).ToList();

        var leaders = Talent.Leaders(pool, OpportunityCategories.All.Select(c => c.Key))
            .Select(x => CardOf(x.Leader) with
            {
                Category = x.Category,
                CategoryLabel = OpportunityCategories.Find(x.Category)?.Label ?? x.Category,
            });

        return Outcome.Ok(new TalentResponse
        {
            Filters = new TalentFilters
            {
                Category = filter.Category,
                Region = filter.Region,
                Merit = filter.MinMerit,
                Availability = ProfileRules.AvailabilityName(filter.Availability),
                Experience = filter.MinYears,
                Wins = filter.MinWins,
            },
            Matched = pool.Count,
            Total = members.Count,
            TrendDays = Board.TrendDays,
            AsOfUtc = listing.ReadAtUtc,
            Top = Talent.Top(pool).Select(CardOf),
            Rising = Talent.Rising(pool).Select(CardOf),
            Leaders = leaders,
            Champions = Talent.Champions(pool).Select(CardOf),
            Regions = regions,
            Categories = categories,
        });
    }

    private static TalentCard CardOf(Board.Member m) => new(
        UserId: m.UserId,
        Name: m.Name,
        AvatarUrl: m.AvatarUrl,
        Headline: m.Headline,
        Merit: m.Merit,
        Band: Merit.Band(m.Merit),
        Verified: Talent.Verified(m),
        Rating: Board.Rating(m),
        RatingCount: m.RatingCount,
        Delivery: Board.Delivery(m),
        DeliveryOf: m.MilestonesDated,
        Wins: m.Wins,
        Trend: m.Trend);
}
