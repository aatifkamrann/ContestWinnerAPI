namespace WinnersPortal.Domain;

/// <summary>
/// The AI features the portal knows, each behind its own switch under the
/// master one (Settings/AiOptions.cs). An <see cref="AiArtifact"/> records
/// which one produced it.
/// </summary>
public enum AiFeature
{
    EntryDigest,
    MilestoneExtraction,
    BriefCoach,
    SpamFilter,
    ProgressNarrative,
    SeoMetadata,

    /// <summary>
    /// Reading notes on an opportunity's board: what each entrant's standing
    /// shows and what to open before deciding who stays. The score and the
    /// order are the portal's arithmetic; the model only puts them in words.
    /// </summary>
    StandingNotes,

    /// <summary>
    /// Which kind of work a draft opportunity is, read off its title and brief
    /// and offered to the client as a suggestion they confirm or ignore.
    /// </summary>
    CategorySuggestion,

    /// <summary>
    /// Which kinds of work one freelancer does, read off the skills on their
    /// profile. It is the whole of the Recommended judgement: with this off,
    /// no opportunity is ever marked as outside somebody’s usual work.
    /// </summary>
    RecommendedMatching,

    /// <summary>
    /// A first draft of the About on a member’s profile, written from what
    /// they have typed on the form so far — title, skills, languages, past
    /// work. Theirs to take, edit or leave; never applied for them.
    /// </summary>
    ProfileSummary,

    /// <summary>
    /// The box on a freelancer's own profile review: what the profile is
    /// strongest for, and up to three things that would make more of the
    /// open opportunities theirs to enter, each with a figure. Read off the
    /// saved profile and the open opportunities' doors; re-read when either
    /// changes. Advice to the member about their own page — never a
    /// ranking, and never shown to anybody else.
    /// </summary>
    ProfileReview,

    /// <summary>
    /// A first draft of how one freelancer would approach an opportunity's work,
    /// written from the brief and their own profile, on the application's
    /// approach step. Theirs to take, edit or leave; never submitted for them.
    /// </summary>
    ProjectApproach,

    /// <summary>
    /// The words of the evaluation box on an application to compete: the
    /// strengths and risks are the portal's arithmetic and are on the page
    /// at once; the model is given them and asked to put each into a short
    /// phrase about this application, and to say what the client should
    /// open first. Never a verdict — the client decides.
    /// </summary>
    ApplicationEvaluation,

    /// <summary>
    /// The technical requirements table drafted from an opportunity form — the
    /// constraints the brief states or clearly implies, one row each, read
    /// off the title, brief, kind of work and required skills above the
    /// table. Rows to add, edit or ignore; never written into the table.
    /// </summary>
    RequirementsSuggestion,

    /// <summary>
    /// The scoring rubric drafted from an opportunity form — what the work is
    /// judged on and how many of a hundred points each line carries, read
    /// off the brief, the requirements and the milestone checklist above
    /// it. The client edits it before publishing; never applied for them.
    /// </summary>
    CriteriaSuggestion,
}
