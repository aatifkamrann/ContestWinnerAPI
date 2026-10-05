using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WinnersPortal.Services.Opportunities;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Domain;

namespace WinnersPortal.Services.Profiles;

/// <summary>
/// The improvements box on a freelancer's own profile review, and the
/// arithmetic under it. Match potential is the share of the open opportunities
/// this profile can enter — the same door the browse page reads, a merit
/// floor and a list of required skills — and each candidate change is
/// worth exactly the share it would newly open: a skill the opportunities ask
/// for, or a merit lever (a title, an About, more projects) that reaches a
/// floor. The figures are computed here and nowhere else; the model is
/// handed them and asked only for the words, and cannot change one.
/// </summary>
public static partial class ProfileReview
{
    /// <summary>Lines in the box — the three best, by what they open.</summary>
    public const int MaxLines = 3;

    /// <summary>One open opportunity's door, as the arithmetic reads it.</summary>
    public sealed record Door(int MinMerit, IReadOnlyList<(string Name, string Key)> Skills);

    /// <summary>
    /// One change and what it is worth. <see cref="Gain"/> is the share of
    /// the open opportunities it would newly open, in whole percentage points;
    /// <see cref="Change"/> and <see cref="Because"/> are the portal's own
    /// words for it, shown as they are until the model's arrive.
    /// </summary>
    public sealed record Candidate(string Id, string Change, string Because, int Gain);

    public sealed record Facts(
        string? Headline,
        bool HasAbout,
        string? PrimaryCategory,
        IReadOnlyList<string> OtherCategories,
        IReadOnlyList<(string Name, string Level, int Years)> Skills,
        int Projects,
        int ProjectsWithLinks,
        IReadOnlyList<string> ProjectKinds,
        bool HasAvailability,
        int? HoursPerWeek,
        int? YearsExperience,
        bool HasPayout,
        int StrengthPercent,
        IReadOnlyList<string> StepsNotDone,
        int OpenOpportunities,
        int OpenOpportunitiesEnterable,
        /// <summary>Every change that opens something, best first. The box shows the first <see cref="MaxLines"/>.</summary>
        IReadOnlyList<Candidate> Candidates);

