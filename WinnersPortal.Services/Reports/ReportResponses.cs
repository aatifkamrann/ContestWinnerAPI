namespace WinnersPortal.Services.Reports;

/// <summary>The applications report.</summary>
public sealed record ApplicationsReportResponse
{
    public required DateTimeOffset GeneratedAtUtc { get; init; }
    public required string Scope { get; init; }
    public required IEnumerable<ApplicationReportRow> Rows { get; init; }
}

/// <summary>One application in the report.</summary>
public sealed record ApplicationReportRow
{
    public required Guid Id { get; init; }
    public required string Status { get; init; }
    public required string? Result { get; init; }
    public required Guid FreelancerId { get; init; }
    public required string FreelancerName { get; init; }
    public required string? FreelancerEmail { get; init; }
    public required Guid OpportunityId { get; init; }
    public required string OpportunitySlug { get; init; }
    public required string OpportunityTitle { get; init; }
    public required string OpportunityStatus { get; init; }
    public required Guid ClientId { get; init; }
    public required string ClientName { get; init; }
    public required string? Category { get; init; }
    public required string? CategoryLabel { get; init; }
    public required string? Subcategory { get; init; }
    public required string? SubcategoryLabel { get; init; }
    public required decimal AwardAmount { get; init; }
    public required string Currency { get; init; }
    public required int MinMeritScore { get; init; }
    public required DateTimeOffset? DeadlineUtc { get; init; }
    public required DateTimeOffset? EntryCloseUtc { get; init; }
    public required DateTimeOffset SubmittedAtUtc { get; init; }
    public required DateTimeOffset? AnsweredAtUtc { get; init; }
    public required DateTimeOffset? ChangedAtUtc { get; init; }
    public required double? DaysToAnswer { get; init; }
    public required int Match { get; init; }
    public required int MeritScore { get; init; }
    public required string? Band { get; init; }
    public required string Commitment { get; init; }
    public required int HoursPerWeek { get; init; }
    public required int PortfolioCount { get; init; }
    public required IEnumerable<string?> Advantages { get; init; }
    public required string? GithubUsername { get; init; }
    public required string? EntryStatus { get; init; }
    public required int? MilestonesClaimed { get; init; }
    public required int? PushCount { get; init; }
    public required DateTimeOffset? LastPushAtUtc { get; init; }
    public required string? Reason { get; init; }
    public required DateTimeOffset? PaidAtUtc { get; init; }
}

/// <summary>The opportunities report.</summary>
public sealed record OpportunitiesReportResponse
{
    public required DateTimeOffset GeneratedAtUtc { get; init; }
    public required string Scope { get; init; }
    public required IEnumerable<OpportunityReportRow> Rows { get; init; }
}

/// <summary>One opportunity in the report.</summary>
public sealed record OpportunityReportRow
{
    public required Guid Id { get; init; }
    public required string Slug { get; init; }
    public required string Title { get; init; }
    public required string Status { get; init; }
    public required Guid ClientId { get; init; }
    public required string ClientName { get; init; }
    public required string? ClientEmail { get; init; }
    public required string? Category { get; init; }
    public required string? CategoryLabel { get; init; }
    public required string? Subcategory { get; init; }
    public required string? SubcategoryLabel { get; init; }
    public required string Delivery { get; init; }
    public required decimal AwardAmount { get; init; }
    public required string Currency { get; init; }
    public required int MinMeritScore { get; init; }
    public required List<string> Skills { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required DateTimeOffset? PublishedAtUtc { get; init; }
    public required DateTimeOffset? StartsAtUtc { get; init; }
    public required DateTimeOffset? DeadlineUtc { get; init; }
    public required DateTimeOffset? EntryCloseUtc { get; init; }
    public required DateTimeOffset? CancelledAtUtc { get; init; }
    public required double? DurationDays { get; init; }
    public required int ApplicationCount { get; init; }
    public required int? ApplicationsWaiting { get; init; }
    public required int? ApplicationsSelected { get; init; }
    public required int EntrantCount { get; init; }
    public required int? EntriesTotal { get; init; }
    public required int? EntriesWithdrawn { get; init; }
    public required int? EntriesRemoved { get; init; }
    public required int MilestoneCount { get; init; }
    public required string? WinnerName { get; init; }
    public required DateTimeOffset? AwardAnnouncedAtUtc { get; init; }
    public required DateTimeOffset? AwardPaidAtUtc { get; init; }
    public required string? MyStatus { get; init; }
}

/// <summary>The entries report.</summary>
public sealed record EntriesReportResponse
{
    public required DateTimeOffset GeneratedAtUtc { get; init; }
    public required string Scope { get; init; }
    public required IEnumerable<EntryReportRow> Rows { get; init; }
}

/// <summary>One entry in the report.</summary>
public sealed record EntryReportRow
{
    public required Guid Id { get; init; }
    public required string Status { get; init; }
    public required string? Result { get; init; }
    public required Guid FreelancerId { get; init; }
    public required string FreelancerName { get; init; }
    public required string? FreelancerEmail { get; init; }
    public required Guid OpportunityId { get; init; }
    public required string OpportunitySlug { get; init; }
    public required string OpportunityTitle { get; init; }
    public required string OpportunityStatus { get; init; }
    public required Guid ClientId { get; init; }
    public required string ClientName { get; init; }
    public required string? Category { get; init; }
    public required string? CategoryLabel { get; init; }
    public required string? Subcategory { get; init; }
    public required string? SubcategoryLabel { get; init; }
    public required string Delivery { get; init; }
    public required decimal AwardAmount { get; init; }
    public required string Currency { get; init; }
    public required DateTimeOffset? DeadlineUtc { get; init; }
    public required DateTimeOffset EnteredAtUtc { get; init; }
    public required DateTimeOffset? EndedAtUtc { get; init; }
    public required double? DaysIn { get; init; }
    public required DateTimeOffset? AppliedAtUtc { get; init; }
    public required int? Match { get; init; }
    public required string? Note { get; init; }
    public required string? GithubUsername { get; init; }
    public required string? RepoFullName { get; init; }
    public required string? RepoSetup { get; init; }
    public required int MilestoneCount { get; init; }
    public required int MilestonesClaimed { get; init; }
    public required int OnTime { get; init; }
    public required int Late { get; init; }
    public required int Overdue { get; init; }
    public required DateTimeOffset? FirstClaimAtUtc { get; init; }
    public required List<DateTimeOffset> ClaimedAtUtc { get; init; }
    public required double? DaysToFirstClaim { get; init; }
    public required int PushCount { get; init; }
    public required DateTimeOffset? LastPushAtUtc { get; init; }
    public required int FilesUploaded { get; init; }
    public required int? RepoFileCount { get; init; }
    public required IReadOnlyList<string>? RepoHas { get; init; }
    public required int? StandingScore { get; init; }
    public required string? StandingBand { get; init; }
    public required int? StandingRank { get; init; }
    public required int? StandingOf { get; init; }
    public required string? Reason { get; init; }
    public required DateTimeOffset? AwardAnnouncedAtUtc { get; init; }
    public required DateTimeOffset? AwardPaidAtUtc { get; init; }
    public required string? Handover { get; init; }
    public required int? RatingOfWinner { get; init; }
    public required int? RatingOfClient { get; init; }
}
