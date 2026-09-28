using WinnersPortal.Services.Opportunities;
using WinnersPortal.Domain;

namespace WinnersPortal.Services.Profiles;

/// <summary>
/// What a profile may hold. Every limit here is a storage limit as well —
/// the columns are sized to these — and every one of them is checked before
/// a row is written rather than trusted from the form.
/// </summary>
public static class ProfileRules
{
    public const int MaxHeadline = Profile.MaxHeadline;
    public const int MaxBio = Profile.MaxBio;
    public const int MaxLocation = Profile.MaxLocation;
    public const int MaxTimeZone = Profile.MaxTimeZone;
    public const int MaxUrl = Profile.MaxUrl;

    /// <summary>
    /// One kind of work a member mainly does, and three more they also do.
    /// Four is a speciality; more is a directory listing, and a profile that
    /// claims every category claims none of them.
    /// </summary>
    public const int MaxSecondaryCategories = 3;

    public const int MaxSkills = 30;
    public const int MaxSkillName = ProfileSkill.MaxName;
    public const int MaxSkillYears = 60;

    /// <summary>Rows, not characters: languages were one comma-separated column until they carried a level.</summary>
    public const int MaxLanguages = 20;
    public const int MaxLanguageName = ProfileLanguage.MaxName;

    public const int MaxProjects = 20;
    public const int MaxProjectTitle = ProfileProject.MaxTitle;
    public const int MaxProjectDescription = ProfileProject.MaxDescription;

    /// <summary>A result in a sentence or two; the story of the project is the description's.</summary>
    public const int MaxProjectOutcome = ProfileProject.MaxOutcome;
    public const int MaxProjectRole = ProfileProject.MaxRole;
    public const int MaxProjectTech = ProfileProject.MaxTech;

    /// <summary>Enough for a bank account, a wallet, a card and a coin. More is a filing cabinet.</summary>
    public const int MaxPayments = 6;
    public const int MaxPaymentLabel = ProfilePayment.MaxLabel;

    /// <summary>Nobody has 80 years of professional experience in software.</summary>
    public const int MaxYearsExperience = 60;

    /// <summary>A week has 168 hours; claiming more than this is not a schedule.</summary>
    public const int MaxHoursPerWeek = 80;

    /// <summary>"09:00 – 18:00, Mon–Fri" with room to spare. Longer is a paragraph, not a window.</summary>
    public const int MaxWorkingWindow = Profile.MaxWorkingWindow;

    /// <summary>Older than the field. A typo, not a project.</summary>
    public const int MinProjectYear = 1970;

