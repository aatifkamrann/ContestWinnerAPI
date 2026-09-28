using WinnersPortal.Services.Auth;
using WinnersPortal.Services.Profiles;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WinnersPortal.Services.Ai;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Domain;
using WinnersPortal.Services.Settings;

namespace WinnersPortal.Services.Opportunities;

/// <summary>
/// One freelancer’s side of the fit, read once per request: their merit
/// score, their skills in comparison form, the kinds of work those skills
/// cover, and what the assessment reads beyond the door. The feed judges
/// twenty opportunities against it without going back to the database; the
/// entry door judges one.
/// </summary>
/// <param name="WorkKinds">
/// The model’s reading of this person, or null when there is none — see
/// <see cref="OpportunityFit.Judge"/> for what null means to the judgement.
/// </param>
public sealed record Viewer(
    int MeritScore,
    IReadOnlySet<string> SkillKeys,
    IReadOnlySet<string>? WorkKinds,
    ViewerFacts Facts)
{
    public Fit Judge(
        int minMerit,
        IReadOnlyList<string> requiredSkills,
        string? category,
        DateTimeOffset? startsAtUtc = null,
        DateTimeOffset? deadlineUtc = null) =>
        OpportunityFit.Judge(minMerit, requiredSkills, category, MeritScore, SkillKeys, WorkKinds, Facts, startsAtUtc, deadlineUtc);
}

/// <summary>
/// The reads behind a <see cref="Viewer"/>: the LINQ here for Postgres,
/// the T-SQL in <c>FitReader.SqlServer.cs</c> for SQL Server. The model's
/// reading of the member stays an EF Core read, because asking for a
/// fresh one is a write.
/// </summary>
public static partial class FitReader
{
    internal sealed record SkillRow(string Name, SkillLevel Level, int Years);

    internal sealed record ProjectRow(string? Category, string Title, string? Description, string? Outcome, string? Role, string? Tech);

    internal sealed record ProfileRow
    {
        public required string? Headline { get; init; }
        public required Availability? Availability { get; init; }
        public required int? HoursPerWeek { get; init; }
        /// <summary>In order.</summary>
        public required List<SkillRow> Skills { get; init; }
        /// <summary>In order.</summary>
        public required List<ProjectRow> Projects { get; init; }
    }

    /// <summary>One dated milestone on a live entry: when it was due, and when it was claimed if it was.</summary>
    internal sealed record DatedRow(DateTimeOffset Due, DateTimeOffset? ClaimedAt);

    internal sealed record EntryRow
    {
        public required OpportunityStatus OpportunityStatus { get; init; }
        public required List<DatedRow> Dated { get; init; }
    }

    /// <summary>Null for anybody who is not a freelancer — there is no fit to read for a client.</summary>
    public static async Task<Viewer?> ForAsync(
        AppDbContext db, Guid? userId, string? role, AiOptions ai, AiWorkSignal signal, CancellationToken ct)
    {
        if (userId is null || role != Roles.Freelancer) return null;
        var merit = await MeritReader.MeritForAsync(db, [userId.Value], ct);
        // Through the profile, not the skills table: a closed profile is
        // filtered out of db.Profiles (!IsDeleted) and its child rows are
        // not, so this is the only read that cannot match a deleted
        // member’s skills.
        var profile = db.UseDapper
            ? await ProfileSqlAsync(db.Sql, userId.Value, ct)
            : await ProfileLinqAsync(db, userId.Value, ct);
        var skills = profile?.Skills.Select(s => s.Name).ToList() ?? [];

        // Their record here, as the assessment reads it: every dated
        // milestone on their live entries that has come due or been
        // claimed, and how many of those were claimed on time; and the open
        // opportunities they are already building for, which compete for the
        // same weeks as the one being judged.
        var now = DateTimeOffset.UtcNow;
        var entries = db.UseDapper
            ? await EntriesSqlAsync(db.Sql, userId.Value, ct)
            : await EntriesLinqAsync(db, userId.Value, ct);
        var dated = entries.SelectMany(e => e.Dated).Where(m => m.Due <= now || m.ClaimedAt != null).ToList();
        var facts = new ViewerFacts(
            Skills: (profile?.Skills ?? [])
                .GroupBy(s => OpportunityFit.Key(s.Name), StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => (g.First().Level, g.First().Years), StringComparer.Ordinal),
            Projects: (profile?.Projects ?? [])
                .Select(x => ProjectFacts.Of(x.Category, x.Title, x.Description, x.Outcome, x.Role, x.Tech))
                .ToList(),
            Availability: profile?.Availability,
            HoursPerWeek: profile?.HoursPerWeek,
            MilestonesDue: dated.Count,
            MilestonesOnTime: dated.Count(m => m.ClaimedAt != null && m.ClaimedAt <= m.Due),
            OpportunitiesInHand: entries.Count(e => e.OpportunityStatus == OpportunityStatus.Open));

        return new Viewer(
            merit.TryGetValue(userId.Value, out var m) ? m.Score : 0,
            skills.Select(OpportunityFit.Key).ToHashSet(StringComparer.Ordinal),
            profile is null
                ? null
                : await WorkKindsAsync(
                    db, ai, signal, userId.Value,
                    () => AiInputs.WorkKinds(
                        profile.Headline,
                        profile.Skills.Select(s => (s.Name, s.Level.ToString(), s.Years))),
                    ct),
            facts);
    }

