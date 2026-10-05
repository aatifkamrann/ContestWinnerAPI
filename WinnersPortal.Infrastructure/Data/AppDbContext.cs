using Microsoft.EntityFrameworkCore;
using NpgsqlTypes;
using WinnersPortal.Domain;

namespace WinnersPortal.Infrastructure.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options, SlowQueryInterceptor? slow = null) : DbContext(options)
{
    private Sql? _sql;

    public DbSet<Setting> Settings => Set<Setting>();
    public DbSet<SettingAudit> SettingAudits => Set<SettingAudit>();
    public DbSet<SetupTest> SetupTests => Set<SetupTest>();
    public DbSet<ActivityEvent> ActivityEvents => Set<ActivityEvent>();
    public DbSet<User> Users => Set<User>();
    public DbSet<UserAvatar> UserAvatars => Set<UserAvatar>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<ProfileImage> ProfileImages => Set<ProfileImage>();
    public DbSet<Opportunity> Opportunities => Set<Opportunity>();
    public DbSet<Milestone> Milestones => Set<Milestone>();
    public DbSet<OpportunitySkill> OpportunitySkills => Set<OpportunitySkill>();
    public DbSet<OpportunityRequirement> OpportunityRequirements => Set<OpportunityRequirement>();
    public DbSet<OpportunityCriterion> OpportunityCriteria => Set<OpportunityCriterion>();
    public DbSet<Entry> Entries => Set<Entry>();
    public DbSet<Application> Applications => Set<Application>();
    public DbSet<Checkpoint> Checkpoints => Set<Checkpoint>();
    public DbSet<Preview> Previews => Set<Preview>();
    public DbSet<WebhookDelivery> WebhookDeliveries => Set<WebhookDelivery>();
    public DbSet<IdentityVerification> IdentityVerifications => Set<IdentityVerification>();
    public DbSet<IdentityDocument> IdentityDocuments => Set<IdentityDocument>();
    public DbSet<Award> Awards => Set<Award>();
    public DbSet<Rating> Ratings => Set<Rating>();
    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<Submission> Submissions => Set<Submission>();
    public DbSet<EmailMessage> EmailMessages => Set<EmailMessage>();
    public DbSet<AiArtifact> AiArtifacts => Set<AiArtifact>();
    public DbSet<AiUsage> AiUsages => Set<AiUsage>();
    public DbSet<AiSpend> AiSpends => Set<AiSpend>();
    public DbSet<PushDevice> PushDevices => Set<PushDevice>();
    public DbSet<PushMessage> PushMessages => Set<PushMessage>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();
    public DbSet<ChatReport> ChatReports => Set<ChatReport>();
    public DbSet<Profile> Profiles => Set<Profile>();
    public DbSet<ProfileSkill> ProfileSkills => Set<ProfileSkill>();
    public DbSet<ProfileLanguage> ProfileLanguages => Set<ProfileLanguage>();
    public DbSet<ProfileProject> ProfileProjects => Set<ProfileProject>();
    public DbSet<ProfilePayment> ProfilePayments => Set<ProfilePayment>();
    public DbSet<MeritSnapshot> MeritSnapshots => Set<MeritSnapshot>();

    /// <summary>Which database this context was built for; the services branch on it where a dialect differs.</summary>
    public DatabaseProvider Provider => Database.IsSqlServer() ? DatabaseProvider.SqlServer : DatabaseProvider.Postgres;

    public bool IsSqlServer => Provider == DatabaseProvider.SqlServer;

    /// <summary>
    /// Whether the day-to-day reads go through <see cref="Sql"/>: on SQL
    /// Server, unless the options turned Dapper off for a diagnostic run;
    /// never on Postgres, which runs the LINQ beside every T-SQL statement.
    /// A reader with both branches dispatches on this and nothing else.
    /// </summary>
    public bool UseDapper => IsSqlServer && AppDbContextOptions.DapperEnabled(options);

    /// <summary>Dapper on this context's connection and transaction; see <see cref="Data.Sql"/>.</summary>
    public Sql Sql => _sql ??= new Sql(this, slow);

    // The eleven lines where the two dialects part: the Postgres branch is
    // the model as it always was, the SQL Server branch its equivalent.
    // Arrays and JSON documents are nvarchar(max) holding their JSON on
    // SQL Server — not the json type 2025 introduced, so that 2022 runs
    // the portal too; nothing reads inside them in SQL, and EF Core and
    // the Dapper readers serialise them the same either way. The tsvector
    // has no counterpart and its work is done by a full-text index the SQL
    // Server migration creates by hand.
    private static string Json(bool sql) => sql ? "nvarchar(max)" : "jsonb";
    private static string Array(bool sql, string postgres) => sql ? "nvarchar(max)" : postgres;
    private static string Filter(bool sql, string column, string rest) => sql ? $"[{column}] {rest}" : $"\"{column}\" {rest}";

