namespace WinnersPortal.Domain;

public enum ApplicationStatus
{
    /// <summary>Submitted; the client (or an administrator) has not decided yet.</summary>
    UnderReview = 0,

    /// <summary>Picked to compete: an entry was made from it, and <see cref="Application.EntryId"/> says which.</summary>
    Selected = 1,

    /// <summary>Not picked. The applicant is told, and may still be selected while the opportunity is open.</summary>
    NotSelected = 2,

    /// <summary>Selected, then taken off the opportunity with a reason. Final.</summary>
    Removed = 3,

    /// <summary>
    /// Selected, then the applicant withdrew the entry it made. Their own
    /// answer, and final like a removal: one application per opportunity, so
    /// withdrawing is leaving that opportunity rather than stepping out of it.
    /// </summary>
    Withdrawn = 4,
}

/// <summary>
/// How much of their week an applicant says they can give this opportunity —
/// the application's own answer, beside the hours, and not the profile's
/// "when could you start". A profile says what is generally true; this
/// says what is true for these weeks.
/// </summary>
public enum Commitment
{
    Full = 0,
    Partial = 1,
    Limited = 2,
}

/// <summary>
/// A freelancer asking to compete in an opportunity. The door used to open at
/// once — pass the merit floor and the required skills and you were in.
/// Now the same door leads to a review: the applicant walks through eight
/// steps that read their own profile against the brief, writes how they
/// would approach the work, picks the past work that proves it and
/// confirms they can deliver, and the client (or an administrator) selects
/// who competes. An entry is made only from a selected application, by the
/// same rules the old door enforced, so a selection can still be refused
/// by a merit floor the applicant has since fallen under.
///
/// One application per freelancer per opportunity, whatever became of it: a
/// unique index says so, and "not selected" is an answer, not an invitation
/// to try the same brief again. What was written is kept as written — the
/// summary and the portfolio are copies of the profile as it stood when
/// they applied, because that is what the client read and decided on.
/// </summary>
public sealed class Application
{
    // Column limits: the one place a length is stated; the rules and the
    // DbContext both read it here.
    public const int MaxApproach = 1000;

    public Guid Id { get; set; }

    public Guid OpportunityId { get; set; }
    public Opportunity? Opportunity { get; set; }

    public Guid FreelancerId { get; set; }
    public User? Freelancer { get; set; }

    public ApplicationStatus Status { get; set; } = ApplicationStatus.UnderReview;

    /// <summary>The professional summary as reviewed for this application — the profile's, edited or not.</summary>
    public required string Summary { get; set; }

    /// <summary>How they would approach the work: plan, architecture and delivery, in their words.</summary>
    public required string Approach { get; set; }

    /// <summary>
    /// The projects they attached, copied from the profile as it stood:
    /// a JSON list of title, role, tech, outcome and the relevance figure
    /// the portal gave each. A copy, because a profile edit replaces its
    /// projects wholesale and the client decided on what they read.
    /// </summary>
    public string PortfolioJson { get; set; } = "[]";

    /// <summary>How many are in <see cref="PortfolioJson"/> — the review row's figure without parsing it.</summary>
    public int PortfolioCount { get; set; }

    public Commitment Commitment { get; set; }

    /// <summary>Hours a week they commit to this opportunity, 1–80.</summary>
    public int HoursPerWeek { get; set; }

    /// <summary>The competitive advantages they claim, as keys from ApplicationRules.Advantages, in that list's order.</summary>
    public string[] Advantages { get; set; } = [];

    /// <summary>Where the repository invitation goes if they are selected; "" on an upload-only opportunity.</summary>
    public required string GithubUsername { get; set; }

    /// <summary>The match figure at the moment they applied — the review row's, and what "stronger than" compares.</summary>
    public int MatchAtSubmit { get; set; }

    public int MeritAtSubmit { get; set; }

    /// <summary>
    /// The portal's own evaluation at the moment they applied — the match,
    /// its word, and the strengths and risks read off the assessment
    /// (ApplicationRules.Evaluate), as JSON. Kept, because the model's
    /// words answer to these ids and the client read this version.
    /// </summary>
    public string EvaluationJson { get; set; } = "{}";

    public DateTimeOffset SubmittedAtUtc { get; set; }

    // ---- the decision -----------------------------------------------

    /// <summary>When the answer last changed: the decision, a removal, or the applicant's withdrawal.</summary>
    public DateTimeOffset? DecidedAtUtc { get; set; }

    /// <summary>The client, or the administrator who acted over them.</summary>
    public Guid? DecidedByUserId { get; set; }

    /// <summary>The entry a selection made. Null until selected; stays set if that entry is later withdrawn or removed.</summary>
    public Guid? EntryId { get; set; }
}
