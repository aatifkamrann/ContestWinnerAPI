using WinnersPortal.Services.Auth;
using WinnersPortal.Services.Profiles;
using System.Text.Json;
using WinnersPortal.Domain;

namespace WinnersPortal.Services.Opportunities;

/// <summary>
/// One project as an application carries it: what the client reads on the
/// review row, and the relevance figure the portal gave it at the time.
/// </summary>
public sealed record PortfolioItem(string Title, string? Role, string? Tech, string? Outcome, int? Relevance);

/// <summary>
/// One line of the portal's own evaluation of an application — a strength
/// or a risk, by id, with the words the portal uses for it until the model
/// has put it in words of its own.
/// </summary>
public sealed record EvaluationLine(string Id, string Label);

/// <summary>
/// The evaluation as arithmetic: the match figure, the word for it, and
/// the strengths and risks read off the assessment's lines and the
/// portfolio attached. Every figure here is the portal's; the model is
/// only ever asked to word the lines.
/// </summary>
public sealed record Evaluation(
    int Percent, string Band, IReadOnlyList<EvaluationLine> Strengths, IReadOnlyList<EvaluationLine> Risks);

/// <summary>
/// What an applicant who was not selected can take to the next
/// application: what held up, and one line per weakness. Read off the
/// portal's evaluation, never off the client, who gives no reasons.
/// </summary>
public sealed record ApplicationAdvice(IReadOnlyList<string> Strong, IReadOnlyList<string> Improve);

/// <summary>
/// The rules of applying to compete, as pure functions: how long an
/// approach may be, which advantages may be claimed, how relevant a past
/// project is to a brief, what the evaluation says, and when an
/// application may be submitted or decided. Nothing here touches the
/// database, so all of it is tested directly.
/// </summary>
public static class ApplicationRules
{
    public const int MinApproach = 100;
    public const int MaxApproach = Application.MaxApproach;

    /// <summary>The review row shows a handful; more than this is a profile, not a selection.</summary>
    public const int MaxPortfolio = 10;

    public const int MinHours = 1;
    public const int MaxHours = Profiles.ProfileRules.MaxHoursPerWeek;

    /// <summary>A past project at or above this is highlighted, and ticked before the applicant chooses.</summary>
    public const int RelevantFrom = 60;

    /// <summary>
    /// What an applicant may claim as their competitive advantage. A closed
    /// list, so the client reads the same eight words on every row and
    /// nobody writes their own superlative. The portal suggests some from
    /// what it can see (<see cref="SuggestedAdvantages"/>); the applicant
    /// decides.
    /// </summary>
    public static readonly IReadOnlyList<(string Key, string Label)> Advantages =
    [
        ("technical-depth", "Technical depth"),
        ("delivery-record", "Delivery record"),
        ("similar-projects", "Previous similar projects"),
        ("domain-expertise", "Domain expertise"),
        ("research-depth", "Research depth"),
        ("cost-efficiency", "Cost efficiency"),
        ("fast-turnaround", "Fast turnaround"),
        ("clear-communication", "Clear communication"),
    ];

    public static string StatusName(ApplicationStatus s) => s switch
    {
        ApplicationStatus.UnderReview => "under_review",
        ApplicationStatus.Selected => "selected",
        ApplicationStatus.Removed => "removed",
        ApplicationStatus.Withdrawn => "withdrawn",
        _ => "not_selected",
    };

    public static string CommitmentName(Commitment c) => c switch
    {
        Commitment.Full => "full",
        Commitment.Partial => "partial",
        _ => "limited",
    };

    public static Commitment? ParseCommitment(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "full" => Commitment.Full,
        "partial" => Commitment.Partial,
        "limited" => Commitment.Limited,
        _ => null,
    };

    /// <summary>The approach as stored, or null with why not: trimmed, and between the two lengths.</summary>
    public static string? CleanApproach(string? text, out string? problem)
    {
        problem = null;
        var t = (text ?? "").Trim();
        if (t.Length < MinApproach)
        {
            problem = $"Say more about your approach — at least {MinApproach} characters, so the client has a plan to read.";
            return null;
        }
        if (t.Length > MaxApproach)
        {
            problem = $"Keep the approach under {MaxApproach} characters.";
            return null;
        }
        return t;
    }

    /// <summary>The claimed advantages as stored: known keys only, in the list's order, each once.</summary>
    public static string[] CleanAdvantages(IEnumerable<string?>? keys)
    {
        var wanted = (keys ?? []).Where(k => k is not null).Select(k => k!.Trim()).ToHashSet(StringComparer.Ordinal);
        return Advantages.Select(a => a.Key).Where(wanted.Contains).ToArray();
    }

