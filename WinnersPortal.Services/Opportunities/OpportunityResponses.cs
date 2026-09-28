using WinnersPortal.Services.Storage;

namespace WinnersPortal.Services.Opportunities;

/// <summary>A page of the opportunity feed, and the cursor for the next.</summary>
public sealed record OpportunityFeedResponse
{
    public required IEnumerable<OpportunityCard> Items { get; init; }
    public required string? NextCursor { get; init; }
}

/// <summary>An opportunity as a card on the feed.</summary>
public sealed record OpportunityCard
{
    public required string Slug { get; init; }
    public required string Title { get; init; }
    public required string Excerpt { get; init; }
    public required string? Category { get; init; }
    public required string? CategoryLabel { get; init; }
    public required string? Subcategory { get; init; }
    public required string? SubcategoryLabel { get; init; }
    public required int MinMeritScore { get; init; }
    public required List<string> Skills { get; init; }
    public required FitView? Fit { get; init; }
    public required decimal AwardAmount { get; init; }
    public required string Currency { get; init; }
    public required DateTimeOffset? DeadlineUtc { get; init; }
    public required DateTimeOffset? EntryCloseUtc { get; init; }
    public required bool EntryOpen { get; init; }
    public required DateTimeOffset? StartsAtUtc { get; init; }
    public required DateTimeOffset? PublishedAtUtc { get; init; }
    public required string Status { get; init; }
    public required string Delivery { get; init; }
    /// <summary>Entries must run with Docker Compose: each claim is built, and the board says whether it did.</summary>
    public required bool RequiresCompose { get; init; }
    public required string ClientName { get; init; }
    public required string? ClientAvatarUrl { get; init; }
    public required int EntrantCount { get; init; }
    public required int ApplicationCount { get; init; }
    public required int MilestoneCount { get; init; }
    public required int ClientAwardsPaid { get; init; }
    public required double? ClientRatingAvg { get; init; }
    public required int ClientRatingCount { get; init; }
}

/// <summary>How an opportunity fits the viewer: whether they can enter, how well they match, and why.</summary>
public sealed record FitView
{
    public required string Verdict { get; init; }
    public required bool CanEnter { get; init; }
    public required bool Recommended { get; init; }
    public required bool MeritOk { get; init; }
    public required bool SkillsOk { get; init; }
    public required bool CategoryOk { get; init; }
    public required int MeritScore { get; init; }
    public required int MinMerit { get; init; }
    public required IReadOnlyList<string> MissingSkills { get; init; }
    public required IReadOnlyList<string> Reasons { get; init; }
    public required int Match { get; init; }
    public required string MatchBand { get; init; }
    public required IEnumerable<FitFactor> Factors { get; init; }
    public required string? Risk { get; init; }
}

/// <summary>One factor of a fit, with its score.</summary>
public sealed record FitFactor
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public required int? Score { get; init; }
    public required string Detail { get; init; }
}

/// <summary>The figures over the feed: open opportunities, the rewards on offer, and how many end soon.</summary>
public sealed record OpportunityStatsResponse
{
    public required int Open { get; init; }
    public required decimal Rewards { get; init; }
    public required string Currency { get; init; }
    public required int EndingSoon { get; init; }
}