    /// <summary>
    /// The arithmetic. Pure, so it can be pinned: give it the written half
    /// of a merit score, the record, the profile's skills in comparison form
    /// and the open doors, and it says what each change would open.
    /// </summary>
    public static IReadOnlyList<Candidate> Candidates(
        Merit.Portfolio portfolio, Merit.Record record, IReadOnlySet<string> mine, IReadOnlyList<Door> open)
    {
        if (open.Count == 0) return [];
        var score = Merit.Score(portfolio, record);
        int Enterable(int s, IReadOnlySet<string> skills) =>
            open.Count(d => d.MinMerit <= s && d.Skills.All(x => skills.Contains(x.Key)));
        var now = Enterable(score, mine);
        // A change that opens one opportunity of two hundred is still a change
        // that opens one: it rounds up to a point rather than away to nothing.
        int Share(int more) => more <= 0 ? 0 : Math.Max(1, (int)Math.Round(100.0 * more / open.Count));
        var list = new List<Candidate>();

        // ---- skills: each one asked for and lacking, and — where a door
        // lists two or three the profile lacks — that set together, because
        // adding one of a pair opens nothing and would read as worthless.
        var lacking = open
            .Select(d => d.Skills.Where(s => !mine.Contains(s.Key)).ToList())
            .Where(l => l.Count > 0)
            .ToList();
        var singles = lacking.SelectMany(l => l)
            .GroupBy(s => s.Key, StringComparer.Ordinal)
            .Select(g => new[] { g.First() });
        var together = lacking
            .Where(l => l.Count is 2 or 3)
            .GroupBy(l => string.Join("+", l.Select(s => s.Key).Order(StringComparer.Ordinal)), StringComparer.Ordinal)
            .Select(g => g.First().ToArray());
        foreach (var set in singles.Concat(together))
        {
            var with = new HashSet<string>(mine, StringComparer.Ordinal);
            foreach (var s in set) with.Add(s.Key);
            var more = Enterable(score, with) - now;
            if (more <= 0) continue;
            var asks = open.Count(d => set.All(s => d.Skills.Any(x => x.Key == s.Key)));
            var names = set.Select(s => s.Name).ToList();
            list.Add(new Candidate(
                "skill:" + string.Join("+", set.Select(s => s.Key)),
                $"Add {Join(names)} to your skills",
                $"{Count(asks, "open opportunity", "open opportunities")} require{(asks == 1 ? "s" : "")} "
                    + (names.Count == 1 ? "it" : names.Count == 2 ? "both" : "all three"),
                Share(more)));
        }

        // ---- merit levers: each part of the written half with headroom,
        // worth what Merit says it is worth, and a line only where the
        // points reach a floor some open opportunity set.
        void Lever(string id, string change, Merit.Portfolio after)
        {
            var delta = Merit.PortfolioScore(after) - Merit.PortfolioScore(portfolio);
            if (delta <= 0) return;
            var more = Enterable(score + delta, mine) - now;
            if (more <= 0) return;
            list.Add(new Candidate(
                id, change,
                $"worth {Count(delta, "merit point")}, enough for {Count(more, "more open opportunity", "more open opportunities")}",
                Share(more)));
        }
        if (!portfolio.HasHeadline)
            Lever("title", "Add a professional title", portfolio with { HasHeadline = true });
        if (!portfolio.HasBio)
            Lever("about", "Write an About You", portfolio with { HasBio = true });
        if (portfolio.Skills < Merit.SkillsCounted)
            Lever("skills", $"List {Count(Merit.SkillsCounted - portfolio.Skills, "more skill")}",
                portfolio with { Skills = Merit.SkillsCounted });
        if (portfolio.Projects < Merit.ProjectsCounted)
            Lever("projects",
                portfolio.Projects == 0
                    ? $"Add {Count(Merit.ProjectsCounted, "portfolio project")}"
                    : $"Add {Count(Merit.ProjectsCounted - portfolio.Projects, "more portfolio project")}",
                portfolio with { Projects = Merit.ProjectsCounted });
        var linkable = Math.Min(Merit.LinksCounted, portfolio.Projects);
        if (portfolio.ProjectsWithLinks < linkable)
            Lever("links",
                linkable - portfolio.ProjectsWithLinks == 1
                    ? "Add a link or repository to one of your projects"
                    : $"Add a link or repository to {linkable - portfolio.ProjectsWithLinks} of your projects",
                portfolio with { ProjectsWithLinks = linkable });
        if (!portfolio.HasDetails)
            Lever("details", "Fill in your location, hours a week and years of experience",
                portfolio with { HasDetails = true });
        if (portfolio.GithubOffered && !portfolio.GithubConnected)
            Lever("github", "Connect your GitHub account", portfolio with { GithubConnected = true });

        return list.OrderByDescending(c => c.Gain).ThenBy(c => c.Id, StringComparer.Ordinal).ToList();
    }

    // ---- the rows the review is built from, whichever database read them;
    // the LINQ is at the foot, the T-SQL in ProfileReview.SqlServer.cs.

    internal sealed record AccountRow(string? GithubLogin, DateTimeOffset? EmailConfirmedAtUtc, DateTimeOffset? PhoneConfirmedAtUtc);

    internal sealed record SkillRow(string Name, SkillLevel Level, int Years);

    internal sealed record ProjectRow(string? Category, string? Url, string? RepoUrl);

    internal sealed record ProfileRow
    {
        public required string? Headline { get; init; }
        public required string? Bio { get; init; }
        public required string? Location { get; init; }
        public required string? PrimaryCategory { get; init; }
        public required List<string> SecondaryCategories { get; init; }
        public required Availability? Availability { get; init; }
        public required int? HoursPerWeek { get; init; }
        public required int? YearsExperience { get; init; }
        /// <summary>In order.</summary>
        public required List<SkillRow> Skills { get; init; }
        public required List<ProjectRow> Projects { get; init; }
    }

