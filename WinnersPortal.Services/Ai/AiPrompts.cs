using WinnersPortal.Services.Opportunities;
using WinnersPortal.Services.Profiles;
using WinnersPortal.Domain;
using System.Text.Json;
using WinnersPortal.Services.Settings;

namespace WinnersPortal.Services.Ai;

/// <summary>
/// Every prompt the portal sends, in one place — so what leaves the server
/// is reviewable, and so each prompt can bake in the product's one law:
/// AI drafts, humans decide. No prompt asks for a score, a ranking, or a
/// verdict; each asks for a draft in a strict JSON shape the worker
/// validates before anyone sees it.
/// </summary>
public static class AiPrompts
{
    internal const string HouseRules =
        "You draft supporting material for a portal of coding opportunities. You never judge, score, rank, or " +
        "compare people or entries — a human client makes every decision. Answer with a single JSON " +
        "object exactly matching the requested shape: no markdown fences, no commentary, no extra keys.";

    /// <summary>
    /// The settings test's round trip: the smallest call that proves provider,
    /// key and model together. It names JSON because OpenAI's JSON mode, which
    /// every call is sent in, refuses a prompt that never says the word.
    /// </summary>
    public static (string System, string User) Ping => (
        "Answer with exactly the JSON object {\"ok\":true} and nothing else.",
        "Ready?");

    // ------------------------------------- the tools that read the form
    // Each takes the opportunity form as JSON — the sections above the one it
    // fills (AiFormReads) — and the same rule in every prompt: what the
    // client has set is a decision, to be used and never re-decided.

    /// <summary>The sentence every form prompt carries about the fields it is handed.</summary>
    internal const string FormRule =
        " The JSON is the opportunity form as the client has filled it so far; a field that is present is " +
        "their decision — build on it and never contradict or re-decide it — and a field that is absent " +
        "has not been filled in yet.";

    public static (string System, string User) Milestones(string formJson) => (
        HouseRules + " Task: turn a draft opportunity into a draft milestone checklist the client will edit " +
        "before publishing. 3 to 8 milestones, ordered, each independently verifiable — from inside a " +
        "repository where work is handed in as one, from the files handed in where it is an upload. Where " +
        "the requirements table names a language, a framework or a serving constraint, the milestones " +
        "honour it; where entries must run with Docker Compose, the first milestone is the one that runs. " +
        "Shape: {\"milestones\":[{\"title\":string,\"description\":string|null}]}. " +
        "Titles under 12 words; descriptions one sentence saying what finished looks like." + FormRule,
        "The opportunity form so far, as JSON:\n\n" + formJson);

    public static (string System, string User) Coach(string formJson) => (
        HouseRules + " Task: flag the things in a draft opportunity that cause disputes later — unstated " +
        "technology constraints, ambiguous deliverables, missing acceptance criteria, no definition of " +
        "done, and contradictions between the brief and the terms set beside it: a requirement the brief " +
        "never mentions, a milestone the brief does not ask for, a required skill the work does not need, " +
        "a hand-in shape the brief argues against. Advisory only; an empty list is a fine answer for a " +
        "good brief. Shape: {\"flags\":[{\"severity\":\"info\"|\"warn\",\"issue\":string,\"suggestion\":string}]}. " +
        "At most 8 flags, the most consequential first." + FormRule,
        "The opportunity form so far, as JSON:\n\n" + formJson);

    public static (string System, string User) Requirements(string formJson) => (
        HouseRules + " Task: draft the technical requirements table of a draft opportunity — the constraints " +
        "the work must meet, one row each: what the constraint is (Language, Frameworks, Data, Serving, " +
        "Hosting, Licence, Testing, Accessibility, and the like) and what it asks for. 3 to 8 rows, only " +
        "constraints the brief states or clearly implies; where the brief is silent, say nothing rather " +
        "than invent a constraint the client did not ask for. The required skills say what an entrant " +
        "must know, not what the work must meet, so a skill is not a row unless the brief makes it one. " +
        "Shape: {\"requirements\":[{\"title\":string,\"detail\":string}]}. title is the constraint in one " +
        "to three words; detail is one line, under 120 characters, in the brief's own terms." + FormRule,
        "The opportunity form so far, as JSON:\n\n" + formJson);

    public static (string System, string User) Criteria(string formJson) => (
        HouseRules + " Task: draft the scoring rubric a client will judge a draft opportunity's entries by — " +
        "what the work is scored on, how each line is judged, and how many of a hundred points it " +
        "carries, weighted by what the brief says matters most. This is the rubric the client publishes " +
        "and later judges by themselves; you are not judging anything. 3 to 6 criteria, whole-number " +
        "points that add up to exactly 100, each line judged from the work itself — the requirements, " +
        "the milestones and what the brief calls done — never from who the entrant is. " +
        "Shape: {\"criteria\":[{\"title\":string,\"points\":number,\"description\":string}]}. title in one " +
        "to four words; description one line, under 120 characters, saying how the line is judged." + FormRule,
        "The opportunity form so far, as JSON:\n\n" + formJson);

