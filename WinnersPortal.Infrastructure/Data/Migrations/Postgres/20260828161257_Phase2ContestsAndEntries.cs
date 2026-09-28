using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace WinnersPortal.Infrastructure.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class Phase2ContestsAndEntries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Hand-edited from the scaffolded drop-and-recreate: AdminUsers
            // becomes Users in place, so the administrator the wizard created
            // survives the upgrade. Every pre-existing row is an admin — the
            // wizard was the only way in.
            migrationBuilder.RenameTable(
                name: "AdminUsers",
                newName: "Users");

            migrationBuilder.RenameIndex(
                name: "IX_AdminUsers_Email",
                table: "Users",
                newName: "IX_Users_Email");

            migrationBuilder.Sql("""ALTER TABLE "Users" RENAME CONSTRAINT "PK_AdminUsers" TO "PK_Users";""");

            migrationBuilder.AddColumn<string>(
                name: "Role",
                table: "Users",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "admin");

            migrationBuilder.CreateTable(
                name: "Contests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Slug = table.Column<string>(type: "character varying(140)", maxLength: 140, nullable: false),
                    Title = table.Column<string>(type: "character varying(140)", maxLength: 140, nullable: false),
                    BriefMarkdown = table.Column<string>(type: "text", nullable: false),
                    AwardAmount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    DeadlineUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PublishedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SearchVector = table.Column<NpgsqlTsVector>(type: "tsvector", nullable: false)
                        .Annotation("Npgsql:TsVectorConfig", "english")
                        .Annotation("Npgsql:TsVectorProperties", new[] { "Title", "BriefMarkdown" })
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
                name: "Entries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ContestId = table.Column<Guid>(type: "uuid", nullable: false),
                    FreelancerId = table.Column<Guid>(type: "uuid", nullable: false),
                    GithubUsername = table.Column<string>(type: "character varying(39)", maxLength: 39, nullable: false),
                    Note = table.Column<string>(type: "character varying(280)", maxLength: 280, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    RepoFullName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    WithdrawnAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
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
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ContestId = table.Column<Guid>(type: "uuid", nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true)
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

            migrationBuilder.CreateIndex(
                name: "IX_Contests_ClientId",
                table: "Contests",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_Contests_SearchVector",
                table: "Contests",
                column: "SearchVector")
                .Annotation("Npgsql:IndexMethod", "GIN");

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
                name: "IX_Entries_ContestId_FreelancerId",
                table: "Entries",
                columns: new[] { "ContestId", "FreelancerId" },
                unique: true,
                filter: "\"Status\" = 0");

            migrationBuilder.CreateIndex(
                name: "IX_Entries_ContestId_Status_CreatedAtUtc",
                table: "Entries",
                columns: new[] { "ContestId", "Status", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Entries_FreelancerId",
                table: "Entries",
                column: "FreelancerId");

            migrationBuilder.CreateIndex(
                name: "IX_Milestones_ContestId_Order",
                table: "Milestones",
                columns: new[] { "ContestId", "Order" });

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Entries");

            migrationBuilder.DropTable(
                name: "Milestones");

            migrationBuilder.DropTable(
                name: "Contests");

            migrationBuilder.DropColumn(
                name: "Role",
                table: "Users");

            migrationBuilder.Sql("""ALTER TABLE "Users" RENAME CONSTRAINT "PK_Users" TO "PK_AdminUsers";""");

            migrationBuilder.RenameIndex(
                name: "IX_Users_Email",
                table: "Users",
                newName: "IX_AdminUsers_Email");

            migrationBuilder.RenameTable(
                name: "Users",
                newName: "AdminUsers");
        }
    }
}