    /// <summary>Null only when the account is gone — a blank profile still has a review, mostly of gaps.</summary>
    public static async Task<Facts?> ReadAsync(AppDbContext db, Guid userId, bool githubOffered, CancellationToken ct)
    {
        var account = db.UseDapper ? await AccountSqlAsync(db.Sql, userId, ct) : await AccountLinqAsync(db, userId, ct);
        if (account is null) return null;

        // The profile, and how many ways to be paid — a count and nothing
        // else: the ciphertext is never read here.
        var (profile, payments) = db.UseDapper
            ? await ProfileSqlAsync(db.Sql, userId, ct)
            : await ProfileLinqAsync(db, userId, ct);

        var portfolio = new Merit.Portfolio(
            HasHeadline: !string.IsNullOrWhiteSpace(profile?.Headline),
            HasBio: !string.IsNullOrWhiteSpace(profile?.Bio),
            Skills: profile?.Skills.Count ?? 0,
            Projects: profile?.Projects.Count ?? 0,
            ProjectsWithLinks: profile?.Projects.Count(x => x.Url != null || x.RepoUrl != null) ?? 0,
            HasDetails: profile is not null
                && !string.IsNullOrWhiteSpace(profile.Location)
                && profile.HoursPerWeek is > 0
                && profile.YearsExperience is not null,
            GithubConnected: account.GithubLogin is not null,
            GithubOffered: githubOffered);
        var record = await MeritReader.RecordAsync(db, userId, ct);
        var score = Merit.Score(portfolio, record);

        var strength = new Strength.Facts(
            Confirmed: account.EmailConfirmedAtUtc is not null || account.PhoneConfirmedAtUtc is not null,
            HasHeadline: portfolio.HasHeadline,
            HasBio: portfolio.HasBio,
            HasCategory: OpportunityCategories.Find(profile?.PrimaryCategory) is not null,
            Skills: portfolio.Skills,
            Projects: portfolio.Projects,
            HasAvailability: profile?.Availability is not null,
            Payments: payments);

        // The door of every open opportunity, read against this profile the way
        // the browse page reads it. Category is left out on purpose — it
        // never gates entry, so it cannot be a gap worth a line.
        var open = db.UseDapper ? await OpenSqlAsync(db.Sql, ct) : await OpenLinqAsync(db, ct);
        IEnumerable<string> skillNames = profile is null ? [] : profile.Skills.Select(s => s.Name);
        var mine = skillNames.Select(OpportunityFit.Key).ToHashSet(StringComparer.Ordinal);

        return new Facts(
            Headline: profile?.Headline,
            HasAbout: portfolio.HasBio,
            PrimaryCategory: OpportunityCategories.Find(profile?.PrimaryCategory)?.Label,
            OtherCategories: Labels(profile?.SecondaryCategories ?? []),
            Skills: profile is null
                ? []
                : profile.Skills.Select(s => (s.Name, ProfileRules.LevelName(s.Level), s.Years)).ToList(),
            Projects: portfolio.Projects,
            ProjectsWithLinks: portfolio.ProjectsWithLinks,
            ProjectKinds: profile is null ? [] : Labels(profile.Projects.Select(x => x.Category)),
            HasAvailability: strength.HasAvailability,
            HoursPerWeek: profile?.HoursPerWeek,
            YearsExperience: profile?.YearsExperience,
            HasPayout: payments > 0,
            StrengthPercent: Strength.Percent(strength),
            StepsNotDone: Strength.Steps(strength).Where(s => !s.Done).Select(s => s.Label).ToList(),
            OpenOpportunities: open.Count,
            OpenOpportunitiesEnterable: open.Count(d => d.MinMerit <= score && d.Skills.All(x => mine.Contains(x.Key))),
            Candidates: Candidates(portfolio, record, mine, open));
    }

