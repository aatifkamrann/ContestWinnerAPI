using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WinnersPortal.Infrastructure.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class Applications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Applications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ContestId = table.Column<Guid>(type: "uuid", nullable: false),
                    FreelancerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Summary = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Approach = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    PortfolioJson = table.Column<string>(type: "jsonb", nullable: false),
                    PortfolioCount = table.Column<int>(type: "integer", nullable: false),
                    Commitment = table.Column<int>(type: "integer", nullable: false),
                    HoursPerWeek = table.Column<int>(type: "integer", nullable: false),
                    Advantages = table.Column<string[]>(type: "text[]", nullable: false),
                    GithubUsername = table.Column<string>(type: "character varying(39)", maxLength: 39, nullable: false),
                    MatchAtSubmit = table.Column<int>(type: "integer", nullable: false),
                    MeritAtSubmit = table.Column<int>(type: "integer", nullable: false),
                    EvaluationJson = table.Column<string>(type: "jsonb", nullable: false),
                    SubmittedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DecidedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DecidedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    EntryId = table.Column<Guid>(type: "uuid", nullable: true)
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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Applications");
        }
    }
}
