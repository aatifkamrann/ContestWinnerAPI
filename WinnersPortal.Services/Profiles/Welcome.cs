using Microsoft.EntityFrameworkCore;
using WinnersPortal.Services.Ai;
using WinnersPortal.Services.Opportunities;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Domain;
using WinnersPortal.Services.Settings;

namespace WinnersPortal.Services.Profiles;

/// <summary>
/// The screen after the eighth step of joining: what joining bought. A new
/// member is greeted by name and told where they stand — profile strength,
/// merit score, a rank among every freelancer and among those doing their
/// kind of work, how many open opportunities are recommended to them, and the
/// one of those worth opening first. None of it is a welcome-only number:
/// each is read fresh off the saved profile and the opportunities open today,
/// so the screen a week later says that week’s truth.
///
/// The arithmetic is here as pure functions; the reads are at the foot —
/// the LINQ for Postgres, and in <c>Welcome.SqlServer.cs</c> the T-SQL
/// SQL Server runs through Dapper.
/// </summary>
public static partial class Welcome
{
    /// <summary>
    /// Competition rank: one more than the number of scores above this one,
    /// so equal scores share a rank and there is always a #1. The member’s
    /// own score may be in the list — it is not above itself.
    /// </summary>
    public static int Rank(int score, IEnumerable<int> scores) => 1 + scores.Count(s => s > score);

    /// <summary>
    /// How hard an opportunity pulls on this member: recommended before merely
    /// enterable before shut, and among equals the one that asks for the
    /// most of their skills — an opportunity that names three things they do
    /// is a better first match than one that names none.
    /// </summary>
    public sealed record Pull(int Tier, int SkillsMatched);

    public static Pull Of(Fit fit, int requiredSkills) =>
        new(fit.Recommended ? 2 : fit.CanEnter ? 1 : 0, requiredSkills - fit.MissingSkills.Count);

    /// <summary>
    /// The strongest opportunity among the open opportunities, or null when none
    /// is theirs to enter — a shut door is not an opportunity, however well
    /// it fits. Ties fall to the larger award, then the newer opportunity.
    /// </summary>
    public static T? Strongest<T>(
        IEnumerable<(T Item, Fit Fit, int RequiredSkills, decimal Award, DateTimeOffset? PublishedAtUtc)> rows)
        where T : class =>
        rows.Where(r => r.Fit.CanEnter)
            .Select(r => (Row: r, Pull: Of(r.Fit, r.RequiredSkills)))
            .OrderByDescending(x => x.Pull.Tier)
            .ThenByDescending(x => x.Pull.SkillsMatched)
            .ThenByDescending(x => x.Row.Award)
            .ThenByDescending(x => x.Row.PublishedAtUtc)
            .Select(x => x.Row.Item)
            .FirstOrDefault();

    // ---- the rows the screen is built from, whichever database read them

    internal sealed record AccountRow(
        string DisplayName, string Role, DateTimeOffset? EmailConfirmedAtUtc, DateTimeOffset? PhoneConfirmedAtUtc);

    internal sealed record ProjectRow(string Title, string? Category, string? Description, string? Outcome, string? Role, string? Tech);

    internal sealed record ProfileRow
    {
        public required string? Headline { get; init; }
        public required string? Bio { get; init; }
        public required string? PrimaryCategory { get; init; }
        public required Availability? Availability { get; init; }
        public required int Skills { get; init; }
        /// <summary>In order.</summary>
        public required List<ProjectRow> Projects { get; init; }
    }

