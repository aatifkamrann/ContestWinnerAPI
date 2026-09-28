namespace WinnersPortal.Services.Opportunities;

/// <summary>
/// Everything the application form needs: the opportunity, the fit, the
/// eligibility, the profile, and any application already made.
/// </summary>
public sealed record ApplyFormResponse
{
    public required ApplyFormOpportunity Opportunity { get; init; }
    public required FitView? Fit { get; init; }
    public required ApplyEligibility Eligibility { get; init; }
    public required ApplyProfile Profile { get; init; }
    public required IEnumerable<AdvantageOption> Advantages { get; init; }
    public required IReadOnlyList<string> SuggestedAdvantages { get; init; }
    public required ApplicationView? Application { get; init; }
}

/// <summary>The opportunity as the application form describes it.</summary>
public sealed record ApplyFormOpportunity
{
    public required string Slug { get; init; }
    public required string Title { get; init; }
    public required decimal AwardAmount { get; init; }
    public required string Currency { get; init; }
    public required string Status { get; init; }
    public required string Delivery { get; init; }
    public required bool NeedsGithubUsername { get; init; }
    public required string? Category { get; init; }
    public required string? CategoryLabel { get; init; }
    public required List<string> Skills { get; init; }
    public required int MinMeritScore { get; init; }
    public required DateTimeOffset? StartsAtUtc { get; init; }
    public required DateTimeOffset? DeadlineUtc { get; init; }
    public required DateTimeOffset? EntryCloseUtc { get; init; }
    public required bool EntryOpen { get; init; }
    public required int? Weeks { get; init; }
    public required string ClientName { get; init; }
    public required int EntrantCount { get; init; }
    public required int? MaxEntries { get; init; }
    public required int ApplicationCount { get; init; }
}

/// <summary>Whether the viewer can apply, and the first thing in the way when not.</summary>
public sealed record ApplyEligibility
{
    public required bool CanApply { get; init; }
    public required string? Problem { get; init; }
    public required bool EmailVerified { get; init; }
    public required bool PhoneVerified { get; init; }
    /// <summary>Whether this portal asks freelancers to verify their identity before applying.</summary>
    public required bool IdentityRequired { get; init; }
    public required bool IdentityVerified { get; init; }
    public required bool AvailabilitySet { get; init; }
    public required bool AvailabilityOk { get; init; }
}

/// <summary>What the application form fills in from the applicant's profile.</summary>
public sealed record ApplyProfile
{
    public required string? Summary { get; init; }
    public required int? HoursPerWeek { get; init; }
    public required List<ApplyProject> Projects { get; init; }
    public required int RelevantCount { get; init; }
}

/// <summary>A portfolio project the form offers, with how relevant it is to the opportunity.</summary>
public sealed record ApplyProject
{
    public required Guid Id { get; init; }
    public required string Title { get; init; }
    public required string? Role { get; init; }
    public required string? Tech { get; init; }
    public required string? Outcome { get; init; }
    public required int? Relevance { get; init; }
}

/// <summary></summary>
public sealed record AdvantageOption
{
    public required string Key { get; init; }
    public required string Label { get; init; }
}

/// <summary>
/// An application as its applicant and the opportunity's owner read it: what was
/// written, the evaluation, and where it stands.
/// </summary>
public sealed record ApplicationView
{
    public required Guid Id { get; init; }
    public required string Status { get; init; }
    public required DateTimeOffset SubmittedAtUtc { get; init; }
    public required DateTimeOffset? DecidedAtUtc { get; init; }
    public required Guid? EntryId { get; init; }
    public required int Match { get; init; }
    public required int MeritScore { get; init; }
    public required string Summary { get; init; }
    public required string Approach { get; init; }
    public required IReadOnlyList<PortfolioItem> Portfolio { get; init; }
    public required string Commitment { get; init; }
    public required int HoursPerWeek { get; init; }
    public required IEnumerable<string> Advantages { get; init; }
    public required string GithubUsername { get; init; }
    public required EvaluationView Evaluation { get; init; }
    public required int? StrongerThan { get; init; }
    public required ApplicationAdvice? Advice { get; init; }
    public required bool ClosedUndecided { get; init; }
    public required string? Outcome { get; init; }
    public required ApplicationStage[] Stages { get; init; }
}

/// <summary>The evaluation at submission, with the model's words joined on where they have landed.</summary>
public sealed record EvaluationView
{
    public required string Status { get; init; }
    public required int Percent { get; init; }
    public required string Band { get; init; }
    public required IEnumerable<WordedLine> Strengths { get; init; }
    public required IEnumerable<WordedLine> Risks { get; init; }
    public required string? Note { get; init; }
    public required bool Worded { get; init; }
    public required string? Provider { get; init; }
    public required DateTimeOffset? CompletedAtUtc { get; init; }
}

/// <summary>An evaluation line in the model's words where it answered, the portal's label otherwise.</summary>
public sealed record WordedLine
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required string Text { get; init; }
}

/// <summary>One step of the application's pipeline and whether it is done, current or still to come.</summary>
public sealed record ApplicationStage
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public required string State { get; init; }
}

/// <summary>A decided application: where it went, and the entry a selection opened.</summary>
public sealed record DecisionResponse
{
    public required string Status { get; init; }
    public required Guid? EntryId { get; init; }
    public required DateTimeOffset DecidedAtUtc { get; init; }
}

/// <summary>One of the viewer's own applications, with the opportunity it is for.</summary>
public sealed record MyApplicationRow
{
    public required Guid Id { get; init; }
    public required string Status { get; init; }
    public required DateTimeOffset SubmittedAtUtc { get; init; }
    public required DateTimeOffset? DecidedAtUtc { get; init; }
    public required Guid? EntryId { get; init; }
    public required int Match { get; init; }
    public required int MeritScore { get; init; }
    public required int PortfolioCount { get; init; }
    public required int HoursPerWeek { get; init; }
    public required bool EvaluationPending { get; init; }
    public required FitView? Fit { get; init; }
    public required OpportunitySummary Opportunity { get; init; }
}

/// <summary>One application on the owner's review list.</summary>
public sealed record ApplicationReviewRow
{
    public required Applicant Applicant { get; init; }
    public required ApplicationView Application { get; init; }
    public required string? WithdrawnReason { get; init; }
}

/// <summary>Who applied, as the review list shows them.</summary>
public sealed record Applicant
{
    public required Guid UserId { get; init; }
    public required string DisplayName { get; init; }
    public required string? AvatarUrl { get; init; }
    public required string? Headline { get; init; }
}
