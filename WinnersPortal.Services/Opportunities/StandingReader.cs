using Microsoft.EntityFrameworkCore;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Domain;
using WinnersPortal.Services.Profiles;

namespace WinnersPortal.Services.Opportunities;

/// <summary>
/// Every entrant's standing for a set of opportunities, in six batched reads
/// whatever the entrant count — the opportunity page needs one opportunity, the
/// dashboards several, and neither may pay a query per row. The arithmetic
/// itself lives in <see cref="Standing"/>; this only gathers the facts and
/// ranks the rows. The facts come from the LINQ here on Postgres and from
/// the T-SQL in <c>StandingReader.SqlServer.cs</c> on SQL Server, both
/// filling the same rows.
/// </summary>
public static partial class StandingReader
{
    /// <summary>One entrant's standing on one opportunity, ranked among that opportunity's rows.</summary>
    public sealed record Row(
        Guid EntryId,
        Guid OpportunityId,
        Guid FreelancerId,
        string DisplayName,
        string? Note,
        DateTimeOffset EnteredAtUtc,
        DateTimeOffset? LastActivityUtc,
        IReadOnlyList<MilestoneState> States,
        int Score,
        Standing.Band Band,
        int Rank,
        int Of,
        IReadOnlyList<Standing.Part> Parts,
        Standing.Facts Facts,
        string? NextStep);

    // ---- the facts, whichever database read them

    internal sealed record MilestoneRow(int Order, string Title, string? Description, DateTimeOffset? DueUtc);

    internal sealed record OpportunityRow
    {
        public required Guid Id { get; init; }
        public required string Title { get; init; }
        public required string BriefMarkdown { get; init; }
        public required DateTimeOffset? DeadlineUtc { get; init; }
        public required OpportunityDelivery Delivery { get; init; }
        public required bool RequiresCompose { get; init; }
        /// <summary>In order.</summary>
        public required List<MilestoneRow> Milestones { get; init; }
    }

    /// <summary>A claim and, where the opportunity requires Docker Compose, where its build stands.</summary>
    internal sealed record ClaimRow(
        int Order, DateTimeOffset ClaimedAtUtc, Guid CheckpointId, PreviewBuildStatus BuildStatus, DateTimeOffset? BuildFinishedAtUtc);

    internal sealed record FileRow(string FileName, string ContentType);

    internal sealed record EntryRow
    {
        public required Guid Id { get; init; }
        public required Guid OpportunityId { get; init; }
        public required Guid FreelancerId { get; init; }
        public required DateTimeOffset CreatedAtUtc { get; init; }
        public required DateTimeOffset? LastPushAtUtc { get; init; }
        public required string? Note { get; init; }
        public required string Name { get; init; }
        public required int? TreeFileCount { get; init; }
        public required bool? TreeHasTests { get; init; }
        public required bool? TreeHasReadme { get; init; }
        public required bool? TreeHasCi { get; init; }
        public required bool? TreeHasCompose { get; init; }
        public required List<ClaimRow> Claimed { get; init; }
        /// <summary>Uploads that finished.</summary>
        public required List<FileRow> Files { get; init; }
    }

    internal sealed record ProfileRow
    {
        public required Guid UserId { get; init; }
        public required string? Headline { get; init; }
        public required string? Bio { get; init; }
        public required string? Location { get; init; }
        public required int? HoursPerWeek { get; init; }
        public required int? YearsExperience { get; init; }
        /// <summary>Skill names, in order.</summary>
        public required List<string> Skills { get; init; }
        public required int Projects { get; init; }
        public required int Linked { get; init; }
    }

    internal sealed record HistoryRow(Guid FreelancerId, bool Won);

    internal sealed record RatingRow(Guid UserId, int Count, int Sum);

    /// <summary>The six batched reads.</summary>
    internal sealed record Reads(
        List<OpportunityRow> Opportunities,
        List<EntryRow> Entries,
        Dictionary<Guid, ProfileRow> Profiles,
        HashSet<Guid> GithubConnected,
        List<HistoryRow> History,
        Dictionary<Guid, RatingRow> Ratings);