    public static (string System, string User) Digest(string inputJson) => (
        HouseRules + " Task: draft a reading aid for a client about to review one opportunity entry's " +
        "repository. Describe what the facts show was built, and what a careful reviewer should open " +
        "first. Never say whether the entry is good, better, or worth choosing. " +
        "Shape: {\"summary\":string,\"stack\":[string],\"milestoneCoverage\":string," +
        "\"quality\":string,\"reviewFocus\":[string]}. summary ≤ 3 sentences; quality states only " +
        "verifiable facts (tests present, docs present, CI present); reviewFocus is 2–4 concrete " +
        "starting points. selfReportedPortfolio is what the entrant wrote about themselves and is " +
        "not evidence about this repository: use it only to suggest where their claims could be " +
        "checked against the code, never to vouch for them.",
        "Facts about the entry, as JSON:\n\n" + inputJson);

    public static (string System, string User) Narrative(string inputJson) => (
        HouseRules + " Task: draft the day's plain-language note for an opportunity progress board, from " +
        "milestone claims and push activity. Factual and neutral — say what moved and what has gone " +
        "quiet, name entrants only with facts about their own activity, and never compare them. " +
        "Where a milestone carries dueUtc you may say a claim was on time or late and that an " +
        "unclaimed one is overdue; where it is null there is no schedule to read into. " +
        "Shape: {\"narrative\":string} — 2 to 4 sentences.",
        "The board's last day, as JSON:\n\n" + inputJson);

    public static (string System, string User) Standing(string inputJson) => (
        HouseRules + " Task: draft the client's reading notes for an opportunity progress board. The portal " +
        "has already computed every entrant's standing score, rank and band from the facts given, and " +
        "explained each part of it; you do not re-score, re-rank or re-order anyone. For each entrant, " +
        "say in 2–3 sentences what their own numbers show — what they have delivered on this opportunity, " +
        "where they are behind, and which fact the client should open before deciding whether they stay " +
        "in — using only the facts given. Name entrants only with facts about their own row. Do not " +
        "recommend keeping or removing anyone; that decision is the client's. " +
        "Shape: {\"summary\":string,\"entrants\":[{\"entryId\":string,\"note\":string,\"check\":[string]}]}. " +
        "summary ≤ 3 sentences on the board as a whole; entryId is copied exactly from the input; " +
        "check is 1–3 short, concrete things to open — a milestone, the last activity, the files or " +
        "the repository.",
        "The board, as JSON:\n\n" + inputJson);

    public static (string System, string User) Seo(string formJson) => (
        HouseRules + " Task: draft search metadata for a public opportunity page. " +
        "Shape: {\"title\":string,\"description\":string}. title ≤ 60 characters, plain and specific; " +
        "description ≤ 155 characters, states what is being built, the kind of work where the form " +
        "names one, and that it is a paid opportunity — no hype, no clickbait." + FormRule,
        "The opportunity form so far, as JSON:\n\n" + formJson);

    public static (string System, string User) Categorise(string taxonomyJson, string formJson) => (
        HouseRules + " Task: read a draft opportunity and say which kind of work it is, choosing from a closed " +
        "list of categories and their subcategories. Answer with keys copied exactly from the list — never " +
        "invent one, never answer with a label. Pick the subcategory only when the brief actually says " +
        "which; null is the right answer for a brief that does not. Prefer the deliverable over the " +
        "technology: a brief asking for a logo is design even when it mentions a website. Use \"other\" " +
        "only when nothing on the list fits. Shape: {\"category\":string,\"subcategory\":string|null," +
        "\"because\":string,\"confident\":boolean}. because is one short phrase — under 12 words — naming " +
        "what in the brief decided it, in the client's own words where you can; confident is false when the " +
        "brief is too thin to be sure, and the client is shown that." + FormRule,
        "The categories to choose from, as JSON:\n\n" + taxonomyJson
        + "\n\nThe opportunity form so far, as JSON:\n\n" + formJson);

    public static (string System, string User) WorkKinds(string taxonomyJson, string profileJson) => (
        HouseRules + " Task: read one freelancer's own profile and say which kinds of work they work in, " +
        "choosing from a closed list of categories. This is not a judgement of how good they are and not a " +
        "score: it decides only whether an opportunity is coloured as their usual kind of work, and they can " +
        "enter anything either way. Include every category their skills genuinely cover and leave out the " +
        "ones they do not — a long list helps nobody. Answer with keys copied exactly from the list. " +
        "Shape: {\"categories\":[string]}. An empty list is the honest answer for a profile with nothing " +
        "on it yet.",
        "The categories to choose from, as JSON:\n\n" + taxonomyJson
        + "\n\nThe freelancer's profile, as JSON:\n\n" + profileJson);

    public static (string System, string User) ProfileSummary(string profileJson) => (
        HouseRules + " Task: draft the About section of one member's profile on the portal, in the first person, " +
        "from the details they have typed on the form so far. A freelancer's reads like a freelancer introducing " +
        "themselves to a client who is about to read their code; a client's reads like a client introducing " +
        "themselves to the freelancers who will enter their opportunities. 2 to 4 sentences and under 600 characters, " +
        "plain and specific: name the kinds of work, the tools, the experience and the results the details actually show, and " +
        "nothing they do not — no invented clients, years, awards or qualities, no superlatives, no filler. Where " +
        "they already wrote an About, keep what is theirs and tighten it rather than replace it. Thin details get " +
        "a short draft, not a padded one. Shape: {\"summary\":string}.",
        "The member's profile so far, as JSON:\n\n" + profileJson);