/// <summary>
/// An opportunity page. The same page for everybody who can see the opportunity at
/// all: what a viewer is, rather than whether they are signed in, decides
/// only the viewer-specific parts — their fit, their entry, their
/// application, and the review rows a client or administrator acts on.
/// </summary>
public sealed record OpportunityDetail
{
    public required Guid Id { get; init; }
    public required string Slug { get; init; }
    public required string Title { get; init; }
    public required string BriefMarkdown { get; init; }
    public required decimal AwardAmount { get; init; }
    public required string Currency { get; init; }
    public required string Status { get; init; }
    public required string Delivery { get; init; }
    public required bool RequiresCompose { get; init; }
    public required DateTimeOffset? DeadlineUtc { get; init; }
    public required DateTimeOffset? EntryCloseUtc { get; init; }
    public required bool EntryOpen { get; init; }
    public required DateTimeOffset? StartsAtUtc { get; init; }
    public required bool EntryClosesEarly { get; init; }
    public required DateTimeOffset? PublishedAtUtc { get; init; }
    public required DateTimeOffset? CancelledAtUtc { get; init; }
    public required string? CancelReason { get; init; }
    public required string ClientName { get; init; }
    public required string? ClientAvatarUrl { get; init; }
    public required ClientRecord ClientRecord { get; init; }
    public required IEnumerable<OpportunityMilestoneView> Milestones { get; init; }
    public required List<AttachmentSummary> Attachments { get; init; }
    public required IEnumerable<OpportunityRequirementView> Requirements { get; init; }
    public required IEnumerable<OpportunityCriterionView> Criteria { get; init; }
    public required string? Category { get; init; }
    public required string? CategoryLabel { get; init; }
    public required string? Subcategory { get; init; }
    public required string? SubcategoryLabel { get; init; }
    public required int MinMeritScore { get; init; }
    public required List<string> Skills { get; init; }
    public required FitView? ViewerFit { get; init; }
    public required string? MetaTitle { get; init; }
    public required string? MetaDescription { get; init; }
    public required OpportunityNarrative? Narrative { get; init; }
    public required List<OpportunityEntrant> Entrants { get; init; }
    public required int EntrantCount { get; init; }
    public required int ApplicationCount { get; init; }
    public required ViewerApplication? ViewerApplication { get; init; }
    public required IReadOnlyList<ApplicationReviewRow>? Applications { get; init; }
    public required int? MaxEntries { get; init; }
    public required bool EntriesFull { get; init; }
    public required bool ShowBoard { get; init; }
    public required bool IsOwner { get; init; }
    public required bool IsAdmin { get; init; }
    public required bool? GithubConfigured { get; init; }
    public required bool? StorageConfigured { get; init; }
    public required ViewerEntry? ViewerEntry { get; init; }
    public required OpportunityAward? Award { get; init; }
}

/// <summary>The client's track record on the portal.</summary>
public sealed record ClientRecord
{
    public required int OpportunitiesPosted { get; init; }
    public required int OpportunitiesCancelled { get; init; }
    public required int AwardsAnnounced { get; init; }
    public required int AwardsPaid { get; init; }
    public required double? MedianDaysToPay { get; init; }
    public required int? OldestUnpaidDays { get; init; }
    public required double? RatingAvg { get; init; }
    public required int RatingCount { get; init; }
    public required IEnumerable<ClientReview> Reviews { get; init; }
}

/// <summary>A rating a winner left for the client.</summary>
public sealed record ClientReview
{
    public required int Stars { get; init; }
    public required string? Comment { get; init; }
    public required string ByName { get; init; }
    public required string OpportunityTitle { get; init; }
    public required DateTimeOffset AtUtc { get; init; }
}

/// <summary>A milestone of the opportunity's plan.</summary>
public sealed record OpportunityMilestoneView
{
    public required string Title { get; init; }
    public required string? Description { get; init; }
    public required DateTimeOffset? DueUtc { get; init; }
    public required int? WeightPercent { get; init; }
}

/// <summary>A requirement of the brief.</summary>
public sealed record OpportunityRequirementView
{
    public required string Title { get; init; }
    public required string Detail { get; init; }
}

/// <summary>A judging criterion and its points.</summary>
public sealed record OpportunityCriterionView
{
    public required int Points { get; init; }
    public required string Title { get; init; }
    public required string? Description { get; init; }
}

/// <summary>The AI-drafted note on the board, and when it was written.</summary>
public sealed record OpportunityNarrative
{
    public required string Text { get; init; }
    public required DateTimeOffset? AtUtc { get; init; }
}