    internal static Task<AccountRow?> AccountLinqAsync(AppDbContext db, Guid userId, CancellationToken ct) =>
        db.Users.AsNoTracking()
            .Where(u => u.Id == userId && u.ErasedAtUtc == null)
            .Select(u => new AccountRow(u.GithubLogin, u.EmailConfirmedAtUtc, u.PhoneConfirmedAtUtc))
            .SingleOrDefaultAsync(ct);

    internal static async Task<(ProfileRow? Profile, int Payments)> ProfileLinqAsync(AppDbContext db, Guid userId, CancellationToken ct)
    {
        var profile = await db.Profiles.AsNoTracking()
            .Where(p => p.UserId == userId)
            .Select(p => new ProfileRow
            {
                Headline = p.Headline, Bio = p.Bio, Location = p.Location, PrimaryCategory = p.PrimaryCategory,
                SecondaryCategories = p.SecondaryCategories,
                Availability = p.Availability, HoursPerWeek = p.HoursPerWeek, YearsExperience = p.YearsExperience,
                Skills = p.Skills.OrderBy(s => s.Order)
                    .Select(s => new SkillRow(s.Name, s.Level, s.Years)).ToList(),
                Projects = p.Projects.Select(x => new ProjectRow(x.Category, x.Url, x.RepoUrl)).ToList(),
            })
            .SingleOrDefaultAsync(ct);
        var payments = await db.ProfilePayments.CountAsync(x => x.UserId == userId, ct);
        return (profile, payments);
    }

    internal static async Task<List<Door>> OpenLinqAsync(AppDbContext db, CancellationToken ct) =>
        (await db.Opportunities.AsNoTracking()
            .Where(c => c.Status == OpportunityStatus.Open)
            .Select(c => new
            {
                c.MinMeritScore,
                Skills = c.Skills.Select(s => new { s.Name, s.Key }).ToList(),
            })
            .ToListAsync(ct))
        .Select(c => new Door(c.MinMeritScore, c.Skills.Select(s => (s.Name, s.Key)).ToList()))
        .ToList();

    /// <summary>
    /// The box as the page gets it: the live candidates with the live
    /// figures, and for each the model's sentence where one answers to its
    /// id, the portal's own words otherwise. A line the model wrote for a
    /// change that no longer opens anything is not shown — the words follow
    /// the arithmetic, never the other way round.
    /// </summary>
    public static ProfileReviewOutput Resolve(string? outputJson, Facts facts)
    {
        string? strongFor = null;
        var words = new Dictionary<string, string>(StringComparer.Ordinal);
        if (outputJson is not null)
        {
            using var doc = JsonDocument.Parse(outputJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("strongFor", out var s) && s.ValueKind == JsonValueKind.String)
                strongFor = s.GetString();
            if (root.TryGetProperty("improvements", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var item in arr.EnumerateArray())
                    if (item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                        && item.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                        words[id.GetString() ?? ""] = text.GetString() ?? "";
        }
        return new ProfileReviewOutput
        {
            StrongFor = strongFor,
            Improvements = facts.Candidates.Take(MaxLines).Select(c => new ReviewImprovement
            {
                Id = c.Id,
                Gain = c.Gain,
                Text = words.TryGetValue(c.Id, out var w) && w.Length > 0 ? w : c.Change + " — " + c.Because + ".",
            }),
        };
    }

    /// <summary>Keys to the names a sentence would use; a key the taxonomy no longer knows says nothing.</summary>
    private static List<string> Labels(IEnumerable<string?> keys) =>
        keys.Select(k => OpportunityCategories.Find(k)?.Label)
            .Where(l => l is not null)
            .Select(l => l!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static string Count(int n, string noun, string? plural = null) => $"{n} {(n == 1 ? noun : plural ?? noun + "s")}";

    private static string Join(IReadOnlyList<string> names) => names.Count switch
    {
        1 => names[0],
        2 => $"{names[0]} and {names[1]}",
        _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
    };
}