    /// <summary>
    /// The box on a freelancer's own profile review. The improvements and
    /// their figures are the portal's arithmetic and travel already decided
    /// (ProfileReview.Candidates); the model is asked for the words — one
    /// sentence per line, answering to the line's id — and for what the
    /// profile is strongest for. It cannot add a line, drop one or touch a
    /// figure: the answer carries ids, and the figures are joined back on
    /// from the arithmetic when the page reads it.
    /// </summary>
    public static (string System, string User) ProfileReview(string json) => (
        HouseRules + " Task: write the words of the improvements box on one freelancer's own profile review. " +
        "The improvements are given, in order, each with an id, the change it names, the reason for it and " +
        "the figure it is worth — the portal's own arithmetic over its open opportunities. You do not choose, add, " +
        "reorder or drop them and you never write a figure: for each given id, write one imperative sentence " +
        "under 120 characters, in plain words, that says what to do and why, from the change and the reason " +
        "given and nothing else. Also say what the profile is strongest for: strongFor is two to five " +
        "lower-case words such as \"mobile development\", taken from the categories and skills given — never " +
        "invented. Shape: {\"strongFor\":string,\"improvements\":[{\"id\":string,\"text\":string}]}, ids copied " +
        "exactly. An empty improvements list in means an empty list out. Never quote a merit score, never " +
        "rank the member against anybody, never invent a client or an opportunity.",
        "The freelancer's profile and the improvements to put into words, as JSON:\n\n" + json);

    /// <summary>
    /// The approach step of an application: a draft of how this freelancer
    /// would do this opportunity's work, from the brief and their own profile.
    /// Written for the applicant to edit — never submitted for them.
    /// </summary>
    public static (string System, string User) ProjectApproach(string json) => (
        HouseRules + " Task: draft how one freelancer would approach an opportunity's work — the development phases, " +
        "the architecture decisions, the risk areas and the delivery strategy — from the brief, its required " +
        "skills, milestones and requirements, and the freelancer's own skills and relevant past work, all given. " +
        "In the first person, addressed to the client who posted the brief, in plain prose — short paragraphs or " +
        "a numbered plan, no headings — between 400 and 900 characters, specific to this brief: name the phases, " +
        "the tools the freelancer actually lists and the risks the brief implies, and nothing invented — no " +
        "clients, years, figures or results the details do not show. Where they have typed a draft, keep what is " +
        "theirs and tighten it rather than replace it. Shape: {\"approach\":string}.",
        "The opportunity and the freelancer, as JSON:\n\n" + json);

    /// <summary>
    /// The evaluation box on an application. The strengths and risks are
    /// the portal's arithmetic and travel already decided, each with an id
    /// and a label; the model is asked for a phrase per id about this
    /// application, and for one note. It cannot add a line, drop one,
    /// write a figure or say whether to select — the client decides.
    /// </summary>
    public static (string System, string User) ApplicationEvaluation(string json) => (
        HouseRules + " Task: write the words of the evaluation box on one application to compete in an opportunity, " +
        "read by the client who decides and by the applicant. The strengths and the risks are given, each with " +
        "an id and the portal's label, read off the portal's own arithmetic. You do not add, drop or reorder " +
        "them and you never write a figure: for each given id, write one phrase under 60 characters, specific to " +
        "this application — what the approach, the summary or the attached past work shows for that line — and " +
        "a note of one sentence under 200 characters saying what the client should open first. Shape: " +
        "{\"strengths\":[{\"id\":string,\"text\":string}],\"risks\":[{\"id\":string,\"text\":string}]," +
        "\"note\":string}, ids copied exactly; an empty list in means an empty list out. Never say whether " +
        "to select the applicant, never compare them with anybody, never quote a percentage or a score.",
        "The application and the lines to put into words, as JSON:\n\n" + json);

    /// <summary>
    /// The prompt for a tool that reads the opportunity form. One place, so the
    /// endpoint that serves all six stays a gate and a call and nothing
    /// else — and so a feature that has no form prompt cannot reach it.
    /// Each is handed the sections above the one it fills and no more
    /// (<see cref="AiFormReads"/>), serialised by <see cref="AiInputs.OpportunityForm"/>.
    /// </summary>
    public static (string System, string User) ForForm(AiFeature feature, OpportunityFormSnapshot form)
    {
        var json = AiInputs.OpportunityForm(form, AiFormReads.Of(feature));
        return feature switch
        {
            AiFeature.CategorySuggestion => Categorise(AiInputs.Taxonomy(), json),
            AiFeature.BriefCoach => Coach(json),
            AiFeature.RequirementsSuggestion => Requirements(json),
            AiFeature.MilestoneExtraction => Milestones(json),
            AiFeature.CriteriaSuggestion => Criteria(json),
            AiFeature.SeoMetadata => Seo(json),
            _ => throw new ArgumentOutOfRangeException(
                nameof(feature), feature, "That feature does not read the opportunity form."),
        };
    }
}