/// <summary>An entrant on the opportunity page; the standing and the files only where the viewer may see them.</summary>
public sealed record OpportunityEntrant
{
    public required string DisplayName { get; init; }
    public required string? AvatarUrl { get; init; }
    public required Guid UserId { get; init; }
    public required string? Headline { get; init; }
    public required WorkHighlight? PastWork { get; init; }
    public required int MeritScore { get; init; }
    public required string MeritBand { get; init; }
    public required string? Note { get; init; }
    public required DateTimeOffset EnteredAtUtc { get; init; }
    public required int Wins { get; init; }
    public required double? RatingAvg { get; init; }
    public required int RatingCount { get; init; }
    public required IEnumerable<int>? MilestonesDone { get; init; }
    public required List<string>? MilestoneStates { get; init; }
    public required MilestoneTally? Standing { get; init; }
    public required int? StandingScore { get; init; }
    public required string? StandingBand { get; init; }
    public required int? StandingRank { get; init; }
    public required List<StandingPartView>? StandingParts { get; init; }
    public required DateTimeOffset? LastPushAtUtc { get; init; }
    public required int? FilesUploaded { get; init; }
    public required List<SubmissionSummary>? Files { get; init; }
    public required Guid? EntryId { get; init; }
    public required string? RepoFullName { get; init; }
    /// <summary>
    /// One per milestone, in order, where the opportunity requires Docker
    /// Compose and the board shows: the build of each claim, as public as
    /// the claim itself. Null otherwise.
    /// </summary>
    public required List<BuildCellView>? Builds { get; init; }
    /// <summary>
    /// The final version's preview — none, pending, starting or running —
    /// once the entry is frozen on an opportunity that runs previews and is in
    /// review or awarded, for the client, an administrator and the entrant.
    /// Null otherwise.
    /// </summary>
    public required string? FinalPreview { get; init; }
}

/// <summary>The build behind one board cell: its state, the checkpoint the dialog asks about, and whether its preview is up.</summary>
/// <param name="Status">none (unclaimed, or nothing to build), pending, building, built or failed.</param>
/// <param name="Preview">pending, starting or running while the milestone's preview is on its way or up; null otherwise.</param>
public sealed record BuildCellView(string Status, Guid? CheckpointId, DateTimeOffset? FinishedAtUtc, string? Preview = null);

/// <summary>The past project that fits an opportunity best.</summary>
public sealed record WorkHighlight
{
    public required string Title { get; init; }
    public required string? Outcome { get; init; }
}

/// <summary>An entry's milestones counted: done, on time, late, overdue, and how many have a date.</summary>
public sealed record MilestoneTally
{
    public required int Done { get; init; }
    public required int OnTime { get; init; }
    public required int Late { get; init; }
    public required int Overdue { get; init; }
    public required int Dated { get; init; }
}

/// <summary>One part of an entrant's standing score, with its arithmetic.</summary>
public sealed record StandingPartView
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public required int Earned { get; init; }
    public required int Available { get; init; }
    public required string Detail { get; init; }
}

/// <summary>The viewer's own application to this opportunity.</summary>
public sealed record ViewerApplication
{
    public required Guid Id { get; init; }
    public required string Status { get; init; }
    public required DateTimeOffset SubmittedAtUtc { get; init; }
    public required DateTimeOffset? DecidedAtUtc { get; init; }
}

/// <summary>The viewer's own entry in this opportunity.</summary>
public sealed record ViewerEntry
{
    public required Guid Id { get; init; }
    public required string GithubUsername { get; init; }
    public required DateTimeOffset EnteredAtUtc { get; init; }
    public required string? RepoFullName { get; init; }
    public required string ProvisionStatus { get; init; }
    public required IEnumerable<int> MilestonesDone { get; init; }
    public required IEnumerable<SubmissionSummary> Files { get; init; }
    public required string? UploadProblem { get; init; }
    public required ViewerStanding? Standing { get; init; }
    /// <summary>
    /// The final version's preview, as <see cref="OpportunityEntrant.FinalPreview"/>
    /// reads it — the entrant runs their own final from their panel.
    /// </summary>
    public required string? FinalPreview { get; init; }
}

