using WinnersPortal.Services.Opportunities;

namespace WinnersPortal.Services.Dashboard;

/// <summary>A dashboard; which one depends on the viewer's role.</summary>
public abstract record DashboardResponse;

/// <summary>The client's dashboard.</summary>
public sealed record ClientDashboard : DashboardResponse
{
    public required string Role { get; init; }
    public required DateTimeOffset GeneratedAtUtc { get; init; }
    public required ClientHeadline Headline { get; init; }
    public required IEnumerable<WaitingReview> Waiting { get; init; }
    public required MoneyTotals[] Money { get; init; }
    public required DailyChart Activity { get; init; }
    public required SplitChart StatusSplit { get; init; }
    public required IEnumerable<ClientOpportunityProgress> Progress { get; init; }
    public required List<AttentionItem> Attention { get; init; }
    public required IEnumerable<ClientDeadline> Deadlines { get; init; }
    public required IEnumerable<RecentEvent> Recent { get; init; }
}

/// <summary>The client's headline figures.</summary>
public sealed record ClientHeadline
{
    public required int LiveOpportunities { get; init; }
    public required int InReview { get; init; }
    public required int Awarded { get; init; }
    public required int Drafts { get; init; }
    public required int Entrants { get; init; }
    public required double AvgEntrants { get; init; }
    public required int MilestonesClaimed { get; init; }
    public required int MilestonesPossible { get; init; }
    public required int RepoIssues { get; init; }
    public required int ApplicationsWaiting { get; init; }
}

/// <summary>An opportunity with applications waiting on a decision.</summary>
public sealed record WaitingReview
{
    public required string Slug { get; init; }
    public required string Title { get; init; }
    public required int Count { get; init; }
    public required DateTimeOffset OldestUtc { get; init; }
}

/// <summary>Award money in one currency: announced, paid and outstanding.</summary>
public sealed record MoneyTotals
{
    public required string Currency { get; init; }
    public required decimal Announced { get; init; }
    public required decimal Paid { get; init; }
    public required decimal Outstanding { get; init; }
    public required int Count { get; init; }
}

/// <summary>A chart of daily counts over the dashboard's window.</summary>
public sealed record DailyChart
{
    public required string[] Labels { get; init; }
    public required ChartSeries[] Series { get; init; }
}

/// <summary>One named line of a chart.</summary>
public sealed record ChartSeries
{
    public required string Name { get; init; }
    public required int[] Points { get; init; }
}

/// <summary>A breakdown of a whole into labelled parts.</summary>
public sealed record SplitChart
{
    public required string[] Labels { get; init; }
    public required int[] Values { get; init; }
}

/// <summary>How one of the client's live opportunities is progressing.</summary>
public sealed record ClientOpportunityProgress
{
    public required string Slug { get; init; }
    public required string Title { get; init; }
    public required string Status { get; init; }
    public required DateTimeOffset? DeadlineUtc { get; init; }
    public required int Entrants { get; init; }
    public required int Claimed { get; init; }
    public required int Possible { get; init; }
    public required ProgressLeader? Leader { get; init; }
    public required int AtRisk { get; init; }
}

/// <summary>The entrant leading an opportunity.</summary>
public sealed record ProgressLeader
{
    public required string Name { get; init; }
    public required int Score { get; init; }
}

/// <summary>Something that needs the viewer, how urgently, and where to act on it.</summary>
public sealed record AttentionItem
{
    public required string Kind { get; init; }
    public required string Severity { get; init; }
    public required string Title { get; init; }
    public required string Detail { get; init; }
    public required string? Href { get; init; }
}

/// <summary>A deadline coming up on one of the client's opportunities.</summary>
public sealed record ClientDeadline
{
    public required string Slug { get; init; }
    public required string Title { get; init; }
    public required DateTimeOffset? DeadlineUtc { get; init; }
    public required int Entrants { get; init; }
}

/// <summary>Something that happened lately, with where to read about it.</summary>
public sealed record RecentEvent
{
    public required DateTimeOffset AtUtc { get; init; }
    public required string Text { get; init; }
    public required string Detail { get; init; }
    public required string Href { get; init; }
}