    internal static Task<ProfileRow?> ProfileLinqAsync(AppDbContext db, Guid userId, CancellationToken ct) =>
        db.Profiles.AsNoTracking()
            .Where(p => p.UserId == userId)
            .Select(p => new ProfileRow
            {
                Headline = p.Headline,
                Availability = p.Availability,
                HoursPerWeek = p.HoursPerWeek,
                Skills = p.Skills.OrderBy(s => s.Order)
                    .Select(s => new SkillRow(s.Name, s.Level, s.Years)).ToList(),
                Projects = p.Projects.OrderBy(x => x.Order)
                    .Select(x => new ProjectRow(x.Category, x.Title, x.Description, x.Outcome, x.Role, x.Tech)).ToList(),
            })
            .SingleOrDefaultAsync(ct);

    internal static Task<List<EntryRow>> EntriesLinqAsync(AppDbContext db, Guid userId, CancellationToken ct) =>
        db.Entries.AsNoTracking()
            .Where(e => e.FreelancerId == userId && e.Status == EntryStatus.Active)
            .Select(e => new EntryRow
            {
                OpportunityStatus = e.Opportunity!.Status,
                Dated = e.Opportunity.Milestones
                    .Where(m => m.DueUtc != null)
                    .Select(m => new DatedRow(
                        m.DueUtc!.Value,
                        e.Checkpoints
                            .Where(cp => cp.MilestoneId == m.Id)
                            .Select(cp => (DateTimeOffset?)cp.ClaimedAtUtc)
                            .FirstOrDefault()))
                    .ToList(),
            })
            .ToListAsync(ct);

    /// <summary>
    /// The stored reading of this member, if one answers the profile they
    /// have now. Null when the feature is off or no key is saved — nothing
    /// local stands in for it, which is the whole point: a reading nobody
    /// made is not a reading, and the browse page simply stops colouring
    /// opportunities as outside anybody’s usual work.
    ///
    /// A profile that has changed since its reading gets a fresh one queued
    /// and keeps the old one meanwhile. Queueing on a read looks odd, but it
    /// is the only trigger that cannot be missed: a member who edits nothing
    /// ever still gets their first reading the first time they ask for one.
    /// </summary>
    private static async Task<IReadOnlySet<string>?> WorkKindsAsync(
        AppDbContext db, AiOptions ai, AiWorkSignal signal, Guid userId, Func<string> profileJson, CancellationToken ct)
    {
        if (!await ai.IsFeatureEnabledAsync(AiFeature.RecommendedMatching, ct)) return null;

        var hash = AiRules.InputHash(AiInputs.Taxonomy() + profileJson());
        var artifact = await db.AiArtifacts
            .SingleOrDefaultAsync(a => a.Feature == AiFeature.RecommendedMatching && a.SubjectId == userId, ct);

        if (artifact?.InputHash != hash && artifact?.Status != AiArtifactStatus.Pending)
        {
            await AiWorker.QueueAsync(db, AiFeature.RecommendedMatching, userId, DateTimeOffset.UtcNow, ct);
            await db.SaveChangesAsync(ct);
            signal.Wake();
        }

        return artifact?.OutputJson is null ? null : Keys(artifact.OutputJson);
    }

    /// <summary>The validated reading back out of its stored JSON.</summary>
    private static IReadOnlySet<string> Keys(string outputJson)
    {
        using var doc = JsonDocument.Parse(outputJson);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        if (doc.RootElement.TryGetProperty("categories", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var v in arr.EnumerateArray())
                if (v.ValueKind == JsonValueKind.String && v.GetString() is { } key)
                    keys.Add(key);
        return keys;
    }

    /// <summary>The wire shape, identical on the card and the page.</summary>
    public static FitView Dto(Fit fit) => new FitView
    {
        Verdict = fit.Verdict,
        CanEnter = fit.CanEnter,
        Recommended = fit.Recommended,
        MeritOk = fit.MeritOk,
        SkillsOk = fit.SkillsOk,
        CategoryOk = fit.CategoryOk,
        MeritScore = fit.MeritScore,
        MinMerit = fit.MinMerit,
        MissingSkills = fit.MissingSkills,
        Reasons = fit.Reasons,
        Match = fit.Match,
        MatchBand = fit.MatchBand,
        Factors = fit.Factors
            .Select(f => new FitFactor { Key = f.Key, Label = f.Label, Score = f.Score, Detail = f.Detail }),
        Risk = fit.Risk,
    };
}