    /// <param name="buildsOn">
    /// Whether Docker Compose builds are on (BuildHostService): while they
    /// are off the board shows no builds, so no standing carries a Builds
    /// line either — the part, and its points, come back with the host.
    /// </param>
    public static async Task<Dictionary<Guid, Row>> ForOpportunitiesAsync(
        AppDbContext db, IReadOnlyCollection<Guid> opportunityIds, DateTimeOffset now, bool buildsOn, CancellationToken ct)
    {
        var result = new Dictionary<Guid, Row>();
        if (opportunityIds.Count == 0) return result;

        var reads = db.UseDapper
            ? await ReadSqlAsync(db.Sql, opportunityIds, ct)
            : await ReadLinqAsync(db, opportunityIds, ct);
        if (reads.Entries.Count == 0) return result;

        foreach (var c in reads.Opportunities)
        {
            // The frozen reading: once the deadline has passed, a row is
            // read as it stood then, not as it drifts further from "now".
            var frozen = c.DeadlineUtc is not null && c.DeadlineUtc < now;
            var asOf = frozen ? c.DeadlineUtc!.Value : now;
            var briefText = c.Title + "\n" + c.BriefMarkdown + "\n"
                + string.Join("\n", c.Milestones.Select(m => m.Title + " " + m.Description));
            var usesRepo = Delivery.UsesRepository(c.Delivery);
            var usesUpload = Delivery.UsesUpload(c.Delivery);

            var rows = new List<(Row Row, int BoardScore)>();
            foreach (var e in reads.Entries.Where(e => e.OpportunityId == c.Id))
            {
                var states = c.Milestones
                    .Select(m => Schedule.StateOf(
                        m.DueUtc,
                        e.Claimed.FirstOrDefault(cp => cp.Order == m.Order)?.ClaimedAtUtc,
                        asOf))
                    .ToList();
                var board = Schedule.Stand(states);

                reads.Profiles.TryGetValue(e.FreelancerId, out var p);
                reads.Ratings.TryGetValue(e.FreelancerId, out var r);
                var mine = reads.History.Where(h => h.FreelancerId == e.FreelancerId).ToList();

                var portfolio = new Merit.Portfolio(
                    HasHeadline: !string.IsNullOrWhiteSpace(p?.Headline),
                    HasBio: !string.IsNullOrWhiteSpace(p?.Bio),
                    Skills: p?.Skills.Count ?? 0,
                    Projects: p?.Projects ?? 0,
                    ProjectsWithLinks: p?.Linked ?? 0,
                    HasDetails: p is not null
                        && !string.IsNullOrWhiteSpace(p.Location)
                        && p.HoursPerWeek is > 0
                        && p.YearsExperience is not null,
                    // The board wants a score, and the score does not move
                    // with this: a portal without GitHub sign-in denies the
                    // same two points to every row. Only the profile page,
                    // which tells one person what they can still reach, asks
                    // the settings and drops them from the denominator too.
                    GithubConnected: reads.GithubConnected.Contains(e.FreelancerId),
                    GithubOffered: true);

                var facts = new Standing.Facts(
                    Board: board,
                    Milestones: c.Milestones.Count,
                    DatedMilestones: c.Milestones.Count(m => m.DueUtc != null),
                    EnteredAtUtc: e.CreatedAtUtc,
                    LastActivityUtc: e.LastPushAtUtc,
                    AsOfUtc: asOf,
                    Frozen: frozen,
                    UsesRepository: usesRepo,
                    Repo: e.TreeFileCount is null
                        ? null
                        : new Standing.RepoFacts(
                            e.TreeFileCount.Value, e.TreeHasTests == true, e.TreeHasReadme == true, e.TreeHasCi == true,
                            e.TreeHasCompose == true),
                    UsesUpload: usesUpload,
                    FilesUploaded: e.Files.Count,
                    DocumentsUploaded: e.Files.Count(f => Standing.IsDocument(f.ContentType, f.FileName)),
                    OpportunitiesDecided: mine.Count,
                    OpportunitiesWon: mine.Count(h => h.Won),
                    RatingCount: r?.Count ?? 0,
                    RatingSum: r?.Sum ?? 0,
                    PortfolioScore: Merit.PortfolioScore(portfolio),
                    SkillsListed: p?.Skills.Count ?? 0,
                    SkillsMatched: Standing.MatchSkills(p?.Skills ?? [], briefText),
                    RequiresCompose: c.RequiresCompose && buildsOn,
                    BuildsFinished: e.Claimed.Count(cp => cp.BuildStatus is PreviewBuildStatus.Built or PreviewBuildStatus.Failed),
                    BuildsOk: e.Claimed.Count(cp => cp.BuildStatus == PreviewBuildStatus.Built));

                var parts = Standing.Parts(facts);
                rows.Add((new Row(
                    e.Id, c.Id, e.FreelancerId, e.Name, e.Note, e.CreatedAtUtc, e.LastPushAtUtc, states,
                    Standing.Score(parts), Standing.BandOf(facts, parts), 0, 0, parts, facts,
                    Standing.NextStep(facts, parts)), Schedule.Score(board)));
            }

            // Best standing first; the board's own on-time count breaks a
            // tie, and join order breaks whatever is left — so an opportunity
            // where nothing is readable yet reads in join order, as before.
            var ranked = rows
                .OrderByDescending(x => x.Row.Score)
                .ThenByDescending(x => x.BoardScore)
                .ThenBy(x => x.Row.EnteredAtUtc)
                .Select((x, i) => x.Row with { Rank = i + 1, Of = rows.Count })
                .ToList();
            foreach (var row in ranked) result[row.EntryId] = row;
        }
        return result;
    }

