using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WinnersPortal.Migrations.SqlServer
{
    /// <summary>
    /// Contests became opportunities. Hand-written over the scaffold, which
    /// dropped and recreated every table: this renames each table, column,
    /// index and key in place, so no row moves and the full-text index goes
    /// with its table (sp_rename carries it, key index and all). Then the
    /// stored values that spelt the old word — four settings keys, their
    /// audit rows and four notification kinds — are rewritten, and the seven
    /// procedures named for contests are dropped: the start-up installs
    /// their successors, and every other procedure, after the migrations.
    /// Down undoes all of it; the older build reinstalls its own procedures
    /// when it starts.
    /// </summary>
    public partial class RenameToOpportunities : Migration
    {
        private static readonly (string Old, string New)[] Tables =
        [
            ("Contests", "Opportunities"),
            ("ContestSkills", "OpportunitySkills"),
            ("ContestRequirements", "OpportunityRequirements"),
            ("ContestCriteria", "OpportunityCriteria"),
        ];

        /// <summary>(table as it is named after the rename, old column, new column).</summary>
        private static readonly (string Table, string Old, string New)[] Columns =
        [
            ("Applications", "ContestId", "OpportunityId"),
            ("Attachments", "ContestId", "OpportunityId"),
            ("Awards", "ContestId", "OpportunityId"),
            ("Entries", "ContestId", "OpportunityId"),
            ("Milestones", "ContestId", "OpportunityId"),
            ("OpportunitySkills", "ContestId", "OpportunityId"),
            ("OpportunityRequirements", "ContestId", "OpportunityId"),
            ("OpportunityCriteria", "ContestId", "OpportunityId"),
            ("Users", "NotifyNewContests", "NotifyNewOpportunities"),
        ];

        private static readonly (string Table, string Old, string New)[] Indexes =
        [
            ("Applications", "IX_Applications_ContestId_FreelancerId", "IX_Applications_OpportunityId_FreelancerId"),
            ("Applications", "IX_Applications_ContestId_Status_SubmittedAtUtc", "IX_Applications_OpportunityId_Status_SubmittedAtUtc"),
            ("Attachments", "IX_Attachments_ContestId", "IX_Attachments_OpportunityId"),
            ("Awards", "IX_Awards_ContestId", "IX_Awards_OpportunityId"),
            ("Entries", "IX_Entries_ContestId_FreelancerId", "IX_Entries_OpportunityId_FreelancerId"),
            ("Entries", "IX_Entries_ContestId_Status_CreatedAtUtc", "IX_Entries_OpportunityId_Status_CreatedAtUtc"),
            ("Milestones", "IX_Milestones_ContestId_Order", "IX_Milestones_OpportunityId_Order"),
            ("Opportunities", "IX_Contests_Category_Status_PublishedAtUtc", "IX_Opportunities_Category_Status_PublishedAtUtc"),
            ("Opportunities", "IX_Contests_ClientId", "IX_Opportunities_ClientId"),
            ("Opportunities", "IX_Contests_Slug", "IX_Opportunities_Slug"),
            ("Opportunities", "IX_Contests_Status_PublishedAtUtc_Id", "IX_Opportunities_Status_PublishedAtUtc_Id"),
            ("OpportunityCriteria", "IX_ContestCriteria_ContestId_Order", "IX_OpportunityCriteria_OpportunityId_Order"),
            ("OpportunityRequirements", "IX_ContestRequirements_ContestId_Order", "IX_OpportunityRequirements_OpportunityId_Order"),
            ("OpportunitySkills", "IX_ContestSkills_ContestId_Order", "IX_OpportunitySkills_OpportunityId_Order"),
        ];

        /// <summary>Primary and foreign keys: constraints, renamed as objects.</summary>
        private static readonly (string Old, string New)[] Keys =
        [
            ("PK_Contests", "PK_Opportunities"),
            ("PK_ContestSkills", "PK_OpportunitySkills"),
            ("PK_ContestRequirements", "PK_OpportunityRequirements"),
            ("PK_ContestCriteria", "PK_OpportunityCriteria"),
            ("FK_Applications_Contests_ContestId", "FK_Applications_Opportunities_OpportunityId"),
            ("FK_Attachments_Contests_ContestId", "FK_Attachments_Opportunities_OpportunityId"),
            ("FK_Awards_Contests_ContestId", "FK_Awards_Opportunities_OpportunityId"),
            ("FK_Entries_Contests_ContestId", "FK_Entries_Opportunities_OpportunityId"),
            ("FK_Milestones_Contests_ContestId", "FK_Milestones_Opportunities_OpportunityId"),
            ("FK_Contests_Users_ClientId", "FK_Opportunities_Users_ClientId"),
            ("FK_ContestSkills_Contests_ContestId", "FK_OpportunitySkills_Opportunities_OpportunityId"),
            ("FK_ContestRequirements_Contests_ContestId", "FK_OpportunityRequirements_Opportunities_OpportunityId"),
            ("FK_ContestCriteria_Contests_ContestId", "FK_OpportunityCriteria_Opportunities_OpportunityId"),
        ];

        private static readonly (string Old, string New)[] SettingKeys =
        [
            ("contest.minAwardUsd", "opportunity.minAwardUsd"),
            ("contest.defaultDurationDays", "opportunity.defaultDurationDays"),
            ("contest.reviewWindowDays", "opportunity.reviewWindowDays"),
            ("limits.maxEntriesPerContest", "limits.maxEntriesPerOpportunity"),
        ];

        private static readonly (string Old, string New)[] Kinds =
        [
            ("contest_open", "opportunity_open"),
            ("contest_cancelled", "opportunity_cancelled"),
            ("contest_review_client", "opportunity_review_client"),
            ("contest_review_entrant", "opportunity_review_entrant"),
        ];

        private static readonly (string Old, string New)[] Procedures =
        [
            ("Contest_Feed", "Opportunity_Feed"),
            ("Contest_Open", "Opportunity_Open"),
            ("Contest_ApplicationCounts", "Opportunity_ApplicationCounts"),
            ("Contest_SlugTaken", "Opportunity_SlugTaken"),
            ("Contest_Status", "Opportunity_Status"),
            ("Recount_Contest", "Recount_Opportunity"),
            ("Ai_OwnsContest", "Ai_OwnsOpportunity"),
        ];

        private static readonly string[] KindTables = ["EmailMessages", "Notifications", "PushMessages"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var (from, to) in Tables)
                migrationBuilder.RenameTable(name: from, newName: to);
            foreach (var (table, from, to) in Columns)
                migrationBuilder.RenameColumn(name: from, table: table, newName: to);
            foreach (var (table, from, to) in Indexes)
                migrationBuilder.RenameIndex(name: from, table: table, newName: to);
            foreach (var (from, to) in Keys)
                migrationBuilder.Sql($"EXEC sp_rename N'[dbo].[{from}]', N'{to}', N'OBJECT';");
            Values(migrationBuilder, up: true);
            foreach (var (from, _) in Procedures)
                migrationBuilder.Sql($"DROP PROCEDURE IF EXISTS [dbo].[{from}];");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var (_, to) in Procedures)
                migrationBuilder.Sql($"DROP PROCEDURE IF EXISTS [dbo].[{to}];");
            Values(migrationBuilder, up: false);
            foreach (var (from, to) in Keys)
                migrationBuilder.Sql($"EXEC sp_rename N'[dbo].[{to}]', N'{from}', N'OBJECT';");
            foreach (var (table, from, to) in Indexes)
                migrationBuilder.RenameIndex(name: to, table: table, newName: from);
            foreach (var (table, from, to) in Columns)
                migrationBuilder.RenameColumn(name: to, table: table, newName: from);
            foreach (var (from, to) in Tables)
                migrationBuilder.RenameTable(name: to, newName: from);
        }

        /// <summary>The stored words: settings keys (and their audit trail) and notification kinds.</summary>
        private static void Values(MigrationBuilder migrationBuilder, bool up)
        {
            foreach (var (old, @new) in SettingKeys)
            {
                var (from, to) = up ? (old, @new) : (@new, old);
                migrationBuilder.Sql($"UPDATE [Settings] SET [Key] = N'{to}' WHERE [Key] = N'{from}';");
                migrationBuilder.Sql($"UPDATE [SettingAudits] SET [Key] = N'{to}' WHERE [Key] = N'{from}';");
            }
            foreach (var (old, @new) in Kinds)
            {
                var (from, to) = up ? (old, @new) : (@new, old);
                foreach (var table in KindTables)
                    migrationBuilder.Sql($"UPDATE [{table}] SET [Kind] = N'{to}' WHERE [Kind] = N'{from}';");
            }
        }
    }
}