/// <summary>The freelancer's dashboard.</summary>
public sealed record FreelancerDashboard : DashboardResponse
{
    public required string Role { get; init; }
    public required DateTimeOffset GeneratedAtUtc { get; init; }
    public required FreelancerHeadline Headline { get; init; }
    public required MoneyTotals[] Money { get; init; }
    public required DailyChart Activity { get; init; }
    public required SplitChart OutcomeSplit { get; init; }
    public required IEnumerable<FreelancerEntryProgress> Progress { get; init; }
    public required List<AttentionItem> Attention { get; init; }
    public required IEnumerable<FreelancerDeadline> Deadlines { get; init; }
    public required IEnumerable<RecentEvent> Recent { get; init; }
}

/// <summary>The freelancer's headline figures.</summary>
public sealed record FreelancerHeadline
{
    public required int ActiveEntries { get; init; }
    public required int TotalEntries { get; init; }
    public required int MilestonesClaimed { get; init; }
    public required int MilestonesPossible { get; init; }
    public required int Wins { get; init; }
    public required int Decided { get; init; }
    public required double WinRate { get; init; }
    public required int Pushes { get; init; }
    public required int RepoIssues { get; init; }
}

/// <summary>How one of the freelancer's entries is progressing.</summary>
public sealed record FreelancerEntryProgress
{
    public required string Slug { get; init; }
    public required string Title { get; init; }
    public required string Status { get; init; }
    public required DateTimeOffset? DeadlineUtc { get; init; }
    public required int Claimed { get; init; }
    public required int Possible { get; init; }
    public required string? RepoFullName { get; init; }
    public required string Delivery { get; init; }
    public required DateTimeOffset? LastPushAtUtc { get; init; }
    public required decimal AwardAmount { get; init; }
    public required string Currency { get; init; }
    public required StandingSummary? Standing { get; init; }
}

/// <summary>A deadline coming up on one of the freelancer's entries.</summary>
public sealed record FreelancerDeadline
{
    public required string Slug { get; init; }
    public required string Title { get; init; }
    public required DateTimeOffset? DeadlineUtc { get; init; }
    public required int Claimed { get; init; }
    public required int Possible { get; init; }
}

/// <summary>The administrator's dashboard.</summary>
public sealed record AdminDashboard : DashboardResponse
{
    public required string Role { get; init; }
    public required DateTimeOffset GeneratedAtUtc { get; init; }
    public required AdminHeadline Headline { get; init; }
    public required IEnumerable<WaitingReview> Waiting { get; init; }
    public required MoneyTotals[] Money { get; init; }
    public required DailyChart Activity { get; init; }
    public required DailyChart Signups { get; init; }
    public required SplitChart StatusSplit { get; init; }
    public required SplitChart ProvisionSplit { get; init; }
    public required IEnumerable<TopClient> TopClients { get; init; }
    public required IEnumerable<TopFreelancer> TopFreelancers { get; init; }
    public required List<AttentionItem> Attention { get; init; }
    public required IEnumerable<RecentEvent> Recent { get; init; }
}

/// <summary>The portal's headline figures.</summary>
public sealed record AdminHeadline
{
    public required int Users { get; init; }
    public required int Clients { get; init; }
    public required int Freelancers { get; init; }
    public required int NewUsers { get; init; }
    public required int Opportunities { get; init; }
    public required int OpenOpportunities { get; init; }
    public required int InReview { get; init; }
    public required int Entries { get; init; }
    public required int Repos { get; init; }
    public required int ReposFailed { get; init; }
    public required int Webhooks24h { get; init; }
    public required int UnmatchedWebhooks { get; init; }
    public required int StuckHandovers { get; init; }
    public required int ApplicationsWaiting { get; init; }
}

/// <summary>A client among the most active.</summary>
public sealed record TopClient
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required int Opportunities { get; init; }
    public required int Entrants { get; init; }
    public required int AwardsAnnounced { get; init; }
    public required int AwardsPaid { get; init; }
    public required decimal PaidValue { get; init; }
    public required string Currency { get; init; }
    public required DateTimeOffset LastPostedUtc { get; init; }
}

/// <summary>A freelancer among the most active.</summary>
public sealed record TopFreelancer
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required int Entries { get; init; }
    public required int Claims { get; init; }
    public required int Pushes { get; init; }
    public required int Wins { get; init; }
    public required decimal Earned { get; init; }
    public required string Currency { get; init; }
}
