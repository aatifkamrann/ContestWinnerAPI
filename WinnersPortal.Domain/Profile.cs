namespace WinnersPortal.Domain;

/// <summary>
/// How well somebody knows a skill they listed. Four rungs rather than the
/// original three: "working" was answering two questions at once — the
/// person who ships with it, and the person everyone else asks — and
/// telling those two apart is most of what a client reads a skill list for.
///
/// The numbers did not move when the rung was added. What was Learning is
/// Beginner, Working is Intermediate, Strong is Advanced; Expert is the new
/// one, and it is a claim a member makes rather than a promotion the portal
/// handed out to everybody who had said "strong" the day before.
/// </summary>
public enum SkillLevel
{
    /// <summary>Used it, still learning it.</summary>
    Beginner = 0,

    /// <summary>Ships production work with it.</summary>
    Intermediate = 1,

    /// <summary>Ships the hard parts of it, unsupervised.</summary>
    Advanced = 2,

    /// <summary>The person others ask.</summary>
    Expert = 3,
}

/// <summary>
/// How somebody works, in the one answer a client wants before they count
/// on a deadline: whether this is their whole week, part of it, a business
/// of one, or somebody with a team behind them. Self-reported like the rest
/// of the portfolio, and worth asking because "20 hours a week" means a
/// different thing from each of them.
/// </summary>
public enum WorkType
{
    /// <summary>Freelancing is the job. The week is the portal's to book.</summary>
    FullTimeFreelancer = 0,

    /// <summary>Freelancing around something else — a job, a degree, a business.</summary>
    PartTimeFreelancer = 1,

    /// <summary>A business of one: their own clients, their own terms, contracting here as well.</summary>
    IndependentProfessional = 2,

    /// <summary>One of a team. Somebody else may be doing the work beside them.</summary>
    AgencyMember = 3,
}

/// <summary>
/// Whether somebody can take a project on today, in the four answers a
/// client with a deadline actually wants. A status rather than a fact about
/// the person — it changes when the current job ends — which is why it is
/// its own question and not read off the hours a week: "40 hours a week"
/// and "not until the 20th" are both true of most busy people.
///
/// Null is nothing said, and stays nothing said: nobody is filed as
/// available now because they never got round to the question.
/// </summary>
public enum Availability
{
    /// <summary>Could start this week.</summary>
    Now = 0,

    /// <summary>Finishing something; free within a week.</summary>
    WithinOneWeek = 1,

    /// <summary>Free within a fortnight.</summary>
    WithinTwoWeeks = 2,

    /// <summary>Not taking anything on. A member, not a departure — the profile stays.</summary>
    Unavailable = 3,
}

/// <summary>
/// How well somebody speaks a language they listed. A separate scale from
/// <see cref="SkillLevel"/> on purpose: "strong at Urdu" says nothing, and a
/// client deciding whether a stand-up can happen in English is asking a
/// different question from a client deciding whether to trust a repository.
/// </summary>
public enum LanguageLevel
{
    /// <summary>A few phrases. Enough to be polite, not to take a brief in it.</summary>
    Beginner = 0,

    /// <summary>Can hold a conversation about the work, given patience.</summary>
    Conversational = 1,

    /// <summary>Works in it: meetings, written specs, code review.</summary>
    Professional = 2,

    /// <summary>No effort. An accent or the odd gap, and nothing slower for it.</summary>
    Fluent = 3,

    /// <summary>Native, or grew up with it alongside another.</summary>
    Native = 4,
}

/// <summary>
/// What a member says about themselves, beyond the name and email the
/// account needed. One row per user, created the first time they save
/// anything — an account without a profile is the normal starting state, not
/// a broken one.
///
/// Everything here is self-reported, which is exactly why the merit score
/// caps its contribution: a portfolio says what someone claims they can do,
/// and the portal's own record says what they actually did. Deliberately
/// professional-only. The portal has no use for a date of birth or a
/// government id, so it does not ask for them and cannot leak them.
///
/// One list here is not part of the portfolio at all: <see cref="Payments"/>
/// is how the member is paid, and it is read by a much shorter list of
/// people than everything above it.
/// </summary>
public sealed class Profile
{
    // Column limits: the one place a length is stated; the rules and the
    // DbContext both read it here.
    public const int MaxHeadline = 120;
    public const int MaxBio = 2000;
    public const int MaxLocation = 80;
    public const int MaxTimeZone = 64;
    public const int MaxUrl = 400;
    public const int MaxWorkingWindow = 40;

    /// <summary>Primary key and foreign key both: one profile per account.</summary>
    public Guid UserId { get; set; }
    public User? User { get; set; }

    /// <summary>One line under the name — "Django specialist, reporting and PDFs".</summary>
    public string? Headline { get; set; }

    /// <summary>
    /// The kind of work they mainly do — a key from the opportunity taxonomy, so
    /// that a member and a brief are described in one vocabulary and can be
    /// matched on it. Null until they say; never guessed from the skills.
    /// </summary>
    public string? PrimaryCategory { get; set; }