    /// <summary>
    /// The whole screen for one member, or null for an account that is
    /// gone. A client gets the greeting alone: the four boxes and the
    /// opportunity are a freelancer’s, and a client’s next step is a brief.
    /// </summary>
    public static async Task<WelcomeView?> ReadAsync(
        AppDbContext db, AiOptions ai, AiWorkSignal signal, Guid userId, CancellationToken ct)
    {
        var account = db.UseDapper ? await AccountSqlAsync(db.Sql, userId, ct) : await AccountLinqAsync(db, userId, ct);
        if (account is null) return null;

        if (account.Role != Roles.Freelancer)
            return new WelcomeView
            {
                DisplayName = account.DisplayName,
                Role = account.Role,
                StrengthPercent = (int?)null,
                MeritScore = (int?)null,
                MeritMax = (int?)null,
                Rank = null,
                OpenOpportunities = 0,
                Matches = 0,
                Strongest = null,
                StrongestWork = null,
            };

        // The same ticks the profile's own checklist counts (ProfileEndpoints
        // builds Strength.Facts off the rows it has already read; this
        // screen reads the few it needs). The payments count is the
        // member's own, which is the one reader the payments rule allows.
        var (profile, payments) = db.UseDapper
            ? await ProfileSqlAsync(db.Sql, userId, ct)
            : await ProfileLinqAsync(db, userId, ct);
        var strength = Strength.Percent(new Strength.Facts(
            Confirmed: account.EmailConfirmedAtUtc is not null || account.PhoneConfirmedAtUtc is not null,
            HasHeadline: !string.IsNullOrWhiteSpace(profile?.Headline),
            HasBio: !string.IsNullOrWhiteSpace(profile?.Bio),
            HasCategory: OpportunityCategories.Find(profile?.PrimaryCategory) is not null,
            Skills: profile?.Skills ?? 0,
            Projects: profile?.Projects.Count ?? 0,
            HasAvailability: profile?.Availability is not null,
            Payments: payments));

        // Every freelancer's score, for the two ranks. Merit is never
        // stored, so this is the batched read the entrant list uses, over
        // everybody at once — four queries however many members there
        // are, and the right shape until a portal has thousands of them.
        var freelancers = db.UseDapper ? await FreelancersSqlAsync(db.Sql, ct) : await FreelancersLinqAsync(db, ct);
        var merit = await MeritReader.MeritForAsync(db, freelancers, ct);
        var mine = merit.TryGetValue(userId, out var m) ? m.Score : 0;
        var category = OpportunityCategories.Find(profile?.PrimaryCategory);
        var peers = category is null
            ? null
            : db.UseDapper
                ? await PeersSqlAsync(db.Sql, category.Key, freelancers, ct)
                : await PeersLinqAsync(db, category.Key, freelancers, ct);

        // The open opportunities, judged the way the feed's Recommended switch
        // judges them — merit floor, required skills, and the model's
        // reading of what kind of work this is, where the portal has one.
        var now = DateTimeOffset.UtcNow;
        var viewer = await FitReader.ForAsync(db, userId, account.Role, ai, signal, ct);
        var open = db.UseDapper
            ? await OpportunityCards.OpenSqlAsync(db.Sql, now, ct)
            : await OpportunityCards.OpenLinqAsync(db, now, ct);
        var judged = open
            .Select(c => (Item: c,
                Fit: viewer!.Judge(
                    c.MinMeritScore, c.Skills, c.Category, Schedule.StartsAt(c.StartsAtUtc, c.PublishedAtUtc), c.DeadlineUtc),
                RequiredSkills: c.Skills.Count, Award: c.AwardAmount, c.PublishedAtUtc))
            .ToList();
        var strongest = Strongest(judged);
        if (strongest is not null) await OpportunityCards.CountApplicationsAsync(db, [strongest], ct);
        // Their own past work that fits it, by the rule the match ring's
        // project line counts with. Their own screen, so work not yet
        // ticked as publishable is theirs to be shown too.
        var work = strongest is null || profile is null
            ? null
            : Assessment.FittingWork(
                profile.Projects.Select(x => new PastWork(
                    x.Title, x.Outcome, ProjectFacts.Of(x.Category, x.Title, x.Description, x.Outcome, x.Role, x.Tech))),
                strongest.Skills, strongest.Category);

        return new WelcomeView
        {
            DisplayName = account.DisplayName,
            Role = account.Role,
            StrengthPercent = (int?)strength,
            MeritScore = (int?)mine,
            MeritMax = (int?)Merit.Max,
            Rank = new WelcomeRank
            {
                Global = Rank(mine, merit.Values.Select(v => v.Score)),
                OfFreelancers = freelancers.Count,
                Category = category is null || peers is null
                    ? null
                    : new CategoryRank
                    {
                        Key = category.Key,
                        Label = category.Label,
                        Rank = Rank(mine, peers.Select(id => merit.TryGetValue(id, out var p) ? p.Score : 0)),
                        Of = peers.Count,
                    },
            },
            OpenOpportunities = open.Count,
            Matches = judged.Count(j => j.Fit.Recommended),
            Strongest = strongest is null ? null : OpportunityCards.Dto(strongest, viewer, now),
            StrongestWork = work is null ? null : new WorkHighlight { Title = work.Title, Outcome = work.Outcome },
        };
    }

    internal static Task<AccountRow?> AccountLinqAsync(AppDbContext db, Guid userId, CancellationToken ct) =>
        db.Users.AsNoTracking()
            .Where(u => u.Id == userId && u.ErasedAtUtc == null)
            .Select(u => new AccountRow(u.DisplayName, u.Role, u.EmailConfirmedAtUtc, u.PhoneConfirmedAtUtc))
            .SingleOrDefaultAsync(ct);

    internal static async Task<(ProfileRow? Profile, int Payments)> ProfileLinqAsync(AppDbContext db, Guid userId, CancellationToken ct)
    {
        var profile = await db.Profiles.AsNoTracking()
            .Where(p => p.UserId == userId)
            .Select(p => new ProfileRow
            {
                Headline = p.Headline, Bio = p.Bio, PrimaryCategory = p.PrimaryCategory, Availability = p.Availability,
                Skills = p.Skills.Count,
                Projects = p.Projects.OrderBy(x => x.Order)
                    .Select(x => new ProjectRow(x.Title, x.Category, x.Description, x.Outcome, x.Role, x.Tech))
                    .ToList(),
            })
            .SingleOrDefaultAsync(ct);
        var payments = await db.ProfilePayments.AsNoTracking().CountAsync(x => x.UserId == userId, ct);
        return (profile, payments);
    }

    internal static Task<List<Guid>> FreelancersLinqAsync(AppDbContext db, CancellationToken ct) =>
        db.Users.AsNoTracking()
            .Where(u => u.Role == Roles.Freelancer && u.ErasedAtUtc == null)
            .Select(u => u.Id)
            .ToListAsync(ct);

    internal static Task<List<Guid>> PeersLinqAsync(AppDbContext db, string categoryKey, List<Guid> freelancers, CancellationToken ct) =>
        db.Profiles.AsNoTracking()
            .Where(p => p.PrimaryCategory == categoryKey && freelancers.Contains(p.UserId))
            .Select(p => p.UserId)
            .ToListAsync(ct);
}