/// <summary>
/// Builders for the canonical input each feature is hashed and cached by.
/// For digests this is also the privacy boundary made testable: with
/// <c>sendCode</c> off, nothing from inside the repository — no paths, no
/// README text — appears in the input, only facts the portal derived locally.
/// </summary>
public static class AiInputs
{
    /// <summary>
    /// The closed list both readings choose from — keys and labels, nothing
    /// else. It is part of the hashed input on purpose: add a category and
    /// every cached reading is stale, which is exactly right, because the
    /// answer could have been the new one.
    /// </summary>
    public static string Taxonomy() => JsonSerializer.Serialize(
        Opportunities.OpportunityCategories.All.Select(c => new
        {
            key = c.Key,
            label = c.Label,
            subcategories = c.Subcategories.Select(s => new { key = s.Key, label = s.Label }),
        }));

    /// <summary>
    /// How much of the form there is to read before any form tool will
    /// spend a call on it: the title and the brief, which every tool reads
    /// and which the client writes first. A category picked over an empty
    /// brief is not something to draft from.
    /// </summary>
    public static int Substance(OpportunityFormSnapshot r) =>
        (AiRules.Clip(r.Title, 200) + AiRules.Clip(r.BriefMarkdown, AiRules.MaxBriefChars)).Trim().Length;

    /// <summary>
    /// The opportunity form as one tool receives it: the parts it reads
    /// (<see cref="AiFormReads"/>), cleaned the way a save would clean
    /// them and clipped, with labels rather than keys wherever the form
    /// holds a key — the model is being asked to write sentences. A part
    /// the tool reads but the client has not filled in is left out, so
    /// "absent" means exactly what the prompt says it means. The award
    /// and the dates never travel: no tool drafts money or a schedule, and
    /// the dates a milestone draft arrives with are the form's arithmetic.
    /// </summary>
    public static string OpportunityForm(OpportunityFormSnapshot r, FormPart parts)
    {
        var form = new Dictionary<string, object?>();
        void Put(FormPart part, string name, object? value)
        {
            if ((parts & part) != 0 && value is not null) form[name] = value;
        }

        Put(FormPart.Title, "title", NullIfBlank(AiRules.Clip(r.Title, 200)));
        Put(FormPart.Brief, "brief", NullIfBlank(AiRules.Clip(r.BriefMarkdown, AiRules.MaxBriefChars)));

        var category = Opportunities.OpportunityCategories.Find(Opportunities.OpportunityCategories.CleanKey(r.Category));
        Put(FormPart.Kind, "kindOfWork", category?.Label);
        Put(FormPart.Kind, "subcategory", category is null
            ? null
            : Opportunities.OpportunityCategories.FindSub(category, Opportunities.OpportunityCategories.CleanKey(r.Subcategory))?.Label);

        var skills = (r.Skills ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => AiRules.Clip(s, 60))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(Opportunities.OpportunityFit.MaxSkills)
            .ToList();
        Put(FormPart.Skills, "requiredSkills", skills.Count == 0 ? null : skills);

        var requirements = (r.Requirements ?? [])
            .Where(q => !string.IsNullOrWhiteSpace(q.Title) && !string.IsNullOrWhiteSpace(q.Detail))
            .Take(Opportunities.Rubric.MaxRequirements)
            .Select(q => new
            {
                title = AiRules.Clip(q.Title, Opportunities.Rubric.MaxTitle),
                detail = AiRules.Clip(q.Detail, Opportunities.Rubric.MaxDetail),
            })
            .ToList();
        Put(FormPart.Requirements, "requirements", requirements.Count == 0 ? null : requirements);

        var delivery = Opportunities.Delivery.Parse(r.Delivery);
        Put(FormPart.Delivery, "handedInAs", delivery switch
        {
            OpportunityDelivery.Upload => "files uploaded on the opportunity page",
            OpportunityDelivery.Both => "a GitHub repository per entrant, plus files uploaded alongside",
            OpportunityDelivery.Repository => "a GitHub repository per entrant",
            _ => null,
        });
        // The tick means nothing without a repository to build from — the
        // form clears it the same way.
        Put(FormPart.Delivery, "entriesMustRunWithDockerCompose",
            delivery is not null && Opportunities.Delivery.UsesRepository(delivery.Value) && r.RequiresCompose == true
                ? true
                : null);

        var milestones = (r.Milestones ?? [])
            .Where(m => !string.IsNullOrWhiteSpace(m.Title))
            .Take(20)
            .Select(m => new
            {
                title = AiRules.Clip(m.Title, 200),
                description = NullIfBlank(AiRules.Clip(m.Description, 300)),
            })
            .ToList();
        Put(FormPart.Milestones, "milestones", milestones.Count == 0 ? null : milestones);

        var criteria = (r.Criteria ?? [])
            .Where(c => !string.IsNullOrWhiteSpace(c.Title))
            .Take(Opportunities.Rubric.MaxCriteria)
            .Select(c => new
            {
                title = AiRules.Clip(c.Title, Opportunities.Rubric.MaxTitle),
                points = c.Points,
                description = NullIfBlank(AiRules.Clip(c.Description, Opportunities.Rubric.MaxDetail)),
            })
            .ToList();
        Put(FormPart.Criteria, "scoringCriteria", criteria.Count == 0 ? null : criteria);

        // Prose for a model to read, not a document for a browser: an em
        // dash travels as itself rather than as —.
        return JsonSerializer.Serialize(form, FormJson);
    }