    /// <summary>
    /// Up to three more kinds of work, in the member's own order. A text[]
    /// column rather than a fifth child table: unlike a skill or a language
    /// a category carries nothing of its own, the keys come from a closed
    /// registry, and Postgres can be asked which members are in one without
    /// a join.
    /// </summary>
    public List<string> SecondaryCategories { get; set; } = [];

    /// <summary>How they work — see <see cref="WorkType"/>. Null until they say.</summary>
    public WorkType? PrimaryWorkType { get; set; }

    /// <summary>The longer introduction. Markdown is not rendered; this is plain text.</summary>
    public string? Bio { get; set; }

    /// <summary>City and country, as the person writes it. Never validated against a list.</summary>
    public string? Location { get; set; }

    /// <summary>IANA zone, so "available 20 hours a week" means something to a client elsewhere.</summary>
    public string? TimeZone { get; set; }

    public int? YearsExperience { get; set; }

    /// <summary>Hours a week they can commit — the number that decides whether a deadline is real.</summary>
    public int? HoursPerWeek { get; set; }

    // ---- when they can work --------------------------------------------
    // The hours above say how much of a week is on offer; these say whether
    // it is on offer now, for what length of work, on what terms, and at
    // which hours of the day. All four are preferences the member states
    // and nothing here gates anything: an opportunity never checks them, and a
    // member who prefers long-term work may enter a two-week one.

    /// <summary>Whether they can start — see <see cref="Domain.Availability"/>. Null until they say.</summary>
    public Availability? Availability { get; set; }

    /// <summary>
    /// The lengths of work they prefer, as keys from <c>ProjectDurations</c>
    /// — "short", "medium", "long". A text[] for the reason the secondary
    /// categories are: keys from a closed list, carrying nothing of their
    /// own. Empty is nothing said, not "none".
    /// </summary>
    public List<string> PreferredDurations { get; set; } = [];

    /// <summary>
    /// The terms they prefer to work on, as keys from <c>EngagementTypes</c>
    /// — "competitive", "direct", "hourly". The portal runs the first of
    /// those today; the other two are recorded so it knows who to ask when
    /// it runs them.
    /// </summary>
    public List<string> PreferredProjectTypes { get; set; } = [];

    /// <summary>
    /// The hours of the day they are usually at work, as they write it —
    /// "09:00 – 18:00", "evenings". Read beside <see cref="TimeZone"/>,
    /// which is what makes it mean something to a client elsewhere. Free
    /// text, never parsed: a working window is a courtesy, not a contract.
    /// </summary>
    public string? WorkingWindow { get; set; }

    public string? WebsiteUrl { get; set; }
    public string? LinkedInUrl { get; set; }

    public DateTimeOffset? UpdatedAtUtc { get; set; }

    // ---- deleted with the account ---------------------------------------
    // A profile is not deleted on its own — it goes when the account does,
    // and only the erasure of an account with a record leaves this row
    // behind to mark. An account nothing points at is removed outright and
    // takes its profile with it, so these three columns are never set on a
    // row anybody can reach.
    //
    // Marked rather than dropped so a deletion can be accounted for
    // afterwards — which administrator, on which day, and whether this was
    // the account somebody meant. Nothing reads a marked profile: the
    // global query filter in AppDbContext takes it out of every query the
    // portal makes, so the portfolio is gone from the portal's point of
    // view while the row is still there to answer a question about.

    public bool IsDeleted { get; set; }

    public DateTimeOffset? DeletedAtUtc { get; set; }

    /// <summary>
    /// The administrator who deleted the account. Null once that
    /// administrator's own account is removed outright — the audit loses a
    /// name rather than blocking a deletion.
    /// </summary>
    public Guid? DeletedByUserId { get; set; }

    public List<ProfileSkill> Skills { get; set; } = [];

    /// <summary>Languages they work in, each with how well. Was one comma-separated column.</summary>
    public List<ProfileLanguage> Languages { get; set; } = [];

    public List<ProfileProject> Projects { get; set; } = [];

    /// <summary>
    /// Every picture of past work this member has uploaded, pointed at by
    /// the projects that show them. A store rather than a child of a
    /// project — see <see cref="ProfileImage"/>.
    /// </summary>
    public List<ProfileImage> Images { get; set; } = [];

    /// <summary>Where an award goes once a client owes it. Never public.</summary>
    public List<ProfilePayment> Payments { get; set; } = [];
}

/// <summary>One claimed skill, with how long and how well.</summary>
public sealed class ProfileSkill
{
    // Column limits: the one place a length is stated; the rules and the
    // DbContext both read it here.
    public const int MaxName = 60;

    public Guid Id { get; set; }
    public Guid UserId { get; set; }

    /// <summary>Position in the list, 0-based — the member's own ordering.</summary>
    public int Order { get; set; }

    public required string Name { get; set; }

    public int Years { get; set; }

    public SkillLevel Level { get; set; } = SkillLevel.Intermediate;
}

