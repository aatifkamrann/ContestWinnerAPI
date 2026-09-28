using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WinnersPortal.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ActivityEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Visitor = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    Kind = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    Method = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    Path = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Action = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    Page = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Subject = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Detail = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Ip = table.Column<string>(type: "nvarchar(45)", maxLength: 45, nullable: true),
                    UserAgent = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    AtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActivityEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AiArtifacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Feature = table.Column<int>(type: "int", nullable: false),
                    SubjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    InputHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    OutputJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Note = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Provider = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    Model = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiArtifacts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EmailMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    ToEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    ToName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    TextBody = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ActionText = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    ActionPath = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    DedupeKey = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    UnsubscribePath = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    AttemptedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SentAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailMessages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SettingAudits",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Key = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    OldValue = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    NewValue = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ChangedBy = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ChangedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SettingAudits", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Settings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Value = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsSecret = table.Column<bool>(type: "bit", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "SetupTests",
                columns: table => new
                {
                    Kind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    SetupId = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Ok = table.Column<bool>(type: "bit", nullable: false),
                    Detail = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    Fingerprint = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    TestedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    TestedBy = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SetupTests", x => new { x.Kind, x.SetupId });
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AvatarUpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PasswordHash = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Role = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    PasswordResetTokenHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    PasswordResetExpiresUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Phone = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    EmailConfirmedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PhoneConfirmedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    EmailCodeHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    PhoneCodeHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CodeIssuedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CodeAttempts = table.Column<int>(type: "int", nullable: false),
                    IdentityVerifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LockedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LockReason = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ErasedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SessionStamp = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    GithubUserId = table.Column<long>(type: "bigint", nullable: true),
                    GithubLogin = table.Column<string>(type: "nvarchar(39)", maxLength: 39, nullable: true),
                    GithubConnectedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ThemeAccent = table.Column<string>(type: "nvarchar(7)", maxLength: 7, nullable: true),
                    ThemeStatusColours = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: true),
                    AcceptedTermsVersion = table.Column<int>(type: "int", nullable: true),
                    AcceptedTermsAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    NotifyNewContests = table.Column<bool>(type: "bit", nullable: false),
                    NotifyWinners = table.Column<bool>(type: "bit", nullable: false),
                    NotifyByEmail = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    AwardsPaidCount = table.Column<int>(type: "int", nullable: false),
                    RatingCount = table.Column<int>(type: "int", nullable: false),
                    RatingSum = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WebhookDeliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false, defaultValue: "github"),
                    DeliveryId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Event = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Action = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    RepoFullName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Payload = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    HandledNote = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    ReceivedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebhookDeliveries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Contests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Slug = table.Column<string>(type: "nvarchar(140)", maxLength: 140, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(140)", maxLength: 140, nullable: false),
                    BriefMarkdown = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Category = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    Subcategory = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    MinMeritScore = table.Column<int>(type: "int", nullable: false),
                    AwardAmount = table.Column<decimal>(type: "decimal(12,2)", precision: 12, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Delivery = table.Column<int>(type: "int", nullable: false),
                    StartsAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DeadlineUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    EntryCloseUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ClientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    PublishedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CancelledAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CancelReason = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    MetaTitle = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    MetaDescription = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ActiveEntryCount = table.Column<int>(type: "int", nullable: false),
                    MilestoneCount = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Contests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Contests_Users_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "IdentityVerifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    SessionId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    LastEventId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Note = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DecidedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdentityVerifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IdentityVerifications_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MeritSnapshots",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DayUtc = table.Column<DateOnly>(type: "date", nullable: false),
                    Score = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MeritSnapshots", x => new { x.UserId, x.DayUtc });
                    table.ForeignKey(
                        name: "FK_MeritSnapshots_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Profiles",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Headline = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    PrimaryCategory = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    SecondaryCategories = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PrimaryWorkType = table.Column<int>(type: "int", nullable: true),
                    Bio = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Location = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    TimeZone = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    YearsExperience = table.Column<int>(type: "int", nullable: true),
                    HoursPerWeek = table.Column<int>(type: "int", nullable: true),
                    Availability = table.Column<int>(type: "int", nullable: true),
                    PreferredDurations = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PreferredProjectTypes = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    WorkingWindow = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    WebsiteUrl = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    LinkedInUrl = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DeletedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Profiles", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_Profiles_Users_DeletedByUserId",
                        column: x => x.DeletedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Profiles_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PushDevices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Endpoint = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    P256dh = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Auth = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Label = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastSeenAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PushDevices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PushDevices_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RefreshTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TokenHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SessionStamp = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IssuedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UsedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RevokedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RefreshTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RefreshTokens_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserAvatars",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Bytes = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserAvatars", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_UserAvatars_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Applications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FreelancerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Summary = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    Approach = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    PortfolioJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PortfolioCount = table.Column<int>(type: "int", nullable: false),
                    Commitment = table.Column<int>(type: "int", nullable: false),
                    HoursPerWeek = table.Column<int>(type: "int", nullable: false),
                    Advantages = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    GithubUsername = table.Column<string>(type: "nvarchar(39)", maxLength: 39, nullable: false),
                    MatchAtSubmit = table.Column<int>(type: "int", nullable: false),
                    MeritAtSubmit = table.Column<int>(type: "int", nullable: false),
                    EvaluationJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SubmittedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DecidedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DecidedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EntryId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Applications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Applications_Contests_ContestId",
                        column: x => x.ContestId,
                        principalTable: "Contests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Applications_Users_FreelancerId",
                        column: x => x.FreelancerId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Attachments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    StorageKey = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    StorageSetup = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UploadedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Attachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Attachments_Contests_ContestId",
                        column: x => x.ContestId,
                        principalTable: "Contests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ContestCriteria",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Order = table.Column<int>(type: "int", nullable: false),
                    Points = table.Column<int>(type: "int", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(240)", maxLength: 240, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContestCriteria", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ContestCriteria_Contests_ContestId",
                        column: x => x.ContestId,
                        principalTable: "Contests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ContestRequirements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Order = table.Column<int>(type: "int", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Detail = table.Column<string>(type: "nvarchar(240)", maxLength: 240, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContestRequirements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ContestRequirements_Contests_ContestId",
                        column: x => x.ContestId,
                        principalTable: "Contests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ContestSkills",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Order = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Key = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContestSkills", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ContestSkills_Contests_ContestId",
                        column: x => x.ContestId,
                        principalTable: "Contests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Entries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FreelancerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GithubUsername = table.Column<string>(type: "nvarchar(39)", maxLength: 39, nullable: false),
                    Note = table.Column<string>(type: "nvarchar(280)", maxLength: 280, nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    RepoFullName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    RepoId = table.Column<long>(type: "bigint", nullable: true),
                    DefaultBranch = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ProvisionStatus = table.Column<int>(type: "int", nullable: false),
                    ProvisionAttempts = table.Column<int>(type: "int", nullable: false),
                    ProvisionAttemptedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ProvisionNote = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    LastPushAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PushCount = table.Column<int>(type: "int", nullable: false),
                    TreeFileCount = table.Column<int>(type: "int", nullable: true),
                    TreeHasTests = table.Column<bool>(type: "bit", nullable: true),
                    TreeHasReadme = table.Column<bool>(type: "bit", nullable: true),
                    TreeHasCi = table.Column<bool>(type: "bit", nullable: true),
                    TreeReadAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    FrozenAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReviewAccessGrantedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ArchivedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ZipStorageKey = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ZipStorageSetup = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    ZipSizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    ZipPackagedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RemovedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RemovedReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RemovedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RepoAccessEndsAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    AccessRevokedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    WithdrawnAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    WithdrawnReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Entries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Entries_Contests_ContestId",
                        column: x => x.ContestId,
                        principalTable: "Contests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Entries_Users_FreelancerId",
                        column: x => x.FreelancerId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Milestones",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Order = table.Column<int>(type: "int", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DueUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    WeightPercent = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Milestones", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Milestones_Contests_ContestId",
                        column: x => x.ContestId,
                        principalTable: "Contests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProfileImages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Bytes = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    UploadedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProfileImages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProfileImages_Profiles_UserId",
                        column: x => x.UserId,
                        principalTable: "Profiles",
                        principalColumn: "UserId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProfileLanguages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Order = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Level = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProfileLanguages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProfileLanguages_Profiles_UserId",
                        column: x => x.UserId,
                        principalTable: "Profiles",
                        principalColumn: "UserId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProfilePayments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Order = table.Column<int>(type: "int", nullable: false),
                    Method = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Label = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    Details = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProfilePayments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProfilePayments_Profiles_UserId",
                        column: x => x.UserId,
                        principalTable: "Profiles",
                        principalColumn: "UserId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProfileProjects",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Order = table.Column<int>(type: "int", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(140)", maxLength: 140, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Outcome = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Role = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    Category = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    Tech = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Url = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    RepoUrl = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Year = table.Column<int>(type: "int", nullable: true),
                    Month = table.Column<int>(type: "int", nullable: true),
                    MayShowPublicly = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    ImageIds = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProfileProjects", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProfileProjects_Profiles_UserId",
                        column: x => x.UserId,
                        principalTable: "Profiles",
                        principalColumn: "UserId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProfileSkills",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Order = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Years = table.Column<int>(type: "int", nullable: false),
                    Level = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProfileSkills", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProfileSkills_Profiles_UserId",
                        column: x => x.UserId,
                        principalTable: "Profiles",
                        principalColumn: "UserId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PushMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Body = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Path = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Topic = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    AttemptedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SentAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PushMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PushMessages_PushDevices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "PushDevices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Awards",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EntryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(12,2)", precision: 12, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    AnnouncedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    PaidAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Handover = table.Column<int>(type: "int", nullable: false),
                    TransferTargetLogin = table.Column<string>(type: "nvarchar(39)", maxLength: 39, nullable: true),
                    TransferRequestedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    HandoverVerifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    HandoverNote = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Awards", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Awards_Contests_ContestId",
                        column: x => x.ContestId,
                        principalTable: "Contests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Awards_Entries_EntryId",
                        column: x => x.EntryId,
                        principalTable: "Entries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Checkpoints",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EntryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MilestoneId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CommitSha = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Via = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Ref = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ClaimedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Checkpoints", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Checkpoints_Entries_EntryId",
                        column: x => x.EntryId,
                        principalTable: "Entries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Checkpoints_Milestones_MilestoneId",
                        column: x => x.MilestoneId,
                        principalTable: "Milestones",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Submissions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EntryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MilestoneId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    FileName = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    StorageKey = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    StorageSetup = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UploadedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Submissions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Submissions_Entries_EntryId",
                        column: x => x.EntryId,
                        principalTable: "Entries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Submissions_Milestones_MilestoneId",
                        column: x => x.MilestoneId,
                        principalTable: "Milestones",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Ratings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AwardId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OfUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Stars = table.Column<int>(type: "int", nullable: false),
                    Comment = table.Column<string>(type: "nvarchar(600)", maxLength: 600, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Ratings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Ratings_Awards_AwardId",
                        column: x => x.AwardId,
                        principalTable: "Awards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Ratings_Users_ByUserId",
                        column: x => x.ByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Ratings_Users_OfUserId",
                        column: x => x.OfUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ActivityEvents_AtUtc",
                table: "ActivityEvents",
                column: "AtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ActivityEvents_UserId_AtUtc",
                table: "ActivityEvents",
                columns: new[] { "UserId", "AtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ActivityEvents_Visitor_AtUtc",
                table: "ActivityEvents",
                columns: new[] { "Visitor", "AtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AiArtifacts_Feature_SubjectId",
                table: "AiArtifacts",
                columns: new[] { "Feature", "SubjectId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AiArtifacts_Status_CreatedAtUtc",
                table: "AiArtifacts",
                columns: new[] { "Status", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Applications_ContestId_FreelancerId",
                table: "Applications",
                columns: new[] { "ContestId", "FreelancerId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Applications_ContestId_Status_SubmittedAtUtc",
                table: "Applications",
                columns: new[] { "ContestId", "Status", "SubmittedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Applications_FreelancerId",
                table: "Applications",
                column: "FreelancerId");

            migrationBuilder.CreateIndex(
                name: "IX_Attachments_ContestId",
                table: "Attachments",
                column: "ContestId");

            migrationBuilder.CreateIndex(
                name: "IX_Awards_ContestId",
                table: "Awards",
                column: "ContestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Awards_EntryId",
                table: "Awards",
                column: "EntryId");

            migrationBuilder.CreateIndex(
                name: "IX_Checkpoints_EntryId_MilestoneId",
                table: "Checkpoints",
                columns: new[] { "EntryId", "MilestoneId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Checkpoints_MilestoneId",
                table: "Checkpoints",
                column: "MilestoneId");

            migrationBuilder.CreateIndex(
                name: "IX_ContestCriteria_ContestId_Order",
                table: "ContestCriteria",
                columns: new[] { "ContestId", "Order" });

            migrationBuilder.CreateIndex(
                name: "IX_ContestRequirements_ContestId_Order",
                table: "ContestRequirements",
                columns: new[] { "ContestId", "Order" });

            migrationBuilder.CreateIndex(
                name: "IX_Contests_Category_Status_PublishedAtUtc",
                table: "Contests",
                columns: new[] { "Category", "Status", "PublishedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Contests_ClientId",
                table: "Contests",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_Contests_Slug",
                table: "Contests",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Contests_Status_PublishedAtUtc_Id",
                table: "Contests",
                columns: new[] { "Status", "PublishedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_ContestSkills_ContestId_Order",
                table: "ContestSkills",
                columns: new[] { "ContestId", "Order" });

            migrationBuilder.CreateIndex(
                name: "IX_EmailMessages_DedupeKey",
                table: "EmailMessages",
                column: "DedupeKey",
                unique: true,
                filter: "[DedupeKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_EmailMessages_Status_CreatedAtUtc",
                table: "EmailMessages",
                columns: new[] { "Status", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Entries_ContestId_FreelancerId",
                table: "Entries",
                columns: new[] { "ContestId", "FreelancerId" },
                unique: true,
                filter: "[Status] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_Entries_ContestId_Status_CreatedAtUtc",
                table: "Entries",
                columns: new[] { "ContestId", "Status", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Entries_FreelancerId",
                table: "Entries",
                column: "FreelancerId");

            migrationBuilder.CreateIndex(
                name: "IX_Entries_ProvisionStatus_CreatedAtUtc",
                table: "Entries",
                columns: new[] { "ProvisionStatus", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Entries_RepoFullName",
                table: "Entries",
                column: "RepoFullName");

            migrationBuilder.CreateIndex(
                name: "IX_Entries_Status_RepoAccessEndsAtUtc",
                table: "Entries",
                columns: new[] { "Status", "RepoAccessEndsAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_IdentityVerifications_SessionId",
                table: "IdentityVerifications",
                column: "SessionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IdentityVerifications_UserId",
                table: "IdentityVerifications",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MeritSnapshots_DayUtc",
                table: "MeritSnapshots",
                column: "DayUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Milestones_ContestId_Order",
                table: "Milestones",
                columns: new[] { "ContestId", "Order" });

            migrationBuilder.CreateIndex(
                name: "IX_ProfileImages_UserId",
                table: "ProfileImages",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_ProfileLanguages_UserId_Order",
                table: "ProfileLanguages",
                columns: new[] { "UserId", "Order" });

            migrationBuilder.CreateIndex(
                name: "IX_ProfilePayments_UserId_Order",
                table: "ProfilePayments",
                columns: new[] { "UserId", "Order" });

            migrationBuilder.CreateIndex(
                name: "IX_ProfileProjects_UserId_Order",
                table: "ProfileProjects",
                columns: new[] { "UserId", "Order" });

            migrationBuilder.CreateIndex(
                name: "IX_Profiles_DeletedByUserId",
                table: "Profiles",
                column: "DeletedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ProfileSkills_UserId_Order",
                table: "ProfileSkills",
                columns: new[] { "UserId", "Order" });

            migrationBuilder.CreateIndex(
                name: "IX_PushDevices_Endpoint",
                table: "PushDevices",
                column: "Endpoint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PushDevices_UserId",
                table: "PushDevices",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_PushMessages_DeviceId",
                table: "PushMessages",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_PushMessages_Status_CreatedAtUtc",
                table: "PushMessages",
                columns: new[] { "Status", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Ratings_AwardId_ByUserId",
                table: "Ratings",
                columns: new[] { "AwardId", "ByUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Ratings_ByUserId",
                table: "Ratings",
                column: "ByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Ratings_OfUserId",
                table: "Ratings",
                column: "OfUserId");

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_TokenHash",
                table: "RefreshTokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_UserId",
                table: "RefreshTokens",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_SettingAudits_Key_ChangedAtUtc",
                table: "SettingAudits",
                columns: new[] { "Key", "ChangedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Submissions_EntryId",
                table: "Submissions",
                column: "EntryId");

            migrationBuilder.CreateIndex(
                name: "IX_Submissions_MilestoneId",
                table: "Submissions",
                column: "MilestoneId");

            migrationBuilder.CreateIndex(
                name: "IX_Users_Email",
                table: "Users",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_PasswordResetTokenHash",
                table: "Users",
                column: "PasswordResetTokenHash");

            migrationBuilder.CreateIndex(
                name: "IX_WebhookDeliveries_DeliveryId",
                table: "WebhookDeliveries",
                column: "DeliveryId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WebhookDeliveries_ReceivedAtUtc",
                table: "WebhookDeliveries",
                column: "ReceivedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ActivityEvents");

            migrationBuilder.DropTable(
                name: "AiArtifacts");

            migrationBuilder.DropTable(
                name: "Applications");

            migrationBuilder.DropTable(
                name: "Attachments");

            migrationBuilder.DropTable(
                name: "Checkpoints");

            migrationBuilder.DropTable(
                name: "ContestCriteria");

            migrationBuilder.DropTable(
                name: "ContestRequirements");

            migrationBuilder.DropTable(
                name: "ContestSkills");

            migrationBuilder.DropTable(
                name: "EmailMessages");

            migrationBuilder.DropTable(
                name: "IdentityVerifications");

            migrationBuilder.DropTable(
                name: "MeritSnapshots");

            migrationBuilder.DropTable(
                name: "ProfileImages");

            migrationBuilder.DropTable(
                name: "ProfileLanguages");

            migrationBuilder.DropTable(
                name: "ProfilePayments");

            migrationBuilder.DropTable(
                name: "ProfileProjects");

            migrationBuilder.DropTable(
                name: "ProfileSkills");

            migrationBuilder.DropTable(
                name: "PushMessages");

            migrationBuilder.DropTable(
                name: "Ratings");

            migrationBuilder.DropTable(
                name: "RefreshTokens");

            migrationBuilder.DropTable(
                name: "SettingAudits");

            migrationBuilder.DropTable(
                name: "Settings");

            migrationBuilder.DropTable(
                name: "SetupTests");

            migrationBuilder.DropTable(
                name: "Submissions");

            migrationBuilder.DropTable(
                name: "UserAvatars");

            migrationBuilder.DropTable(
                name: "WebhookDeliveries");

            migrationBuilder.DropTable(
                name: "Profiles");

            migrationBuilder.DropTable(
                name: "PushDevices");

            migrationBuilder.DropTable(
                name: "Awards");

            migrationBuilder.DropTable(
                name: "Milestones");

            migrationBuilder.DropTable(
                name: "Entries");

            migrationBuilder.DropTable(
                name: "Contests");

            migrationBuilder.DropTable(
                name: "Users");
        }
    }
}
