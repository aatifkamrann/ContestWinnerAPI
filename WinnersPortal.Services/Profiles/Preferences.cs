namespace WinnersPortal.Services.Profiles;

/// <summary>
/// One answer on a closed list a member picks from: the key that is stored,
/// the words the form shows, and the line under them.
/// </summary>
public sealed record Preference(string Key, string Label, string Hint);

/// <summary>
/// How long a piece of work runs, in the three bands a member can say they
/// prefer. Bands rather than numbers because the question is a preference
/// and not a schedule: "up to a month" is what somebody means, and asking
/// for a figure would get a guess.
///
/// A preference and nothing more — no opportunity checks it, and a member who
/// prefers long-term work may enter a two-week one.
/// </summary>
public static class ProjectDurations
{
    public static readonly IReadOnlyList<Preference> All =
    [
        new("short", "Short-term", "Days to a few weeks — an opportunity, a fix, a feature."),
        new("medium", "Medium-term", "One to three months — a build with milestones."),
        new("long", "Long-term", "Three months and up — a product, or a retainer."),
    ];

    public static Preference? Find(string? key) =>
        All.FirstOrDefault(p => p.Key == key?.Trim().ToLowerInvariant());
}

/// <summary>
/// The terms a member prefers to work on. The portal runs the first of
/// these today — every opportunity here is a competitive fixed award — and asks
/// about the other two so it knows who to put in front of a client once a
/// direct or an hourly engagement is something it can offer. Recorded, and
/// read by nothing that decides anything.
/// </summary>
public static class EngagementTypes
{
    public static readonly IReadOnlyList<Preference> All =
    [
        new("competitive", "Competitive Fixed Price", "An opportunity: several entrants, one fixed award, the client picks."),
        new("direct", "Direct Fixed Price", "One client, one freelancer, a fixed price agreed up front."),
        new("hourly", "Hourly Engagement", "Paid by the hour, for as long as the work runs."),
    ];

    public static Preference? Find(string? key) =>
        All.FirstOrDefault(p => p.Key == key?.Trim().ToLowerInvariant());
}