    private static readonly JsonSerializerOptions FormJson = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// One freelancer as the Recommended reading sees them: what they say
    /// they do and what they build with. Not their merit score, not their
    /// record here, not their name — the question is which kinds of work
    /// these skills cover, and nothing else belongs in it.
    /// </summary>
    public static string WorkKinds(string? headline, IEnumerable<(string Name, string Level, int Years)> skills) =>
        JsonSerializer.Serialize(new
        {
            headline = NullIfBlank(AiRules.Clip(headline, 120)),
            skills = skills.Select(s => new { name = s.Name, level = s.Level, years = s.Years }),
        });

    /// <summary>
    /// The review's input: the profile in labels, the strength checklist's
    /// gaps, and the improvements already decided — id, change, reason and
    /// figure each, the first <see cref="Profiles.ProfileReview.MaxLines"/>
    /// of them. Not the name, never the email, and of the payout only that
    /// there is one. It is the hashed input too: an opportunity opening with a
    /// skill this profile lacks changes a figure, and a changed figure is a
    /// new answer.
    /// </summary>
    public static string ProfileReview(Profiles.ProfileReview.Facts f) =>
        JsonSerializer.Serialize(new
        {
            headline = NullIfBlank(AiRules.Clip(f.Headline, 120)),
            hasAbout = f.HasAbout,
            primaryCategory = f.PrimaryCategory,
            otherCategories = f.OtherCategories,
            skills = f.Skills.Take(30)
                .Select(s => new { name = AiRules.Clip(s.Name, 60), level = s.Level, years = s.Years }),
            projects = f.Projects,
            projectsWithLinks = f.ProjectsWithLinks,
            projectKinds = f.ProjectKinds,
            availabilitySet = f.HasAvailability,
            hoursPerWeek = f.HoursPerWeek,
            yearsExperience = f.YearsExperience,
            payoutSet = f.HasPayout,
            profileStrengthPercent = f.StrengthPercent,
            stepsNotDone = f.StepsNotDone,
            openOpportunities = new { total = f.OpenOpportunities, thisProfileCanEnter = f.OpenOpportunitiesEnterable },
            improvements = f.Candidates.Take(Profiles.ProfileReview.MaxLines)
                .Select(c => new { id = c.Id, change = c.Change, because = c.Because, gainPercent = c.Gain }),
        });

    /// <summary>
    /// The approach drafter's input: the brief and its terms, and what the
    /// freelancer lists that bears on it — their skills, and only the past
    /// work that fits this brief, so the draft cites what is relevant.
    /// </summary>
    public static string ProjectApproach(
        string title, string brief, string? categoryLabel, IReadOnlyList<string> skills,
        IReadOnlyList<string> milestones, IReadOnlyList<(string Title, string Detail)> requirements, int? weeks,
        string? headline, IReadOnlyList<(string Name, string Level, int Years)> profileSkills,
        IReadOnlyList<(string Title, string? Tech, string? Outcome)> relevantProjects, string? draft) =>
        JsonSerializer.Serialize(new
        {
            opportunity = new
            {
                title = AiRules.Clip(title, 200),
                brief = AiRules.Clip(brief, AiRules.MaxBriefChars),
                kindOfWork = categoryLabel,
                requiredSkills = skills.Take(15),
                milestones = milestones.Take(12).Select(m => AiRules.Clip(m, 120)),
                requirements = requirements.Take(12)
                    .Select(r => new { title = AiRules.Clip(r.Title, 80), detail = AiRules.Clip(r.Detail, 200) }),
                weeks,
            },
            freelancer = new
            {
                headline = NullIfBlank(AiRules.Clip(headline, 120)),
                skills = profileSkills.Take(30)
                    .Select(s => new { name = AiRules.Clip(s.Name, 60), level = s.Level.ToLowerInvariant(), years = s.Years }),
                relevantProjects = relevantProjects.Take(8).Select(p => new
                {
                    title = AiRules.Clip(p.Title, 120),
                    tech = NullIfBlank(AiRules.Clip(p.Tech, 200)),
                    outcome = NullIfBlank(AiRules.Clip(p.Outcome, 500)),
                }),
            },
            draft = NullIfBlank(AiRules.Clip(draft, Opportunities.ApplicationRules.MaxApproach)),
        });