    /// <summary>Null and blank are the same answer here: nothing was said.</summary>
    public static string? Clean(string? value, int max)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        return trimmed.Length > max ? trimmed[..max] : trimmed;
    }

    /// <summary>
    /// A link the portal is willing to render. Only http and https: a
    /// javascript: or data: URL in a field that becomes an anchor on
    /// somebody else's screen is the whole reason this function exists.
    /// </summary>
    public static string? CleanUrl(string? value)
    {
        var trimmed = Clean(value, MaxUrl);
        if (trimmed is null) return null;
        // A bare domain is what people type; assume the safe scheme.
        if (!trimmed.Contains("://", StringComparison.Ordinal)) trimmed = "https://" + trimmed;
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        return uri.ToString().Length > MaxUrl ? null : uri.ToString();
    }

    public static int? CleanCount(int? value, int max) =>
        value is null || value < 0 ? null : Math.Min(value.Value, max);

    public static int? CleanYear(int? value, int thisYear) =>
        value is null || value < MinProjectYear || value > thisYear + 1 ? null : value;

    /// <summary>
    /// The month a project shipped, beside a year that survived
    /// <see cref="CleanYear"/>. A month without a year is not a date — the
    /// form cannot produce one, and a request that sends one is saying
    /// nothing — so it is dropped rather than stored as half an answer.
    /// </summary>
    public static int? CleanMonth(int? month, int? cleanedYear) =>
        cleanedYear is null || month is null or < 1 or > 12 ? null : month;

    /// <summary>
    /// The word a skill level travels as. Reads have always handed out the
    /// word rather than the number, so saves have to take it back: the enum
    /// has no string converter registered anywhere in this API, and a bare
    /// <see cref="SkillLevel"/> on the request would accept only 0, 1 or 2 —
    /// refusing the form's own vocabulary with an empty 400 before any of
    /// the rules below run.
    /// </summary>
    public const string SkillBeginnerName = "beginner";
    public const string IntermediateName = "intermediate";
    public const string AdvancedName = "advanced";
    public const string ExpertName = "expert";

    public static string LevelName(SkillLevel level) => level switch
    {
        SkillLevel.Beginner => SkillBeginnerName,
        SkillLevel.Advanced => AdvancedName,
        SkillLevel.Expert => ExpertName,
        _ => IntermediateName,
    };

    /// <summary>Anything unrecognised is Intermediate, which is what a new row starts on.</summary>
    public static SkillLevel ParseLevel(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        SkillBeginnerName => SkillLevel.Beginner,
        AdvancedName => SkillLevel.Advanced,
        ExpertName => SkillLevel.Expert,
        _ => SkillLevel.Intermediate,
    };

    /// <summary>
    /// The word a language level travels as — the same wire-vocabulary rule
    /// as a skill's, and for the same reason. The two scales share the word
    /// "beginner" and nothing else: they are different questions, read by
    /// different parsers, and neither is a rung on the other.
    /// </summary>
    public const string BeginnerName = "beginner";
    public const string ConversationalName = "conversational";
    public const string ProfessionalName = "professional";
    public const string FluentName = "fluent";
    public const string NativeName = "native";

    public static string LanguageLevelName(LanguageLevel level) => level switch
    {
        LanguageLevel.Beginner => BeginnerName,
        LanguageLevel.Conversational => ConversationalName,
        LanguageLevel.Fluent => FluentName,
        LanguageLevel.Native => NativeName,
        _ => ProfessionalName,
    };

    /// <summary>
    /// Anything unrecognised is Professional — what a new row starts on, and
    /// what the old comma-separated field meant by "languages you work in".
    /// </summary>
    public static LanguageLevel ParseLanguageLevel(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        BeginnerName => LanguageLevel.Beginner,
        ConversationalName => LanguageLevel.Conversational,
        FluentName => LanguageLevel.Fluent,
        NativeName => LanguageLevel.Native,
        _ => LanguageLevel.Professional,
    };

    // ------------------------------------------------------- how they work

    /// <summary>The word a work type travels as, on the same rule as the two levels above.</summary>
    public const string FullTimeName = "fulltime";
    public const string PartTimeName = "parttime";
    public const string IndependentName = "independent";
    public const string AgencyName = "agency";

    public static string? WorkTypeName(WorkType? type) => type switch
    {
        WorkType.FullTimeFreelancer => FullTimeName,
        WorkType.PartTimeFreelancer => PartTimeName,
        WorkType.IndependentProfessional => IndependentName,
        WorkType.AgencyMember => AgencyName,
        _ => null,
    };

    /// <summary>
    /// Null for anything unrecognised. Unlike a skill level, "not said" is a
    /// real answer here — most of the form is optional and this is no
    /// different, so there is no default to fall back to.
    /// </summary>
    public static WorkType? ParseWorkType(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        FullTimeName => WorkType.FullTimeFreelancer,
        PartTimeName => WorkType.PartTimeFreelancer,
        IndependentName => WorkType.IndependentProfessional,
        AgencyName => WorkType.AgencyMember,
        _ => null,
    };

    /// <summary>
    /// The words a person would use for it. The form has its own copy of
    /// these, as it does for the two levels; this one exists because the
    /// summary drafter reads the profile as prose, and "agency" on its own
    /// is not a sentence anybody would write.
    /// </summary>
    public static string? WorkTypeLabel(WorkType? type) => type switch
    {
        WorkType.FullTimeFreelancer => "Full-time freelancer",
        WorkType.PartTimeFreelancer => "Part-time freelancer",
        WorkType.IndependentProfessional => "Independent professional",
        WorkType.AgencyMember => "Agency / team member",
        _ => null,
    };

    // ---------------------------------------------- when they can work

    /// <summary>The word an availability travels as, on the same rule as the work type.</summary>
    public const string AvailableNowName = "now";
    public const string WithinOneWeekName = "week";
    public const string WithinTwoWeeksName = "twoweeks";
    public const string UnavailableName = "unavailable";

    public static string? AvailabilityName(Availability? availability) => availability switch
    {
        Availability.Now => AvailableNowName,
        Availability.WithinOneWeek => WithinOneWeekName,
        Availability.WithinTwoWeeks => WithinTwoWeeksName,
        Availability.Unavailable => UnavailableName,
        _ => null,
    };

    /// <summary>
    /// Null for anything unrecognised, as for the work type: "not said" is
    /// the real answer, and there is no rung to land somebody on — least of
    /// all "available now", which a client would act on.
    /// </summary>
    public static Availability? ParseAvailability(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        AvailableNowName => Availability.Now,
        WithinOneWeekName => Availability.WithinOneWeek,
        WithinTwoWeeksName => Availability.WithinTwoWeeks,
        UnavailableName => Availability.Unavailable,
        _ => null,
    };

    /// <summary>The words the form shows for it — the API's copy, for anything that reads a profile as prose.</summary>
    public static string? AvailabilityLabel(Availability? availability) => availability switch
    {
        Availability.Now => "Available now",
        Availability.WithinOneWeek => "Available within 1 week",
        Availability.WithinTwoWeeks => "Available within 2 weeks",
        Availability.Unavailable => "Currently unavailable",
        _ => null,
    };

    /// <summary>
    /// A set of keys from one of the closed preference lists, in the list's
    /// own order and with nothing said twice. Unknown keys are dropped
    /// rather than refused, as a category's are: a preference the portal
    /// stops offering must not make a profile unsaveable.
    /// </summary>
    public static List<string> CleanPreferences(IEnumerable<string?>? sent, IReadOnlyList<Preference> from)
    {
        var wanted = new HashSet<string>(
            (sent ?? []).Select(k => k?.Trim().ToLowerInvariant()).Where(k => !string.IsNullOrEmpty(k))!,
            StringComparer.Ordinal);
        return from.Where(p => wanted.Contains(p.Key)).Select(p => p.Key).ToList();
    }

    // -------------------------------------------------- kinds of work done

    /// <summary>
    /// One key as the taxonomy spells it, or null for anything it does not
    /// know — which is how a retired category leaves a profile rather than
    /// making it unsaveable. Used for a member's own kinds of work and for
    /// the kind each piece of past work was.
    /// </summary>
    public static string? CleanCategory(string? key) => OpportunityCategories.Find(key)?.Key;

    /// <summary>
    /// The kinds of work a member says they do: one primary, and up to three
    /// more that are not it and not each other. Every key is checked against
    /// the opportunity taxonomy — the same list a brief is filed under, which is
    /// the whole point of asking — and anything else is dropped rather than
    /// refused: a category retired from the registry must not be able to
    /// make somebody's profile unsaveable.
    /// </summary>
    public static (string? Primary, List<string> Secondary) CleanCategories(
        string? primary, IEnumerable<string?>? secondary)
    {
        var first = CleanCategory(primary);
        var rest = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (first is not null) seen.Add(first);
        foreach (var key in secondary ?? [])
        {
            if (rest.Count == MaxSecondaryCategories) break;
            if (CleanCategory(key) is not { } found) continue;
            if (seen.Add(found)) rest.Add(found);
        }
        return (first, rest);
    }

    // ------------------------------------------------------ payment privacy

    /// <summary>
    /// Who may read somebody's payment details. Everything else on a profile
    /// is written to be read by other members; this is written for the one
    /// person who owes money, so it goes to the member, to the portal's
    /// administrators, and to a client who has announced this member as the
    /// winner of one of their opportunities — never to another client, who owes
    /// them nothing, and never to another freelancer.
    ///
    /// A relationship and not a role: being a client is not a reason to hold
    /// a stranger's account number. The award is, and it stays one after the
    /// award is paid, because a payment that bounced or went astray is sorted
    /// out against what was on the page.
    /// </summary>
    public static bool MayReadPayments(bool own, string? viewerRole, bool awardedByViewer) =>
        own || viewerRole == Roles.Admin || (viewerRole == Roles.Client && awardedByViewer);

    /// <summary>
    /// Whose profile has payment details at all. Only a freelancer is ever
    /// owed an award, so a client's or an administrator's page carries no
    /// "How to pay" section, not even an empty one, and a list sent for one
    /// is not stored.
    /// </summary>
    public static bool HasPayments(string? accountRole) => accountRole == Roles.Freelancer;

    /// <summary>
    /// What a client who has not awarded this member reads where the details
    /// would be: that they are coming, not whether there are any.
    /// </summary>
    public static string PaymentsAfterAwardNote(string displayName) =>
        $"Shown to you once you announce {displayName} as the winner of one of your opportunities.";

    // ----------------------------------------------------------- duplicates

    /// <summary>
    /// The first thing in a list that was already in it, as the member typed
    /// it the second time — or null when every entry is its own. Case and
    /// spacing are set aside, because "React Native", "react native" and
    /// "React  Native" are one skill and listing them all is a mistake
    /// rather than a claim.
    /// </summary>
    public static string? FirstDuplicate(IEnumerable<string?> values)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            var folded = Fold(value);
            if (folded.Length == 0) continue;
            if (!seen.Add(folded)) return value!.Trim();
        }
        return null;
    }

    /// <summary>
    /// Trimmed, inner runs of whitespace collapsed to one space — the form two
    /// names are compared in (case-insensitively). Public because an opportunity's
    /// required skills are folded the same way before they meet profile skills.
    /// </summary>
    public static string Fold(string? value) =>
        string.Join(' ', (value ?? string.Empty).Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    /// <summary>
    /// One comma-separated field with nothing said twice — a project's
    /// "built with", where the repeat is inside a single text box and there
    /// is no row to point the member at. Kept in the order and spelling they
    /// typed, minus the second mention.
    /// </summary>
    public static string? DedupeList(string? value, int max)
    {
        var cleaned = Clean(value, max);
        if (cleaned is null) return null;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<string>();
        foreach (var part in cleaned.Split(','))
        {
            var folded = Fold(part);
            if (folded.Length == 0) continue;
            if (seen.Add(folded)) kept.Add(folded);
        }
        return kept.Count == 0 ? null : Clean(string.Join(", ", kept), max);
    }

    /// <summary>What the whole submission gets refused for, or null if it is fine.</summary>
    public static string? Problem(
        IReadOnlyList<string?> skillNames,
        IReadOnlyList<string?> languageNames,
        IReadOnlyList<string?> projectTitles)
    {
        if (skillNames.Count > MaxSkills)
            return $"At most {MaxSkills} skills — pick the ones you would be hired for.";
        if (languageNames.Count > MaxLanguages) return $"At most {MaxLanguages} languages.";
        if (projectTitles.Count > MaxProjects) return $"At most {MaxProjects} projects.";

        if (skillNames.Any(string.IsNullOrWhiteSpace)) return "Every skill needs a name.";
        if (languageNames.Any(string.IsNullOrWhiteSpace)) return "Every language needs a name.";
        if (projectTitles.Any(string.IsNullOrWhiteSpace)) return "Every project needs a title.";

        // Nothing on a profile is worth saying twice. Said twice, it reads
        // as two different claims that happen to collide — and a client
        // scanning "React · strong · 6y" beside "react · learning · 1y" has
        // no way to know which one to believe.
        if (FirstDuplicate(skillNames) is { } skill)
            return $"“{skill}” is in your skills twice — one row each.";
        if (FirstDuplicate(languageNames) is { } language)
            return $"“{language}” is in your languages twice — one row each.";
        if (FirstDuplicate(projectTitles) is { } project)
            return $"“{project}” is in your past work twice — give them different titles.";
        return null;
    }

    /// <summary>The most a suggestion list carries. Past this it is a catalogue, not a prompt.</summary>
    public const int MaxSuggestions = 200;

    /// <summary>
    /// What other members already wrote in a free-text field, folded into
    /// one list the form can offer back. Case is the fold: "postgresql",
    /// "PostgreSQL" and "Postgresql" are one skill, and the spelling most
    /// people chose is the one suggested. Most-used first, so the field's
    /// dropdown leads with what the portal actually calls things.
    /// </summary>
    /// <param name="split">
    /// True for the comma-separated fields — a project's tech — where each
    /// item between commas is a suggestion of its own.
    /// </param>
    public static IReadOnlyList<string> Suggest(IEnumerable<string?> values, bool split, int max = MaxSuggestions)
    {
        var seen = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in values)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var items = split ? raw.Split(',') : [raw];
            foreach (var item in items)
            {
                var text = item.Trim();
                if (text.Length == 0) continue;
                if (!seen.TryGetValue(text, out var spellings))
                    seen[text] = spellings = new Dictionary<string, int>(StringComparer.Ordinal);
                spellings[text] = spellings.GetValueOrDefault(text) + 1;
            }
        }

        return seen.Values
            .Select(spellings => new
            {
                Count = spellings.Values.Sum(),
                // The commonest spelling; on a tie, the first one written.
                Text = spellings.MaxBy(kv => kv.Value).Key,
            })
            .OrderByDescending(s => s.Count)
            .ThenBy(s => s.Text, StringComparer.OrdinalIgnoreCase)
            .Select(s => s.Text)
            .Take(max)
            .ToList();
    }
}