/// <summary>The viewer's standing among the entrants, with its parts and the next step that would raise it.</summary>
public sealed record ViewerStanding
{
    public required int Score { get; init; }
    public required string Band { get; init; }
    public required int Rank { get; init; }
    public required int Of { get; init; }
    public required IEnumerable<StandingPartView> Parts { get; init; }
    public required string? NextStep { get; init; }
}

/// <summary>The opportunity's award and its ratings; the handover only for the opportunity's owner.</summary>
public sealed record OpportunityAward
{
    public required string WinnerName { get; init; }
    public required DateTimeOffset AnnouncedAtUtc { get; init; }
    public required DateTimeOffset? PaidAtUtc { get; init; }
    public required AwardRating? RatingOfClient { get; init; }
    public required AwardRating? RatingOfWinner { get; init; }
    public required bool ViewerIsWinner { get; init; }
    public required bool CanRate { get; init; }
    public required MyRating? MyRating { get; init; }
    public required Guid? Id { get; init; }
    public required string? Handover { get; init; }
    public required Guid? WinnerEntryId { get; init; }
    /// <summary>The winner's profile, where the owner — and, of clients, only the owner — reads how to pay them.</summary>
    public required Guid? WinnerUserId { get; init; }
    public required string? TransferTargetLogin { get; init; }
    public required DateTimeOffset? TransferRequestedAtUtc { get; init; }
    public required DateTimeOffset? HandoverVerifiedAtUtc { get; init; }
    public required string? HandoverNote { get; init; }
    public required string? WinnerRepoFullName { get; init; }
}

/// <summary>A rating one party of an award left for the other.</summary>
public sealed record AwardRating
{
    public required int Stars { get; init; }
    public required string? Comment { get; init; }
    public required string ByName { get; init; }
    public required DateTimeOffset UpdatedAtUtc { get; init; }
}

/// <summary>The viewer's own rating of this award.</summary>
public sealed record MyRating
{
    public required int Stars { get; init; }
    public required string? Comment { get; init; }
}

/// <summary>A saved opportunity.</summary>
public sealed record OpportunitySavedResponse
{
    public required Guid Id { get; init; }
    public required string Slug { get; init; }
}

/// <summary>A published opportunity, with its dates as publishing set them.</summary>
public sealed record PublishResponse
{
    public required string Slug { get; init; }
    public required string Status { get; init; }
    public required DateTimeOffset? StartsAtUtc { get; init; }
    public required DateTimeOffset? DeadlineUtc { get; init; }
    public required DateTimeOffset? EntryCloseUtc { get; init; }
}

/// <summary>An opportunity as a freelancer's own lists name it.</summary>
public sealed record OpportunitySummary
{
    public required string Slug { get; init; }
    public required string Title { get; init; }
    public required decimal AwardAmount { get; init; }
    public required string Currency { get; init; }
    public required DateTimeOffset? DeadlineUtc { get; init; }
    public required DateTimeOffset? StartsAtUtc { get; init; }
    public required DateTimeOffset? PublishedAtUtc { get; init; }
    public required string Status { get; init; }
    public required string Delivery { get; init; }
}

/// <summary>One of the client's own opportunities.</summary>
public sealed record MyOpportunityRow
{
    public required Guid Id { get; init; }
    public required string Slug { get; init; }
    public required string Title { get; init; }
    public required decimal AwardAmount { get; init; }
    public required string Currency { get; init; }
    public required DateTimeOffset? DeadlineUtc { get; init; }
    public required DateTimeOffset? StartsAtUtc { get; init; }
    public required DateTimeOffset? PublishedAtUtc { get; init; }
    public required string Status { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required int EntrantCount { get; init; }
    public required int MilestoneCount { get; init; }
    public required int ApplicationsWaiting { get; init; }
}