    /// <summary>
    /// The evaluation's input, and its cache key: the lines to word, with
    /// the portal's figure and band beside them, and what the applicant
    /// wrote and attached. An unchanged application against unchanged
    /// lines costs nothing a second time.
    /// </summary>
    public static string ApplicationEvaluation(
        string opportunityTitle, string brief, string? categoryLabel, IReadOnlyList<string> skills,
        Opportunities.Evaluation evaluation, string summary, string approach,
        IReadOnlyList<Opportunities.PortfolioItem> portfolio, string commitment, int hoursPerWeek,
        IEnumerable<string> advantages) =>
        JsonSerializer.Serialize(new
        {
            opportunity = new
            {
                title = AiRules.Clip(opportunityTitle, 200),
                brief = AiRules.Clip(brief, 3000),
                kindOfWork = categoryLabel,
                requiredSkills = skills.Take(15),
            },
            evaluation = new
            {
                matchPercent = evaluation.Percent,
                band = evaluation.Band,
                strengths = evaluation.Strengths.Select(l => new { id = l.Id, label = l.Label }),
                risks = evaluation.Risks.Select(l => new { id = l.Id, label = l.Label }),
            },
            application = new
            {
                summary = NullIfBlank(AiRules.Clip(summary, 2000)),
                approach = AiRules.Clip(approach, Opportunities.ApplicationRules.MaxApproach),
                portfolio = portfolio.Take(Opportunities.ApplicationRules.MaxPortfolio).Select(p => new
                {
                    title = AiRules.Clip(p.Title, 120),
                    role = NullIfBlank(AiRules.Clip(p.Role, 120)),
                    tech = NullIfBlank(AiRules.Clip(p.Tech, 200)),
                    outcome = NullIfBlank(AiRules.Clip(p.Outcome, 500)),
                    relevancePercent = p.Relevance,
                }),
                commitment,
                hoursPerWeek,
                claimedAdvantages = advantages,
            },
        });

    /// <summary>
    /// The profile form as the summary drafter sees it: what the member
    /// says they do and have done, and which side of the market they are
    /// on, so a client's draft reads as a client's. Not their name — the
    /// draft is in the first person and needs none — and never the email
    /// or how they are paid, which the request shape cannot even carry.
    /// <c>Substance</c> is how much there is to read: below the floor the
    /// endpoint declines to spend a call on it.
    /// </summary>
    public static (string Json, int Substance) ProfileSummary(ProfileSummaryRequest r, string? role)
    {
        var headline = NullIfBlank(AiRules.Clip(r.Headline, 120));
        var about = NullIfBlank(AiRules.Clip(r.Bio, 2000));
        var location = NullIfBlank(AiRules.Clip(r.Location, 80));
        // Labels, not keys: the model is being asked to write a sentence,
        // and "devops" is not a phrase anybody would use in one. Unknown
        // keys fall out here the same way they do on a save.
        var (primaryKey, secondaryKeys) = Profiles.ProfileRules.CleanCategories(
            r.PrimaryCategory, r.SecondaryCategories);
        var primaryCategory = Opportunities.OpportunityCategories.Find(primaryKey)?.Label;
        var otherCategories = secondaryKeys
            .Select(k => Opportunities.OpportunityCategories.Find(k)?.Label)
            .Where(l => l is not null)
            .ToList();
        var workType = Profiles.ProfileRules.WorkTypeLabel(
            Profiles.ProfileRules.ParseWorkType(r.WorkType));
        var skills = (r.Skills ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s.Name))
            .Take(30)
            .Select(s => new
            {
                name = AiRules.Clip(s.Name, 60),
                level = NullIfBlank(AiRules.Clip(s.Level, 20)),
                years = s.Years,
            })
            .ToList();
        var languages = (r.Languages ?? [])
            .Where(l => !string.IsNullOrWhiteSpace(l.Name))
            .Take(20)
            .Select(l => new { name = AiRules.Clip(l.Name, 60), level = NullIfBlank(AiRules.Clip(l.Level, 20)) })
            .ToList();
        // The form sends only the work its owner has said they may show
        // publicly — the summary is a paragraph other people read, and work
        // under an agreement that forbids showing it must not reach a model
        // and come back out as prose. The label, not the key, for the same
        // reason as above.
        var projects = (r.Projects ?? [])
            .Where(p => !string.IsNullOrWhiteSpace(p.Title))
            .Take(20)
            .Select(p => new
            {
                title = AiRules.Clip(p.Title, 140),
                description = NullIfBlank(AiRules.Clip(p.Description, 1000)),
                outcome = NullIfBlank(AiRules.Clip(p.Outcome, 500)),
                role = NullIfBlank(AiRules.Clip(p.Role, 80)),
                kindOfWork = Opportunities.OpportunityCategories.Find(p.Category)?.Label,
                tech = NullIfBlank(AiRules.Clip(p.Tech, 200)),
                year = p.Year,
            })
            .ToList();

        // A kind of work is not counted: the form knows only the key and the
        // server only the label, and the two floors have to be the same one.
        var substance = (headline ?? "").Length + (about ?? "").Length
            + skills.Sum(s => s.name.Length)
            + languages.Sum(l => l.name.Length)
            + projects.Sum(p => p.title.Length + (p.description ?? "").Length + (p.outcome ?? "").Length);

