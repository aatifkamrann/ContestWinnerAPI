using System.Reflection;

namespace WinnersPortal.Infrastructure.Data;

/// <summary>
/// Every stored procedure the portal calls on SQL Server, one handle each,
/// named for its script under <c>Data/Procedures</c>. A reader names the
/// handle; the script says what it does. Adding a procedure is a script
/// and a field here — <c>DapperTests</c> holds the two lists to each
/// other, and <see cref="StoredProcedures.ApplyAsync"/> installs them all
/// at every start, so a database that predates a procedure has it before
/// the first request.
/// </summary>
public static class Procedures
{
    // ---- accounts and gates
    public static readonly Procedure AccountSession = new("Account_Session");
    public static readonly Procedure AccountConfirmed = new("Account_Confirmed");
    public static readonly Procedure AccountAcceptedTerms = new("Account_AcceptedTerms");
    public static readonly Procedure UsersByRole = new("Users_ByRole");

    // ---- the feed, an opportunity's page and the live board
    public static readonly Procedure OpportunityFeed = new("Opportunity_Feed");
    public static readonly Procedure OpportunityOpen = new("Opportunity_Open");
    public static readonly Procedure OpportunityApplicationCounts = new("Opportunity_ApplicationCounts");
    public static readonly Procedure OpportunitySlugTaken = new("Opportunity_SlugTaken");
    public static readonly Procedure OpportunityStatus = new("Opportunity_Status");
    public static readonly Procedure StandingBatch = new("Standing_Batch");
    public static readonly Procedure FitProfile = new("Fit_Profile");
    public static readonly Procedure FitEntries = new("Fit_Entries");

    // ---- profiles, merit and the board
    public static readonly Procedure MeritRecord = new("Merit_Record");
    public static readonly Procedure MeritReads = new("Merit_Reads");
    public static readonly Procedure MeritSnapshotRecorded = new("Merit_SnapshotRecorded");
    public static readonly Procedure MeritSnapshotInsert = new("Merit_SnapshotInsert");
    public static readonly Procedure MeritSnapshotSweep = new("Merit_SnapshotSweep");
    public static readonly Procedure LeaderboardUsers = new("Leaderboard_Users");
    public static readonly Procedure LeaderboardProfiles = new("Leaderboard_Profiles");
    public static readonly Procedure LeaderboardHistory = new("Leaderboard_History");
    public static readonly Procedure WelcomeAccount = new("Welcome_Account");
    public static readonly Procedure WelcomeProfile = new("Welcome_Profile");
    public static readonly Procedure WelcomePeers = new("Welcome_Peers");
    public static readonly Procedure ProfileReviewAccount = new("ProfileReview_Account");
    public static readonly Procedure ProfileReviewProfile = new("ProfileReview_Profile");
    public static readonly Procedure ProfileReviewOpen = new("ProfileReview_Open");

    // ---- dashboards, reports, the activity log and the operations console
    public static readonly Procedure DashboardClient = new("Dashboard_Client");
    public static readonly Procedure DashboardFreelancer = new("Dashboard_Freelancer");
    public static readonly Procedure DashboardWaiting = new("Dashboard_Waiting");
    public static readonly Procedure DashboardAdmin = new("Dashboard_Admin");
    public static readonly Procedure ReportOpportunities = new("Report_Opportunities");
    public static readonly Procedure ReportOpportunityFigures = new("Report_OpportunityFigures");
    public static readonly Procedure ReportMyParts = new("Report_MyParts");
    public static readonly Procedure ReportApplications = new("Report_Applications");
    public static readonly Procedure ReportApplicationOutcomes = new("Report_ApplicationOutcomes");
    public static readonly Procedure ReportEntries = new("Report_Entries");
    public static readonly Procedure ReportEntryFacts = new("Report_EntryFacts");
    public static readonly Procedure ActivityPage = new("Activity_Page");
    public static readonly Procedure ActivityInsert = new("Activity_Insert");
    public static readonly Procedure ActivitySweep = new("Activity_Sweep");
    public static readonly Procedure ActivityExchange = new("Activity_Exchange");
    public static readonly Procedure ActivityAiModels = new("Activity_AiModels");
    public static readonly Procedure AdminOperations = new("Admin_Operations");

    // ---- settings and setups
    public static readonly Procedure SetupFileCounts = new("Setup_FileCounts");
    public static readonly Procedure SetupRepos = new("Setup_Repos");
    public static readonly Procedure SetupTestRecord = new("SetupTest_Record");
    public static readonly Procedure SetupTestPrune = new("SetupTest_Prune");

    // ---- AI
    public static readonly Procedure AiArtifact = new("Ai_Artifact");
    public static readonly Procedure AiModerated = new("Ai_Moderated");
    public static readonly Procedure AiOwnsOpportunity = new("Ai_OwnsOpportunity");
    public static readonly Procedure AiEntryGate = new("Ai_EntryGate");
    public static readonly Procedure AiOwnsEntry = new("Ai_OwnsEntry");

    // ---- the inbox behind the bell
    public static readonly Procedure InboxPage = new("Inbox_Page");
    public static readonly Procedure InboxMarkOne = new("Inbox_MarkOne");
    public static readonly Procedure InboxMarkAll = new("Inbox_MarkAll");
    public static readonly Procedure InboxSweep = new("Inbox_Sweep");

    // ---- the workers' and the services' set-based writes
    public static readonly Procedure EmailExpire = new("Email_Expire");
    public static readonly Procedure PushExpire = new("Push_Expire");
    public static readonly Procedure PushRemoveDevices = new("Push_RemoveDevices");
    public static readonly Procedure PushDropPending = new("Push_DropPending");
    public static readonly Procedure PushRemoveDevice = new("Push_RemoveDevice");
    public static readonly Procedure AttachmentDropStale = new("Attachment_DropStale");
    public static readonly Procedure RecountOpportunity = new("Recount_Opportunity");
    public static readonly Procedure RecountUser = new("Recount_User");

    /// <summary>Every handle above, in the order they are installed.</summary>
    public static IReadOnlyList<Procedure> All { get; } =
        typeof(Procedures).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(Procedure))
            .Select(f => (Procedure)f.GetValue(null)!)
            .ToList();
}