    /// <summary>
    /// How much one past project has to do with a brief, 0–100, or null
    /// when the brief names neither a kind of work nor a skill and there
    /// is nothing to measure it against. Filed under the brief's kind of
    /// work is most of the answer (60); the rest is how many of the
    /// required skills its own words mention. A brief with no skills is
    /// answered by the kind of work alone; one with no kind of work by the
    /// skills alone. The figure the wizard shows beside each project, and
    /// the one it ticks the relevant ones by.
    /// </summary>
    public static int? Relevance(ProjectFacts p, IReadOnlyList<string> required, string? category)
    {
        var key = OpportunityCategories.Find(category)?.Key;
        var skillWords = required.Select(s => Assessment.Words(s)).ToList();
        if (key is null && skillWords.Count == 0) return null;

        var filed = key is not null && string.Equals(p.Category, key, StringComparison.OrdinalIgnoreCase);
        if (skillWords.Count == 0) return filed ? 100 : 0;
        var mentioned = skillWords.Count(w => p.Text.Contains(w, StringComparison.Ordinal));
        var skillShare = (double)mentioned / skillWords.Count;
        if (key is null) return (int)Math.Round(100 * skillShare, MidpointRounding.AwayFromZero);
        return (filed ? 60 : 0) + (int)Math.Round(40 * skillShare, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// The advantages the portal would tick before the applicant looks:
    /// technical depth where the required skills read strong, a delivery
    /// record where the on-time line does, previous similar projects where
    /// any past work fits, domain expertise where the brief is their kind
    /// of work. Suggestions — the applicant takes or leaves each.
    /// </summary>
    public static IReadOnlyList<string> SuggestedAdvantages(Fit fit, int relevantProjects)
    {
        var picks = new List<string>();
        if (Score(fit, "skills") >= Assessment.StrongLine) picks.Add("technical-depth");
        if (Score(fit, "record") >= Assessment.StrongLine) picks.Add("delivery-record");
        if (relevantProjects > 0) picks.Add("similar-projects");
        if (fit.CategoryOk && fit.Recommended) picks.Add("domain-expertise");
        return picks;
    }

    /// <summary>
    /// "Stronger than N% of applicants": how many of the other applications
    /// to the same opportunity carried a lower match figure, as a share of
    /// them. Null with no other applicant — there is nobody to be stronger
    /// than, and the page says so rather than showing 100%.
    /// </summary>
    public static int? StrongerThan(int match, IReadOnlyList<int> others) =>
        others.Count == 0
            ? null
            : (int)Math.Round(100.0 * others.Count(o => o < match) / others.Count, MidpointRounding.AwayFromZero);

    /// <summary>
    /// The evaluation from the assessment: the match figure and its word,
    /// then a strength for each line that reads strong and a risk for each
    /// that reads weak, plus the portfolio — attached is a strength, none
    /// attached is a risk. A line the portal cannot read yet is neither.
    /// </summary>
    public static Evaluation Evaluate(Fit fit, int portfolioCount)
    {
        var strengths = new List<EvaluationLine>();
        var risks = new List<EvaluationLine>();
        void Line(string key, string strong, string weak)
        {
            var score = Score(fit, key);
            if (score is null) return;
            if (score >= 80) strengths.Add(new(key, strong));
            else if (score < 60) risks.Add(new(key, weak));
        }
        Line("skills", "Technical fit", "Gaps in the required skills");
        if (portfolioCount > 0) strengths.Add(new("portfolio", "Relevant portfolio"));
        else risks.Add(new("portfolio", "No portfolio attached"));
        Line("projects", "Domain experience", "Limited domain experience");
        Line("record", "Delivery history", "Late milestones on record");
        Line("availability", "Available for the timeline", "Thin availability for the timeline");

        var band = !fit.CategoryOk ? OutsideBand
            : fit.Match >= 80 ? "Strong Candidate"
            : fit.Match >= 60 ? "Qualified Candidate"
            : "Developing Candidate";
        return new(fit.Match, band, strengths, risks);
    }

    /// <summary>The band of an application from outside the applicant's usual kind of work.</summary>
    public const string OutsideBand = "Outside their usual work";

    /// <summary>
    /// After a no: what held up and what to strengthen, from the evaluation
    /// stored at filing. Strong is the kind of work where the brief's
    /// matched theirs, otherwise the strengths the evaluation named.
    /// Improve is one line per weakness, the cheapest to fix first — and
    /// competition experience for as long as the delivery record is not a
    /// strength, which is every applicant who has not delivered here yet.
    /// </summary>
    public static ApplicationAdvice AdviceFor(Evaluation evaluation, string? categoryLabel)
    {
        var inCategory = evaluation.Band != OutsideBand;
        var strong = new List<string>();
        if (inCategory && !string.IsNullOrWhiteSpace(categoryLabel))
            strong.Add($"{categoryLabel} experience");
        else
            strong.AddRange(evaluation.Strengths.Select(s => s.Label));

        bool Risk(string id) => evaluation.Risks.Any(r => r.Id == id);
        bool Strength(string id) => evaluation.Strengths.Any(s => s.Id == id);
        var improve = new List<string>();
        if (Risk("portfolio") || Risk("projects")) improve.Add("Add more relevant portfolio examples");
        if (Risk("skills")) improve.Add("List the required skills you have on your profile");
        if (Risk("record")) improve.Add("Deliver your next milestones on time");
        else if (!Strength("record")) improve.Add("Increase competition experience");
        if (Risk("availability")) improve.Add("Keep more of your week free for the opportunity timeline");
        if (!inCategory) improve.Add("Apply where the kind of work matches what your profile shows");
        return new(strong, improve);
    }

    private static int? Score(Fit fit, string key) => fit.Factors.FirstOrDefault(f => f.Key == key)?.Score;

    /// <summary>
    /// Why an application cannot be submitted, or null when it can. The
    /// same door the entry used to be, in the same order, then the two
    /// answers that only exist now: already in, and already applied.
    /// </summary>
    public static string? SubmitProblem(
        OpportunityStatus status, bool entryOpen, Fit? fit, bool removedBefore, bool alreadyEntered, bool alreadyApplied,
        bool identityOwed = false)
    {
        if (status != OpportunityStatus.Open) return "This opportunity is no longer open for entry.";
        if (!entryOpen) return "The last joining date has passed — this opportunity is closed to new applicants.";
        // Before the fit: a door the portal shut, with a link out of it,
        // comes before a door the client's own terms shut.
        if (identityOwed) return Identity.IdentityRules.ApplyProblem;
        if (fit is not null && OpportunityFit.EntryProblem(fit) is { } unfit) return unfit;
        if (Removal.ReentryProblem(removedBefore) is { } shut) return shut;
        if (alreadyEntered) return "You are already competing in this opportunity.";
        if (alreadyApplied) return "You have already applied to this opportunity.";
        return null;
    }

    /// <summary>
    /// Why an application cannot be given this answer, or null when it can.
    /// Only while the opportunity is open — an opportunity in review or decided has
    /// no field left to join or leave. An answer can be changed: a
    /// turned-down applicant can still be selected, and a selection can be
    /// taken back until work arrives in the entry it made. After that,
    /// taking somebody out is a removal, with the reason and the clone
    /// window that come with it — and a removal, like a withdrawal, is final.
    /// </summary>
    /// <param name="kind">
    /// Paid by milestone, selecting is hiring: the opportunity turns awarded
    /// at once, so the one decision still open afterwards is taking the hire
    /// back — before any work has arrived.
    /// </param>
    public static string? DecisionProblem(
        ApplicationStatus current, ApplicationStatus next, OpportunityStatus opportunityStatus, bool workArrived,
        OpportunityKind kind = OpportunityKind.Competitive)
    {
        if (current == ApplicationStatus.Removed)
            return "This applicant was removed from the opportunity, and a removal is final.";
        if (current == ApplicationStatus.Withdrawn)
            return "This applicant withdrew from the opportunity, and a withdrawal is final.";
        if (current == next)
            return next == ApplicationStatus.Selected
                ? "This application is already selected."
                : "This application is already turned down.";
        var hireTakenBack = MilestonePay.ByMilestone(kind) && opportunityStatus == OpportunityStatus.Awarded
            && current == ApplicationStatus.Selected && next == ApplicationStatus.NotSelected;
        if (opportunityStatus != OpportunityStatus.Open && !hireTakenBack)
            return MilestonePay.ByMilestone(kind) && opportunityStatus == OpportunityStatus.Awarded
                ? "Somebody is hired on this opportunity, so its other applications can no longer be decided."
                : "The opportunity is no longer open, so its applications can no longer be decided.";
        if (hireTakenBack && workArrived)
            return "They have already started work, so the hire can no longer be taken back. Talk it through with "
                + "them in Messages; you may cancel the opportunity while no milestone is waiting on you.";
        if (current == ApplicationStatus.Selected && workArrived)
            return "They have already started work, so the selection can no longer be taken back. "
                + "Use Remove from opportunity instead: it tells them why and leaves them a week to keep a copy.";
        return null;
    }

    /// <summary>
    /// An application nobody answered before its opportunity closed — the
    /// deadline came, or the client cancelled. Nothing more can happen to
    /// it, and every screen says so rather than promising a review.
    /// </summary>
    public static bool ClosedUndecided(ApplicationStatus status, OpportunityStatus opportunityStatus) =>
        status == ApplicationStatus.UnderReview && opportunityStatus is not (OpportunityStatus.Open or OpportunityStatus.Draft);

    /// <summary>
    /// Where a selection ended up, for the applicant's own page: still
    /// running, in review, won, lost to another entrant, or cancelled. Null
    /// for an application that is not selected — its status is the answer.
    /// </summary>
    public static string? Outcome(ApplicationStatus status, OpportunityStatus opportunityStatus, bool won, bool hired = false) =>
        status != ApplicationStatus.Selected ? null : opportunityStatus switch
        {
            OpportunityStatus.Reviewing => "reviewing",
            // Paid by milestone, the selection is the hire itself.
            OpportunityStatus.Awarded => hired ? "hired" : won ? "won" : "lost",
            OpportunityStatus.Cancelled => "cancelled",
            _ => "open",
        };

    public static string PortfolioJson(IEnumerable<PortfolioItem> items) => JsonSerializer.Serialize(items);

    public static IReadOnlyList<PortfolioItem> Portfolio(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<PortfolioItem>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