        var json = JsonSerializer.Serialize(new
        {
            role = role ?? "freelancer",
            headline,
            about,
            primaryCategory,
            otherCategories,
            workType,
            location,
            yearsExperience = r.YearsExperience,
            hoursPerWeek = r.HoursPerWeek,
            skills,
            languages,
            projects,
        });
        return (json, substance);
    }

    private static string? NullIfBlank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;

    /// <summary>Repository structure this deep is a summary, not the work.</summary>
    public const int MaxTreePaths = 300;

    public const int MaxReadmeChars = 2000;

    public sealed record DigestFacts(
        string OpportunityTitle,
        string Brief,
        string EntrantNote,
        int PushCount,
        DateTimeOffset? LastPushAtUtc,
        IReadOnlyList<string> MilestonesAll,
        IReadOnlyList<string> MilestonesClaimed,
        IReadOnlyList<string> Languages,
        int? FileCount,
        bool? HasTests,
        bool? HasReadme,
        bool? HasCi,
        /// <summary>
        /// The entrant's own portfolio, as they wrote it. Self-reported, and
        /// labelled that way in the payload so the model is not invited to
        /// treat "10 years of Django" as a fact about the code in front of it.
        /// </summary>
        EntrantPortfolio? Portfolio = null);

    /// <summary>What the entrant claims, kept apart from what the portal observed.</summary>
    public sealed record EntrantPortfolio(
        string? Headline,
        string? Bio,
        IReadOnlyList<string> Skills,
        IReadOnlyList<string> Projects,
        int? YearsExperience,
        int? HoursPerWeek);

    public static string Digest(DigestFacts f, IReadOnlyList<string>? treePaths, string? readme, bool sendCode)
    {
        return JsonSerializer.Serialize(new
        {
            opportunityTitle = f.OpportunityTitle,
            brief = AiRules.Clip(f.Brief, 4000),
            entrantNote = AiRules.Clip(f.EntrantNote, 280),
            pushCount = f.PushCount,
            lastPushAtUtc = f.LastPushAtUtc,
            milestonesAll = f.MilestonesAll,
            milestonesClaimed = f.MilestonesClaimed,
            languages = f.Languages,
            // Under a name that says what it is. Everything else in this
            // payload was observed by the portal or read from the repository;
            // this is the one part the entrant wrote themselves.
            selfReportedPortfolio = f.Portfolio is null ? null : new
            {
                headline = AiRules.Clip(f.Portfolio.Headline, 120),
                about = AiRules.Clip(f.Portfolio.Bio, 1200),
                skills = f.Portfolio.Skills,
                pastWork = f.Portfolio.Projects,
                yearsExperience = f.Portfolio.YearsExperience,
                hoursPerWeek = f.Portfolio.HoursPerWeek,
            },
            fileCount = f.FileCount,
            hasTests = f.HasTests,
            hasReadme = f.HasReadme,
            hasCi = f.HasCi,
            // The switch, enforced at input-build time rather than by prompt
            // wording: off means repository contents never enter the payload.
            fileTree = sendCode && treePaths is not null
                ? treePaths.Take(MaxTreePaths).ToArray()
                : null,
            readme = sendCode && !string.IsNullOrWhiteSpace(readme)
                ? AiRules.Clip(readme, MaxReadmeChars)
                : null,
        });
    }

    public static string Narrative(
        string opportunityTitle,
        IReadOnlyList<NarrativeMilestone> milestones,
        IReadOnlyList<NarrativeEntrant> entrants,
        DateTimeOffset nowUtc)
    {
        return JsonSerializer.Serialize(new
        {
            opportunityTitle,
            dayUtc = AiQuotaRules.DayKey(nowUtc),
            // dueUtc is null on a milestone the client dated nothing by, which
            // is most of them — the note must not invent a schedule from that.
            milestones = milestones.Select(m => new { title = m.Title, dueUtc = m.DueUtc }),
            entrants = entrants.Select(e => new
            {
                name = e.Name,
                milestonesClaimed = e.MilestonesClaimed,
                claimedLast24h = e.ClaimedLast24h,
                // Already counted against the dates, so the model is
                // summarising arithmetic rather than doing any.
                claimedOnTime = e.OnTime,
                claimedLate = e.Late,
                overdue = e.Overdue,
                pushCount = e.PushCount,
                lastPushAtUtc = e.LastPushAtUtc,
            }),
        });
    }

    public sealed record NarrativeMilestone(string Title, DateTimeOffset? DueUtc);

    /// <summary>Beyond this the notes stop being readable anyway; the payload says how many were left out.</summary>
    public const int MaxStandingEntrants = 40;

    /// <summary>One row of the board as the standing job sends it — the arithmetic already done.</summary>
    public sealed record StandingEntrant(
        Guid EntryId,
        string Name,
        int Rank,
        int Of,
        int Score,
        string Band,
        DateTimeOffset EnteredAtUtc,
        DateTimeOffset? LastActivityUtc,
        IReadOnlyList<string> MilestoneStates,
        IReadOnlyList<StandingPart> Parts,
        string? Note);

    public sealed record StandingPart(string Label, int Earned, int Available, string Detail);

    /// <summary>
    /// The standing job's input: every row's computed score, band and
    /// explained parts, best first. Nothing from inside a repository — the
    /// facts the parts already state are the whole of what travels.
    /// </summary>
    public static string Standing(
        string opportunityTitle,
        string status,
        string delivery,
        DateTimeOffset? deadlineUtc,
        IReadOnlyList<NarrativeMilestone> milestones,
        IReadOnlyList<StandingEntrant> entrants,
        DateTimeOffset nowUtc)
    {
        return JsonSerializer.Serialize(new
        {
            opportunityTitle,
            status,
            delivery,
            deadlineUtc,
            dayUtc = AiQuotaRules.DayKey(nowUtc),
            milestones = milestones.Select(m => new { title = m.Title, dueUtc = m.DueUtc }),
            entrantsOmitted = Math.Max(0, entrants.Count - MaxStandingEntrants),
            entrants = entrants.OrderBy(e => e.Rank).Take(MaxStandingEntrants).Select(e => new
            {
                entryId = e.EntryId,
                name = e.Name,
                rank = e.Rank,
                of = e.Of,
                standingScore = e.Score,
                band = e.Band,
                enteredAtUtc = e.EnteredAtUtc,
                lastActivityUtc = e.LastActivityUtc,
                milestoneStates = e.MilestoneStates,
                // Already counted and explained, so the note summarises
                // arithmetic instead of attempting any.
                parts = e.Parts.Select(p => new
                {
                    label = p.Label, earned = p.Earned, available = p.Available, detail = p.Detail,
                }),
                entrantNote = AiRules.Clip(e.Note, 280),
            }),
        });
    }

    public sealed record NarrativeEntrant(
        string Name,
        IReadOnlyList<string> MilestonesClaimed,
        IReadOnlyList<string> ClaimedLast24h,
        int PushCount,
        DateTimeOffset? LastPushAtUtc,
        int OnTime = 0,
        int Late = 0,
        int Overdue = 0);

    /// <summary>
    /// What a file listing proves without any of it leaving the server:
    /// how much is there, and whether tests, docs, CI and a compose file
    /// exist at all. The compose file counts only at the root, under one
    /// of the four names Docker Compose looks for itself — the same file
    /// the build host builds on an opportunity that requires one.
    /// </summary>
    public static (int FileCount, bool HasTests, bool HasReadme, bool HasCi, bool HasCompose) TreeFacts(
        IReadOnlyList<string> paths)
    {
        var hasTests = false;
        var hasReadme = false;
        var hasCi = false;
        var hasCompose = false;
        foreach (var p in paths)
        {
            var lower = p.ToLowerInvariant();
            if (lower.Contains("test") || lower.Contains("spec")) hasTests = true;
            if (lower is "readme" || lower.StartsWith("readme.")) hasReadme = true;
            if (lower.StartsWith(".github/workflows/")) hasCi = true;
            if (IsComposeFile(lower)) hasCompose = true;
        }
        return (paths.Count, hasTests, hasReadme, hasCi, hasCompose);
    }

    /// <summary>The four root-level names Docker Compose reads without being told, lower-cased.</summary>
    public static bool IsComposeFile(string lowerPath) =>
        lowerPath is "compose.yaml" or "compose.yml" or "docker-compose.yaml" or "docker-compose.yml";
}