    internal static async Task<Reads> ReadLinqAsync(AppDbContext db, IReadOnlyCollection<Guid> opportunityIds, CancellationToken ct)
    {
        var opportunities = await db.Opportunities.AsNoTracking()
            .Where(c => opportunityIds.Contains(c.Id))
            .Select(c => new OpportunityRow
            {
                Id = c.Id, Title = c.Title, BriefMarkdown = c.BriefMarkdown, DeadlineUtc = c.DeadlineUtc, Delivery = c.Delivery,
                RequiresCompose = c.RequiresCompose,
                Milestones = c.Milestones.OrderBy(m => m.Order)
                    .Select(m => new MilestoneRow(m.Order, m.Title, m.Description, m.DueUtc)).ToList(),
            })
            .ToListAsync(ct);

        var entries = await db.Entries.AsNoTracking()
            .Where(e => opportunityIds.Contains(e.OpportunityId) && e.Status == EntryStatus.Active)
            .Select(e => new EntryRow
            {
                Id = e.Id, OpportunityId = e.OpportunityId, FreelancerId = e.FreelancerId, CreatedAtUtc = e.CreatedAtUtc,
                LastPushAtUtc = e.LastPushAtUtc, Note = e.Note,
                Name = e.Freelancer!.DisplayName,
                TreeFileCount = e.TreeFileCount, TreeHasTests = e.TreeHasTests, TreeHasReadme = e.TreeHasReadme, TreeHasCi = e.TreeHasCi, TreeHasCompose = e.TreeHasCompose,
                Claimed = e.Checkpoints
                    .Select(cp => new ClaimRow(cp.Milestone!.Order, cp.ClaimedAtUtc, cp.Id, cp.BuildStatus, cp.BuildFinishedAtUtc))
                    .ToList(),
                Files = e.Submissions.Where(s => s.UploadedAtUtc != null)
                    .Select(s => new FileRow(s.FileName, s.ContentType)).ToList(),
            })
            .ToListAsync(ct);
        if (entries.Count == 0) return new Reads(opportunities, entries, new(), [], [], new());

        var userIds = entries.Select(e => e.FreelancerId).Distinct().ToList();

        var profiles = await db.Profiles.AsNoTracking()
            .Where(p => userIds.Contains(p.UserId))
            .Select(p => new ProfileRow
            {
                UserId = p.UserId, Headline = p.Headline, Bio = p.Bio, Location = p.Location,
                HoursPerWeek = p.HoursPerWeek, YearsExperience = p.YearsExperience,
                Skills = p.Skills.OrderBy(s => s.Order).Select(s => s.Name).ToList(),
                Projects = p.Projects.Count,
                Linked = p.Projects.Count(x => x.Url != null || x.RepoUrl != null),
            })
            .ToDictionaryAsync(p => p.UserId, ct);

        var githubConnected = (await db.Users.AsNoTracking()
                .Where(u => userIds.Contains(u.Id) && u.GithubLogin != null)
                .Select(u => u.Id)
                .ToListAsync(ct))
            .ToHashSet();

        // Opportunities elsewhere that reached a decision with this person still
        // in — a withdrawal or a removal is not an opportunity finished, and the
        // opportunities being read here are not decided yet (or are the one
        // being read, which must not count itself).
        var history = await db.Entries.AsNoTracking()
            .Where(e => userIds.Contains(e.FreelancerId)
                && e.Status == EntryStatus.Active
                && !opportunityIds.Contains(e.OpportunityId)
                && e.Opportunity!.Status == OpportunityStatus.Awarded)
            .Select(e => new HistoryRow(e.FreelancerId, db.Awards.Any(a => a.EntryId == e.Id)))
            .ToListAsync(ct);

        var ratings = await db.Ratings.AsNoTracking()
            .Where(r => userIds.Contains(r.OfUserId))
            .GroupBy(r => r.OfUserId)
            .Select(g => new RatingRow(g.Key, g.Count(), g.Sum(r => r.Stars)))
            .ToDictionaryAsync(r => r.UserId, ct);

        return new Reads(opportunities, entries, profiles, githubConnected, history, ratings);
    }
}
