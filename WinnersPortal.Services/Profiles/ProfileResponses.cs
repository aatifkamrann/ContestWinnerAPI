using WinnersPortal.Services.Opportunities;

namespace WinnersPortal.Services.Profiles;

/// <summary>A profile as its owner and other members read it.</summary>
public sealed record ProfileView
{
    public required Guid UserId { get; init; }
    public required string DisplayName { get; init; }
    public required string? AvatarUrl { get; init; }
    public required string Role { get; init; }
    public required string? GithubLogin { get; init; }
    public required bool GithubConnectable { get; init; }
    public required DateTimeOffset MemberSince { get; init; }
    public required bool Own { get; init; }
    public required string? Headline { get; init; }
    public required string? Bio { get; init; }
    public required ProfileCategoryView? PrimaryCategory { get; init; }
    public required IEnumerable<ProfileCategoryView?> SecondaryCategories { get; init; }
    public required string? WorkType { get; init; }
    public required string? Location { get; init; }
    public required string? TimeZone { get; init; }
    public required int? YearsExperience { get; init; }
    public required int? HoursPerWeek { get; init; }
    public required string? Availability { get; init; }
    public required List<string> PreferredDurations { get; init; }
    public required List<string> PreferredProjectTypes { get; init; }
    public required string? WorkingWindow { get; init; }
    public required string? WebsiteUrl { get; init; }
    public required string? LinkedInUrl { get; init; }
    public required DateTimeOffset? UpdatedAtUtc { get; init; }
    public required IEnumerable<ProfileSkillView> Skills { get; init; }
    public required IEnumerable<ProfileLanguageView> Languages { get; init; }
    public required IEnumerable<ProfileProjectView> Projects { get; init; }
    public required List<PaymentView?>? Payments { get; init; }
    /// <summary>For a client held back from the payment details — until they award the member, or until the member verifies — the line that says so. Null otherwise.</summary>
    public required string? PaymentsNote { get; init; }
    /// <summary>When the provider approved the person behind this profile; the verified mark beside the name.</summary>
    public required DateTimeOffset? IdentityVerifiedAtUtc { get; init; }
    public required ProfileStrength? Strength { get; init; }
    public required MeritView Merit { get; init; }
}

/// <summary>A kind of work on a profile: the stored key, and its name.</summary>
public sealed record ProfileCategoryView
{
    public required string Key { get; init; }
    public required string Label { get; init; }
}

/// <summary>A skill on a profile.</summary>
public sealed record ProfileSkillView
{
    public required string Name { get; init; }
    public required int Years { get; init; }
    public required string Level { get; init; }
}

/// <summary>A language on a profile.</summary>
public sealed record ProfileLanguageView
{
    public required string Name { get; init; }
    public required string Level { get; init; }
}

/// <summary>A portfolio project on a profile.</summary>
public sealed record ProfileProjectView
{
    public required string Title { get; init; }
    public required string? Description { get; init; }
    public required string? Outcome { get; init; }
    public required string? Role { get; init; }
    public required ProfileCategoryView? Category { get; init; }
    public required string? Tech { get; init; }
    public required string? Url { get; init; }
    public required string? RepoUrl { get; init; }
    public required int? Year { get; init; }
    public required int? Month { get; init; }
    public required bool MayShowPublicly { get; init; }
    public required IEnumerable<ProjectImageView> Images { get; init; }
}

/// <summary>A screenshot of a portfolio project.</summary>
public sealed record ProjectImageView
{
    public required Guid Id { get; init; }
    public required string Url { get; init; }
}

/// <summary>A stored payment method, decrypted for the people allowed to read it.</summary>
public sealed record PaymentView
{
    public required string Method { get; init; }
    public required string MethodLabel { get; init; }
    public required string? Label { get; init; }
    public required bool Unreadable { get; init; }
    public required IEnumerable<PaymentDetail> Details { get; init; }
}

/// <summary>One field of a payment method.</summary>
public sealed record PaymentDetail
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public required string Value { get; init; }
}

/// <summary>How complete the profile is, step by step; its owner's view only.</summary>
public sealed record ProfileStrength
{
    public required int Percent { get; init; }
    public required IEnumerable<StrengthStep> Steps { get; init; }
}

/// <summary>One step towards a complete profile.</summary>
public sealed record StrengthStep
{
    public required string Label { get; init; }
    public required bool Done { get; init; }
    public required string Detail { get; init; }
}

/// <summary>The member's merit score and its two halves.</summary>
public sealed record MeritView
{
    public required int Score { get; init; }
    public required int Max { get; init; }
    public required string Band { get; init; }
    public required int Completeness { get; init; }
    public required MeritHalf Portfolio { get; init; }
    public required MeritHalf Record { get; init; }
}

/// <summary>The portfolio or the record half of merit, with its parts.</summary>
public sealed record MeritHalf
{
    public required int Earned { get; init; }
    public required int Available { get; init; }
    public required IEnumerable<MeritLine> Parts { get; init; }
}

/// <summary>One line of merit arithmetic.</summary>
public sealed record MeritLine
{
    public required string Label { get; init; }
    public required int Earned { get; init; }
    public required int Available { get; init; }
    public required string Detail { get; init; }
}

/// <summary>The welcome screen after sign-up: rank, matches and the strongest open opportunity.</summary>
public sealed record WelcomeView
{
    public required string DisplayName { get; init; }
    public required string Role { get; init; }
    public required int? StrengthPercent { get; init; }
    public required int? MeritScore { get; init; }
    public required int? MeritMax { get; init; }
    public required WelcomeRank? Rank { get; init; }
    public required int OpenOpportunities { get; init; }
    public required int Matches { get; init; }
    public required OpportunityCard? Strongest { get; init; }
    public required WorkHighlight? StrongestWork { get; init; }
}

/// <summary>Where the member ranks among freelancers, overall and in their kind of work.</summary>
public sealed record WelcomeRank
{
    public required int Global { get; init; }
    public required int OfFreelancers { get; init; }
    public required CategoryRank? Category { get; init; }
}

/// <summary>Where the member ranks in their kind of work.</summary>
public sealed record CategoryRank
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public required int Rank { get; init; }
    public required int Of { get; init; }
}

/// <summary>The values other members already use, for the profile form to suggest.</summary>
public sealed record ProfileSuggestionsResponse
{
    public required IReadOnlyList<string> Locations { get; init; }
    public required IReadOnlyList<string> TimeZones { get; init; }
    public required IReadOnlyList<string> Languages { get; init; }
    public required List<string> Skills { get; init; }
    public required IReadOnlyList<string> Roles { get; init; }
    public required IReadOnlyList<string> WorkingWindows { get; init; }
}

/// <summary>The review's words: what the profile is strong for, and the changes that would open the most.</summary>
public sealed record ProfileReviewOutput
{
    public required string? StrongFor { get; init; }
    public required IEnumerable<ReviewImprovement> Improvements { get; init; }
}

/// <summary>A change to the profile and the merit it would gain.</summary>
public sealed record ReviewImprovement
{
    public required string Id { get; init; }
    public required int Gain { get; init; }
    public required string Text { get; init; }
}