/// <summary>
/// The profile form as the summary drafter receives it. Its own shape
/// rather than <c>ProfileRequest</c> on purpose: the form's request
/// carries payment details, and a request that cannot carry them is how
/// they are kept out of the prompt — not a filter someone remembers.
/// </summary>
public sealed record ProfileSummaryRequest(
    string? DisplayName,
    string? Headline,
    string? Bio,
    /// <summary>Taxonomy keys, as the form holds them; the prompt gets the labels.</summary>
    string? PrimaryCategory,
    List<string>? SecondaryCategories,
    /// <summary>The word a work type travels as — see <c>ProfileRules.ParseWorkType</c>.</summary>
    string? WorkType,
    string? Location,
    int? YearsExperience,
    int? HoursPerWeek,
    List<SummarySkill>? Skills,
    List<SummaryLanguage>? Languages,
    List<SummaryProject>? Projects);

public sealed record SummarySkill(string? Name, int? Years, string? Level);

public sealed record SummaryLanguage(string? Name, string? Level);

public sealed record SummaryProject(
    string? Title, string? Description, string? Outcome, string? Role, string? Category, string? Tech, int? Year);

/// <summary>
/// The opportunity form as the form-reading tools receive it, saved or not —
/// the whole form on every request, so one shape serves all six tools
/// and the browser has one thing to send; which parts a tool actually
/// reads is <see cref="AiFormReads"/>, applied on this side. Keys as the
/// form holds them (category, delivery); the prompt gets labels. No
/// award and no dates: nothing here drafts money or a schedule.
/// </summary>
public sealed record OpportunityFormSnapshot(
    string? Title,
    string? BriefMarkdown,
    string? Category,
    string? Subcategory,
    List<string>? Skills,
    List<FormRequirement>? Requirements,
    /// <summary>The form's word for it — see <c>Delivery.Parse</c>.</summary>
    string? Delivery,
    bool? RequiresCompose,
    List<FormMilestone>? Milestones,
    List<FormCriterion>? Criteria);

public sealed record FormRequirement(string? Title, string? Detail);

public sealed record FormMilestone(string? Title, string? Description);

public sealed record FormCriterion(int? Points, string? Title, string? Description);