/// <summary>
/// One language, with how well it is spoken. A row rather than an item in a
/// comma-separated string, for the same reason a skill is one: "English,
/// Urdu, German" tells a client nothing about which of the three a call
/// could happen in.
/// </summary>
public sealed class ProfileLanguage
{
    // Column limits: the one place a length is stated; the rules and the
    // DbContext both read it here.
    public const int MaxName = 60;

    public Guid Id { get; set; }
    public Guid UserId { get; set; }

    public int Order { get; set; }

    public required string Name { get; set; }

    public LanguageLevel Level { get; set; } = LanguageLevel.Professional;
}

/// <summary>
/// One piece of past work. Not verified by the portal and never presented as
/// if it were — the verified half of somebody's record is the opportunities they
/// entered here, which is a different list on the same page.
///
/// One field here decides who sees the rest of it:
/// <see cref="MayShowPublicly"/> is the member saying they are allowed to
/// show this work, and until they say it the row is theirs alone. Work under
/// an agreement that forbids showing it is still worth writing down — it is
/// what they have done — but the portal is the one publishing it, and it
/// does not publish it on an assumption.
/// </summary>
public sealed class ProfileProject
{
    // Column limits: the one place a length is stated; the rules and the
    // DbContext both read it here.
    public const int MaxTitle = 140;
    public const int MaxDescription = 1000;
    public const int MaxOutcome = 500;
    public const int MaxRole = 80;
    public const int MaxTech = 200;

    public Guid Id { get; set; }
    public Guid UserId { get; set; }

    public int Order { get; set; }

    public required string Title { get; set; }

    public string? Description { get; set; }

    /// <summary>
    /// What came of it — the result or the benefit, in the member's words:
    /// "month-end close cut from three days to one". Beside
    /// <see cref="Description"/> rather than inside it, because what the
    /// work was and what it did are two things a client reads for.
    /// </summary>
    public string? Outcome { get; set; }

    /// <summary>What they did on it — "sole developer", "backend of four".</summary>
    public string? Role { get; set; }

    /// <summary>
    /// Which kind of work it was — a key from the opportunity taxonomy, the same
    /// vocabulary as <see cref="Profile.PrimaryCategory"/> and as the brief a
    /// client writes. A member who says "mobile apps" here has said something
    /// an opportunity filed under "mobile apps" can be matched against.
    /// </summary>
    public string? Category { get; set; }

    /// <summary>What it was built with, comma-separated as typed.</summary>
    public string? Tech { get; set; }

    public string? Url { get; set; }
    public string? RepoUrl { get; set; }

    /// <summary>The year it shipped. Null for work with no obvious date.</summary>
    public int? Year { get; set; }

    /// <summary>
    /// The month it shipped, 1-12, beside <see cref="Year"/> rather than
    /// instead of it: the column that was there keeps what it held, so
    /// nobody's date was rewritten to add a month they never gave. Null
    /// where only a year was said, and meaningless without one.
    /// </summary>
    public int? Month { get; set; }

    /// <summary>
    /// The member has said they may show this work publicly. False is not a
    /// deletion and not a doubt — the row is kept and shown to its owner,
    /// and left out of what anybody else reads.
    /// </summary>
    public bool MayShowPublicly { get; set; }

    /// <summary>
    /// Screenshots, in the member's own order — ids of their own
    /// <see cref="ProfileImage"/> rows. A uuid[] rather than a join table for
    /// the reason a category list is a text[]: the ordering is the only thing
    /// the link carries, and the pictures outlive the project rows, which are
    /// rewritten on every save.
    /// </summary>
    public List<Guid> ImageIds { get; set; } = [];
}

/// <summary>
/// One way this member can be paid. The portal never moves money — a client
/// pays the winner directly and then marks the award paid — so this is the
/// answer to "where do I send it", written once instead of over email every
/// time.
///
/// Unlike everything else on a profile, this is not published: only the
/// member, the portal's administrators and a client who has announced the
/// member as a winner can read it (ProfileRules.MayReadPayments). The
/// fields themselves are encrypted at
/// rest with the same data-protection key that guards secret settings, so a
/// database backup on somebody's laptop is not a list of account numbers.
/// </summary>
public sealed class ProfilePayment
{
    // Column limits: the one place a length is stated; the rules and the
    // DbContext both read it here.
    public const int MaxLabel = 60;

    public Guid Id { get; set; }
    public Guid UserId { get; set; }

    public int Order { get; set; }

    /// <summary>A key from <c>PaymentMethods.All</c> — "bank", "wallet", "crypto".</summary>
    public required string Method { get; set; }

    /// <summary>The member's own name for this one — "Preferred — HBL". Optional.</summary>
    public string? Label { get; set; }

    /// <summary>
    /// The fields that method asked for, as a JSON object keyed by field,
    /// protected. Unreadable ciphertext is treated as a row that needs
    /// re-typing rather than as an error — the same choice the settings
    /// service makes for a secret it can no longer decrypt.
    /// </summary>
    public required string Details { get; set; }
}
