using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WinnersPortal.Infrastructure.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class Phase3GithubAppAndWebhooks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "GithubConnectedAtUtc",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GithubLogin",
                table: "Users",
                type: "character varying(39)",
                maxLength: 39,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "GithubUserId",
                table: "Users",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ArchivedAtUtc",
                table: "Entries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DefaultBranch",
                table: "Entries",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "FrozenAtUtc",
                table: "Entries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastPushAtUtc",
                table: "Entries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ProvisionAttemptedAtUtc",
                table: "Entries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ProvisionAttempts",
                table: "Entries",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ProvisionNote",
                table: "Entries",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ProvisionStatus",
                table: "Entries",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "PushCount",
                table: "Entries",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "RepoId",
                table: "Entries",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReviewAccessGrantedAtUtc",
                table: "Entries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Awards",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ContestId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    AnnouncedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PaidAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Handover = table.Column<int>(type: "integer", nullable: false),
                    TransferTargetLogin = table.Column<string>(type: "character varying(39)", maxLength: 39, nullable: true),
                    TransferRequestedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    HandoverVerifiedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    HandoverNote = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true)
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
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EntryId = table.Column<Guid>(type: "uuid", nullable: false),
                    MilestoneId = table.Column<Guid>(type: "uuid", nullable: false),
                    CommitSha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Via = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Ref = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ClaimedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
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
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WebhookDeliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeliveryId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Event = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    RepoFullName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Payload = table.Column<string>(type: "jsonb", nullable: false),
                    HandledNote = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    ReceivedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebhookDeliveries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Entries_ProvisionStatus_CreatedAtUtc",
                table: "Entries",
                columns: new[] { "ProvisionStatus", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Entries_RepoFullName",
                table: "Entries",
                column: "RepoFullName");

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
                name: "Awards");

            migrationBuilder.DropTable(
                name: "Checkpoints");

            migrationBuilder.DropTable(
                name: "WebhookDeliveries");

            migrationBuilder.DropIndex(
                name: "IX_Entries_ProvisionStatus_CreatedAtUtc",
                table: "Entries");

            migrationBuilder.DropIndex(
                name: "IX_Entries_RepoFullName",
                table: "Entries");

            migrationBuilder.DropColumn(
                name: "GithubConnectedAtUtc",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "GithubLogin",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "GithubUserId",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ArchivedAtUtc",
                table: "Entries");

            migrationBuilder.DropColumn(
                name: "DefaultBranch",
                table: "Entries");

            migrationBuilder.DropColumn(
                name: "FrozenAtUtc",
                table: "Entries");

            migrationBuilder.DropColumn(
                name: "LastPushAtUtc",
                table: "Entries");

            migrationBuilder.DropColumn(
                name: "ProvisionAttemptedAtUtc",
                table: "Entries");

            migrationBuilder.DropColumn(
                name: "ProvisionAttempts",
                table: "Entries");

            migrationBuilder.DropColumn(
                name: "ProvisionNote",
                table: "Entries");

            migrationBuilder.DropColumn(
                name: "ProvisionStatus",
                table: "Entries");

            migrationBuilder.DropColumn(
                name: "PushCount",
                table: "Entries");

            migrationBuilder.DropColumn(
                name: "RepoId",
                table: "Entries");

            migrationBuilder.DropColumn(
                name: "ReviewAccessGrantedAtUtc",
                table: "Entries");
        }
    }
}