    protected override void OnModelCreating(ModelBuilder b)
    {
        var sql = Database.IsSqlServer();

        b.Entity<Setting>(e =>
        {
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(128);
        });

        b.Entity<SettingAudit>(e =>
        {
            e.Property(x => x.Key).HasMaxLength(128);
            e.HasIndex(x => new { x.Key, x.ChangedAtUtc });
        });

        b.Entity<SetupTest>(e =>
        {
            e.HasKey(x => new { x.Kind, x.SetupId });
            e.Property(x => x.Kind).HasMaxLength(16);
            e.Property(x => x.SetupId).HasMaxLength(16);
            e.Property(x => x.Detail).HasMaxLength(SetupTest.MaxDetail);
            e.Property(x => x.Fingerprint).HasMaxLength(512);
            e.Property(x => x.TestedBy).HasMaxLength(320);
        });

        b.Entity<ActivityEvent>(e =>
        {
            e.Property(x => x.Visitor).HasMaxLength(16);
            e.Property(x => x.Kind).HasMaxLength(8);
            e.Property(x => x.Method).HasMaxLength(8);
            e.Property(x => x.Path).HasMaxLength(ActivityEvent.MaxPath);
            e.Property(x => x.Action).HasMaxLength(160);
            e.Property(x => x.Page).HasMaxLength(ActivityEvent.MaxPath);
            e.Property(x => x.Subject).HasMaxLength(ActivityEvent.MaxSubject);
            e.Property(x => x.Detail).HasMaxLength(ActivityEvent.MaxDetail);
            e.Property(x => x.Ip).HasMaxLength(45); // an IPv6 address at its longest
            e.Property(x => x.UserAgent).HasMaxLength(ActivityEvent.MaxAgent);
            e.Property(x => x.Service).HasMaxLength(ActivityEvent.MaxService);
            // No relationship to User on purpose (see the entity): the rows
            // outlive an account removed outright. The screen reads newest
            // first, for everybody, for one account, or for one browser
            // session; the retention sweep reads by age.
            e.HasIndex(x => x.AtUtc);
            e.HasIndex(x => new { x.UserId, x.AtUtc });
            e.HasIndex(x => new { x.Visitor, x.AtUtc });
        });

        b.Entity<User>(e =>
        {
            e.Property(x => x.Email).HasMaxLength(320);
            e.HasIndex(x => x.Email).IsUnique();
            e.Property(x => x.Role).HasMaxLength(16);
            e.Property(x => x.GithubLogin).HasMaxLength(39);
            e.Property(x => x.Phone).HasMaxLength(User.MaxPhoneLength);
            e.Property(x => x.EmailCodeHash).HasMaxLength(64); // SHA-256 as hex
            e.Property(x => x.PhoneCodeHash).HasMaxLength(64);
            e.Property(x => x.ThemeAccent).HasMaxLength(7); // #RRGGBB
            e.Property(x => x.ThemeStatusColours).HasMaxLength(160); // five "status":"#RRGGBB" pairs at most
            // The reset endpoint's one lookup: presented token, hashed → row.
            e.Property(x => x.PasswordResetTokenHash).HasMaxLength(64); // SHA-256 as hex
            e.HasIndex(x => x.PasswordResetTokenHash);
            // On for accounts that predate the preference: they have opted
            // into no topic yet, and the topics are what gate the mail.
            e.Property(x => x.NotifyByEmail).HasDefaultValue(true);
            e.Property(x => x.LockReason).HasMaxLength(User.MaxLockReason);
            // Rows that predate the column each get their own stamp, so no
            // two accounts ever share one.
            e.Property(x => x.SessionStamp).HasDefaultValueSql(sql ? "NEWID()" : "gen_random_uuid()");
        });

        b.Entity<UserAvatar>(e =>
        {
            // The account id is the key: one picture per account, gone with
            // the account. Read by the one endpoint that serves it and by
            // nothing that reads a User.
            e.HasKey(x => x.UserId);
            e.HasOne<User>().WithOne()
                .HasForeignKey<UserAvatar>(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.ContentType).HasMaxLength(UserAvatar.MaxContentTypeLength);
        });

        b.Entity<RefreshToken>(e =>
        {
            e.Property(x => x.TokenHash).HasMaxLength(64); // SHA-256 as hex
            // The refresh endpoint's one lookup: presented token, hashed → row.
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasOne(x => x.User).WithMany()
                .HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            // A reuse ends every token the account holds; a sweep reads by age.
            e.HasIndex(x => x.UserId);
        });

        b.Entity<Profile>(e =>
        {
            // The account id is the key: one profile per user, and no way to
            // end up with two.
            e.HasKey(x => x.UserId);
            e.HasOne(x => x.User).WithOne()
                .HasForeignKey<Profile>(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Headline).HasMaxLength(Profile.MaxHeadline);
            e.Property(x => x.PrimaryCategory).HasMaxLength(Opportunity.MaxCategoryKey);
            // Up to three keys from the same closed registry, in one text[]
            // column rather than a fifth child table: a category carries
            // nothing of its own, so the table would be a key, an order and
            // no other reason to exist.
            e.Property(x => x.SecondaryCategories).HasColumnType(Array(sql, "text[]"));
            e.Property(x => x.Bio).HasMaxLength(Profile.MaxBio);
            e.Property(x => x.Location).HasMaxLength(Profile.MaxLocation);
            e.Property(x => x.TimeZone).HasMaxLength(Profile.MaxTimeZone);
            // Two more closed-list sets, stored the way the categories are.
            e.Property(x => x.PreferredDurations).HasColumnType(Array(sql, "text[]"));
            e.Property(x => x.PreferredProjectTypes).HasColumnType(Array(sql, "text[]"));
            e.Property(x => x.WorkingWindow).HasMaxLength(Profile.MaxWorkingWindow);
            e.Property(x => x.WebsiteUrl).HasMaxLength(Profile.MaxUrl);
            e.Property(x => x.LinkedInUrl).HasMaxLength(Profile.MaxUrl);
            // Who erased the account this profile belonged to. Nulled rather
            // than blocking: an administrator's own account may later be
            // removed outright, and a foreign key that refuses that would
            // make one deletion depend on another person's history.
            // SQL Server refuses a second cascading path from Users to this
            // table (the profile's own key already cascades), so there the
            // nulling is EF's, done at the removal site before the delete.
            e.HasOne<User>().WithMany()
                .HasForeignKey(x => x.DeletedByUserId).OnDelete(sql ? DeleteBehavior.ClientSetNull : DeleteBehavior.SetNull);
            // The portfolio of a deleted account is out of every query the
            // portal makes, from here rather than at each read site: the
            // profile is read by the profile page, the entrant list, the
            // standing board and the merit score, and "remember to filter"
            // is how the deleted stay visible. IgnoreQueryFilters() is the
            // one way back in, and only the delete endpoint uses it.
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        b.Entity<ProfileSkill>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(ProfileSkill.MaxName);
            e.HasOne<Profile>().WithMany(p => p.Skills)
                .HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.UserId, x.Order });
        });

        b.Entity<ProfileLanguage>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(ProfileLanguage.MaxName);
            e.HasOne<Profile>().WithMany(p => p.Languages)
                .HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.UserId, x.Order });
        });

        b.Entity<ProfileProject>(e =>
        {
            e.Property(x => x.Title).HasMaxLength(ProfileProject.MaxTitle);
            e.Property(x => x.Description).HasMaxLength(ProfileProject.MaxDescription);
            e.Property(x => x.Outcome).HasMaxLength(ProfileProject.MaxOutcome);
            e.Property(x => x.Role).HasMaxLength(ProfileProject.MaxRole);
            e.Property(x => x.Category).HasMaxLength(Opportunity.MaxCategoryKey);
            e.Property(x => x.Tech).HasMaxLength(ProfileProject.MaxTech);
            e.Property(x => x.Url).HasMaxLength(Profile.MaxUrl);
            e.Property(x => x.RepoUrl).HasMaxLength(Profile.MaxUrl);
            // The ids of this member's own pictures, in the order they show.
            // A uuid[] rather than a join table for the reason the kinds of
            // work above are a text[]: the link carries nothing but its
            // position, and these ids outlive the project rows, which are
            // rewritten from the form on every save.
            e.Property(x => x.ImageIds).HasColumnType(Array(sql, "uuid[]"));
            // Work already on the portal was already being published, so the
            // column starts true for every row that predates it; the form
            // starts a new project unticked, and says so.
            e.Property(x => x.MayShowPublicly).HasDefaultValue(true);
            e.HasOne<Profile>().WithMany(p => p.Projects)
                .HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.UserId, x.Order });
        });

        b.Entity<ProfileImage>(e =>
        {
            // Hung off the profile like every other list a member writes, so
            // it goes when the portfolio does; the erasure of an account
            // that leaves a record behind deletes these outright, the way it
            // deletes the picture of their face.
            e.HasOne<Profile>().WithMany(p => p.Images)
                .HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.ContentType).HasMaxLength(ProfileImage.MaxContentTypeLength);
            // The one query that is not by id: everything this member owns,
            // read once per save to decide what is kept and what goes.
            e.HasIndex(x => x.UserId);
        });

        b.Entity<ProfilePayment>(e =>
        {
            e.Property(x => x.Method).HasMaxLength(40);
            e.Property(x => x.Label).HasMaxLength(ProfilePayment.MaxLabel);
            // Ciphertext, so no length worth declaring: the plaintext is
            // capped field by field in PaymentMethods, and what lands here
            // is base64 several times that.
            e.HasOne<Profile>().WithMany(p => p.Payments)
                .HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.UserId, x.Order });
        });

        b.Entity<Opportunity>(e =>
        {
            e.Property(x => x.Slug).HasMaxLength(140);
            e.HasIndex(x => x.Slug).IsUnique();
            e.Property(x => x.Title).HasMaxLength(140);
            e.Property(x => x.AwardAmount).HasPrecision(12, 2);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.MetaTitle).HasMaxLength(80);
            e.Property(x => x.MetaDescription).HasMaxLength(200);
            e.Property(x => x.Category).HasMaxLength(Opportunity.MaxCategoryKey);
            e.Property(x => x.Subcategory).HasMaxLength(Opportunity.MaxCategoryKey);
            e.HasOne(x => x.Client).WithMany().HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Restrict);
            // The feed pages by keyset on (PublishedAtUtc, Id) and filters by status.
            e.HasIndex(x => new { x.Status, x.PublishedAtUtc, x.Id });
            // …and, since the browse page grew a category filter, by that too.
            e.HasIndex(x => new { x.Category, x.Status, x.PublishedAtUtc });
            // Phase-one search is tsvector + GIN, straight from the blueprint —
            // no extra container until Meilisearch earns its place.
            // A shadow property: the entity has no member for it (Domain/Opportunity.cs).
            // SQL Server has no such column; its full-text index over the
            // same two columns is created by the ContestFullText migration.
            if (!sql)
            {
                e.Property<NpgsqlTsVector>("SearchVector")
                    .IsRequired() // generated, so never null; the column has always been NOT NULL
                    .IsGeneratedTsVectorColumn("english", nameof(Opportunity.Title), nameof(Opportunity.BriefMarkdown));
                e.HasIndex("SearchVector").HasMethod("GIN");
            }
        });

        b.Entity<OpportunitySkill>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(OpportunitySkill.MaxName);
            e.Property(x => x.Key).HasMaxLength(OpportunitySkill.MaxName);
            e.HasOne<Opportunity>().WithMany(c => c.Skills)
                .HasForeignKey(x => x.OpportunityId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.OpportunityId, x.Order });
        });

        // The two lists beside the brief, kept like the skills: ordered by
        // the client, rewritten whole on every draft save, gone with the opportunity.
        b.Entity<OpportunityRequirement>(e =>
        {
            e.Property(x => x.Title).HasMaxLength(Opportunity.MaxRubricTitle);
            e.Property(x => x.Detail).HasMaxLength(Opportunity.MaxRubricDetail);
            e.HasOne<Opportunity>().WithMany(c => c.Requirements)
                .HasForeignKey(x => x.OpportunityId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.OpportunityId, x.Order });
        });

        b.Entity<OpportunityCriterion>(e =>
        {
            e.Property(x => x.Title).HasMaxLength(Opportunity.MaxRubricTitle);
            e.Property(x => x.Description).HasMaxLength(Opportunity.MaxRubricDetail);
            e.HasOne<Opportunity>().WithMany(c => c.Criteria)
                .HasForeignKey(x => x.OpportunityId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.OpportunityId, x.Order });
        });

        b.Entity<Milestone>(e =>
        {
            e.Property(x => x.Title).HasMaxLength(200);
            e.Property(x => x.Amount).HasPrecision(12, 2);
            e.HasOne<Opportunity>().WithMany(c => c.Milestones)
                .HasForeignKey(x => x.OpportunityId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.OpportunityId, x.Order });
        });

        b.Entity<Entry>(e =>
        {
            e.Property(x => x.GithubUsername).HasMaxLength(39); // GitHub's own limit
            e.Property(x => x.Note).HasMaxLength(280);
            e.Property(x => x.RepoFullName).HasMaxLength(200);
            e.Property(x => x.RemovedReason).HasMaxLength(Domain.Entry.MaxReason); // Domain.: DbContext.Entry() shadows the name here
            e.Property(x => x.WithdrawnReason).HasMaxLength(Domain.Entry.MaxReason);
            e.Property(x => x.ZipStorageSetup).HasMaxLength(16);
            // The worker's removal follow-up: whose access window has run out.
            e.HasIndex(x => new { x.Status, x.RepoAccessEndsAtUtc });
            e.HasOne(x => x.Opportunity).WithMany(c => c.Entries)
                .HasForeignKey(x => x.OpportunityId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Freelancer).WithMany()
                .HasForeignKey(x => x.FreelancerId).OnDelete(DeleteBehavior.Restrict);
            // One active entry per freelancer per opportunity, enforced here rather
            // than in application code: a partial unique index means a race of
            // two submits produces one row and one constraint violation.
            e.HasIndex(x => new { x.OpportunityId, x.FreelancerId })
                .IsUnique()
                .HasFilter(Filter(sql, "Status", "= 0"));
            e.HasIndex(x => new { x.OpportunityId, x.Status, x.CreatedAtUtc });
            // The worker's provisioning sweep: pending entries, oldest first.
            e.HasIndex(x => new { x.ProvisionStatus, x.CreatedAtUtc });
            // The webhook's repo → entry lookup.
            e.HasIndex(x => x.RepoFullName);
        });

        b.Entity<Application>(e =>
        {
            e.Property(x => x.Summary).HasMaxLength(Profile.MaxBio);
            e.Property(x => x.Approach).HasMaxLength(Application.MaxApproach);
            e.Property(x => x.GithubUsername).HasMaxLength(39);
            // Copies of what the client read, kept as written.
            e.Property(x => x.PortfolioJson).HasColumnType(Json(sql));
            e.Property(x => x.EvaluationJson).HasColumnType(Json(sql));
            e.HasOne(x => x.Opportunity).WithMany()
                .HasForeignKey(x => x.OpportunityId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Freelancer).WithMany()
                .HasForeignKey(x => x.FreelancerId).OnDelete(DeleteBehavior.Restrict);
            // One application per freelancer per opportunity, whatever became
            // of it — a race of two submits produces one row.
            e.HasIndex(x => new { x.OpportunityId, x.FreelancerId }).IsUnique();
            // The client's review box: those under review first, oldest first.
            e.HasIndex(x => new { x.OpportunityId, x.Status, x.SubmittedAtUtc });
        });

        b.Entity<Checkpoint>(e =>
        {
            e.Property(x => x.Via).HasMaxLength(16);
            e.Property(x => x.Ref).HasMaxLength(200);
            e.Property(x => x.CommitSha).HasMaxLength(64);
            e.Property(x => x.BuildError).HasMaxLength(400);
            e.Property(x => x.BuildLogKey).HasMaxLength(300);
            e.Property(x => x.BuildLogSetup).HasMaxLength(16);
            e.Property(x => x.ChangesNote).HasMaxLength(Checkpoint.MaxChangesNote);
            // The preview worker's sweep: builds waiting for the host, and
            // the ones it is waiting on.
            e.HasIndex(x => new { x.BuildStatus, x.BuildDueAtUtc });
            e.HasOne(x => x.Entry).WithMany(x => x.Checkpoints)
                .HasForeignKey(x => x.EntryId).OnDelete(DeleteBehavior.Cascade);
            // The second cascading path from Opportunities (the first runs through
            // Entries); SQL Server refuses it, so there EF deletes the
            // checkpoints itself — an opportunity's milestones only go with the
            // opportunity, whose entries and their checkpoints go the same way.
            e.HasOne(x => x.Milestone).WithMany()
                .HasForeignKey(x => x.MilestoneId).OnDelete(sql ? DeleteBehavior.ClientCascade : DeleteBehavior.Cascade);
            // A milestone is claimed once per entry; the first claim wins and
            // a redelivered webhook is a constraint hit, not a duplicate row.
            e.HasIndex(x => new { x.EntryId, x.MilestoneId }).IsUnique();
        });

        b.Entity<Preview>(e =>
        {
            // The id is the checkpoint's or the entry's (see Preview), never generated.
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Ref).HasMaxLength(Preview.MaxRef);
            e.Property(x => x.Sha).HasMaxLength(64);
            e.Property(x => x.StopReason).HasMaxLength(Preview.MaxStopReason);
            e.Property(x => x.Error).HasMaxLength(Preview.MaxError);
            // The preview worker's sweep, as for builds.
            e.HasIndex(x => new { x.Status, x.DueAtUtc });
            e.HasOne(x => x.Entry).WithMany()
                .HasForeignKey(x => x.EntryId).OnDelete(DeleteBehavior.Cascade);
            // A second cascading path from Entries (through Checkpoints); SQL
            // Server refuses it, so there EF deletes the preview itself.
            e.HasOne(x => x.Checkpoint).WithMany()
                .HasForeignKey(x => x.CheckpointId).OnDelete(sql ? DeleteBehavior.ClientCascade : DeleteBehavior.Cascade);
            e.HasIndex(x => x.CheckpointId).IsUnique().HasFilter(Filter(sql, "CheckpointId", "IS NOT NULL"));
        });

        b.Entity<WebhookDelivery>(e =>
        {
            e.Property(x => x.Source).HasMaxLength(20).HasDefaultValue(WebhookDelivery.GitHub);
            e.Property(x => x.DeliveryId).HasMaxLength(64);
            e.Property(x => x.Event).HasMaxLength(64);
            e.Property(x => x.Action).HasMaxLength(64);
            e.Property(x => x.RepoFullName).HasMaxLength(200);
            e.Property(x => x.HandledNote).HasMaxLength(400);
            // Raw payloads stay queryable without a migration per event type.
            e.Property(x => x.Payload).HasColumnType(Json(sql));
            // GitHub redelivers; the unique delivery id makes stamping idempotent.
            e.HasIndex(x => x.DeliveryId).IsUnique();
            e.HasIndex(x => x.ReceivedAtUtc);
        });

        b.Entity<Award>(e =>
        {
            e.Property(x => x.Amount).HasPrecision(12, 2);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.TransferTargetLogin).HasMaxLength(39);
            e.Property(x => x.HandoverNote).HasMaxLength(400);
            e.HasOne(x => x.Opportunity).WithMany()
                .HasForeignKey(x => x.OpportunityId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Entry).WithMany()
                .HasForeignKey(x => x.EntryId).OnDelete(DeleteBehavior.Restrict);
            // The only payout row an opportunity ever has.
            e.HasIndex(x => x.OpportunityId).IsUnique();
        });

        b.Entity<MeritSnapshot>(e =>
        {
            // One row per freelancer per day: the worker writes a day's
            // batch only when no row for the day exists, and the trend read
            // wants one member's rows in a window, which the key serves.
            e.HasKey(x => new { x.UserId, x.DayUtc });
            e.HasOne<User>().WithMany()
                .HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            // "Is today written yet?" is the worker's first question every hour.
            e.HasIndex(x => x.DayUtc);
        });

        b.Entity<Rating>(e =>
        {
            e.Property(x => x.Comment).HasMaxLength(Rating.MaxComment);
            e.HasOne(x => x.Award).WithMany()
                .HasForeignKey(x => x.AwardId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.ByUser).WithMany()
                .HasForeignKey(x => x.ByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.OfUser).WithMany()
                .HasForeignKey(x => x.OfUserId).OnDelete(DeleteBehavior.Restrict);
            // Each party scores an award once; a re-rate is an edit, and a
            // race of two submits is one row and one constraint violation.
            e.HasIndex(x => new { x.AwardId, x.ByUserId }).IsUnique();
            // Every aggregate the portal shows is "ratings of this user".
            e.HasIndex(x => x.OfUserId);
        });

        b.Entity<Attachment>(e =>
        {
            e.Property(x => x.FileName).HasMaxLength(Attachment.MaxFileNameLength);
            e.Property(x => x.ContentType).HasMaxLength(120);
            e.Property(x => x.StorageKey).HasMaxLength(300);
            e.Property(x => x.StorageSetup).HasMaxLength(16);
            e.HasOne(x => x.Opportunity).WithMany()
                .HasForeignKey(x => x.OpportunityId).OnDelete(DeleteBehavior.Cascade);
            // Every read is "this opportunity's files".
            e.HasIndex(x => x.OpportunityId);
        });

        b.Entity<Submission>(e =>
        {
            e.Property(x => x.FileName).HasMaxLength(Submission.MaxFileNameLength);
            e.Property(x => x.ContentType).HasMaxLength(120);
            e.Property(x => x.StorageKey).HasMaxLength(300);
            e.Property(x => x.StorageSetup).HasMaxLength(16);
            e.HasOne(x => x.Entry).WithMany(x => x.Submissions)
                .HasForeignKey(x => x.EntryId).OnDelete(DeleteBehavior.Cascade);
            // Milestones only ever go with their opportunity, and the opportunity
            // takes its entries with it; set-null is belt and braces.
            e.HasOne(x => x.Milestone).WithMany()
                .HasForeignKey(x => x.MilestoneId).OnDelete(sql ? DeleteBehavior.ClientSetNull : DeleteBehavior.SetNull);
            // Every read is "this entry's files".
            e.HasIndex(x => x.EntryId);
        });

        b.Entity<AiArtifact>(e =>
        {
            e.Property(x => x.InputHash).HasMaxLength(64);
            e.Property(x => x.Note).HasMaxLength(400);
            e.Property(x => x.Provider).HasMaxLength(20);
            e.Property(x => x.Model).HasMaxLength(80);
            // One draft per feature per subject — a re-request re-queues the
            // same row, and the input hash keeps unchanged re-runs free.
            e.HasIndex(x => new { x.Feature, x.SubjectId }).IsUnique();
            // The worker's sweep: pending rows, oldest first.
            e.HasIndex(x => new { x.Status, x.CreatedAtUtc });
        });

        b.Entity<AiUsage>(e =>
        {
            // One row per day for the portal (Guid.Empty) and one per member
            // who spent; the key is what makes the count a single statement.
            e.HasKey(x => new { x.Day, x.UserId });
            e.Property(x => x.Day).HasMaxLength(10);
        });

        b.Entity<AiSpend>(e =>
        {
            // One row per day per feature per model, added to in a single
            // statement; the panel reads a month of them and sums.
            e.HasKey(x => new { x.Day, x.Feature, x.Provider, x.Model });
            e.Property(x => x.Day).HasMaxLength(10);
            e.Property(x => x.Feature).HasMaxLength(40);
            e.Property(x => x.Provider).HasMaxLength(20);
            e.Property(x => x.Model).HasMaxLength(80);
        });

        b.Entity<IdentityVerification>(e =>
        {
            e.Property(x => x.Provider).HasMaxLength(20);
            e.Property(x => x.SessionId).HasMaxLength(64);
            e.Property(x => x.SessionUrl).HasMaxLength(2000);
            e.Property(x => x.ReturnPath).HasMaxLength(512);
            e.Property(x => x.LastEventId).HasMaxLength(64);
            e.Property(x => x.Note).HasMaxLength(400);
            e.Property(x => x.ProofError).HasMaxLength(400);
            // One row per member; the provider keys its webhooks by session.
            e.HasIndex(x => x.UserId).IsUnique();
            e.HasIndex(x => x.SessionId).IsUnique();
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            // The proof worker's queue.
            e.HasIndex(x => x.ProofDueAtUtc);
        });

        b.Entity<IdentityDocument>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(IdentityDocument.MaxName);
            e.Property(x => x.ContentType).HasMaxLength(120);
            e.Property(x => x.StorageKey).HasMaxLength(300);
            e.Property(x => x.StorageSetup).HasMaxLength(16);
            // No foreign keys, on purpose (see the entity): a reset or an
            // account deleted outright leaves these behind for the proof
            // worker, which deletes the stored image before the row.
            e.HasIndex(x => x.VerificationId);
            e.HasIndex(x => x.UserId);
            e.HasIndex(x => x.RemovedAtUtc);
        });

        b.Entity<EmailMessage>(e =>
        {
            e.Property(x => x.Kind).HasMaxLength(40);
            e.Property(x => x.ToEmail).HasMaxLength(320);
            e.Property(x => x.ToName).HasMaxLength(200);
            e.Property(x => x.Subject).HasMaxLength(300);
            e.Property(x => x.ActionText).HasMaxLength(80);
            e.Property(x => x.ActionPath).HasMaxLength(300);
            e.Property(x => x.DedupeKey).HasMaxLength(120);
            e.Property(x => x.UnsubscribePath).HasMaxLength(300);
            e.Property(x => x.LastError).HasMaxLength(400);
            // The worker's send sweep: pending rows, oldest first.
            e.HasIndex(x => new { x.Status, x.CreatedAtUtc });
            // A message that could be composed twice (the digest) is inserted
            // once — the database backstops the worker's check.
            e.HasIndex(x => x.DedupeKey).IsUnique().HasFilter(Filter(sql, "DedupeKey", "IS NOT NULL"));
        });

        b.Entity<PushDevice>(e =>
        {
            e.Property(x => x.Endpoint).HasMaxLength(PushDevice.MaxEndpointLength);
            e.Property(x => x.P256dh).HasMaxLength(120); // 65 bytes base64url = 87 chars
            e.Property(x => x.Auth).HasMaxLength(40);    // 16 bytes = 22 chars
            e.Property(x => x.Label).HasMaxLength(80);
            e.HasOne(x => x.User).WithMany()
                .HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            // The browser is the identity: registering again refreshes the
            // row, never duplicates it.
            e.HasIndex(x => x.Endpoint).IsUnique();
            // The page's list, and the fan-out's "every device this person has".
            e.HasIndex(x => x.UserId);
        });

        b.Entity<PushMessage>(e =>
        {
            e.Property(x => x.Kind).HasMaxLength(40);
            e.Property(x => x.Title).HasMaxLength(200);
            e.Property(x => x.Body).HasMaxLength(400);
            e.Property(x => x.Path).HasMaxLength(300);
            e.Property(x => x.Topic).HasMaxLength(PushMessage.MaxTopicLength);
            e.Property(x => x.LastError).HasMaxLength(400);
            // A dead device takes its queue with it.
            e.HasOne(x => x.Device).WithMany()
                .HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
            // The worker's send sweep: pending rows, oldest first.
            e.HasIndex(x => new { x.Status, x.CreatedAtUtc });
        });

        b.Entity<Notification>(e =>
        {
            e.Property(x => x.Kind).HasMaxLength(40);
            e.Property(x => x.Title).HasMaxLength(Notification.MaxTitleLength);
            e.Property(x => x.Body).HasMaxLength(Notification.MaxBodyLength);
            e.Property(x => x.Path).HasMaxLength(300);
            e.HasOne(x => x.User).WithMany()
                .HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            // The bell's page, newest first, and its unread count.
            e.HasIndex(x => new { x.UserId, x.CreatedAtUtc });
            e.HasIndex(x => new { x.UserId, x.ReadAtUtc });
        });

        b.Entity<ChatMessage>(e =>
        {
            e.Property(x => x.Body).HasMaxLength(ChatMessage.MaxBodyLength);
            // The conversation is the entry's: an entry that goes takes its
            // messages with it. The sender's account is never deleted from
            // under a message — erasure blanks the account and keeps the row.
            e.HasOne(x => x.Entry).WithMany()
                .HasForeignKey(x => x.EntryId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Sender).WithMany()
                .HasForeignKey(x => x.SenderId).OnDelete(DeleteBehavior.Restrict);
            // A conversation's page, newest first, and the last line of each.
            e.HasIndex(x => new { x.EntryId, x.CreatedAtUtc });
            // What the other side has not read yet: the badge, per thread and in all.
            e.HasIndex(x => new { x.EntryId, x.SenderId, x.ReadAtUtc });
        });

        b.Entity<ChatReport>(e =>
        {
            e.Property(x => x.Reason).HasMaxLength(ChatReport.MaxReasonLength);
            e.Property(x => x.Details).HasMaxLength(ChatReport.MaxDetailsLength);
            e.Property(x => x.Resolution).HasMaxLength(ChatReport.MaxResolutionLength);
            // A report is about the entry's conversation and goes with it.
            // The reporter's key refuses a deletion, as a sender's does:
            // erasure deletes the person's own reports first. The reviewer's
            // nulls — on SQL Server by the portal at the removal site, which
            // a second cascade path through the entry would refuse to the
            // database (as Profile.DeletedByUserId).
            e.HasOne(x => x.Entry).WithMany()
                .HasForeignKey(x => x.EntryId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Reporter).WithMany()
                .HasForeignKey(x => x.ReporterId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.ResolvedBy).WithMany()
                .HasForeignKey(x => x.ResolvedById).OnDelete(sql ? DeleteBehavior.ClientSetNull : DeleteBehavior.SetNull);
            // A conversation's reports, open first; the open ones portal-wide.
            e.HasIndex(x => new { x.EntryId, x.ResolvedAtUtc });
            e.HasIndex(x => x.ResolvedAtUtc);
        });
    }
}
