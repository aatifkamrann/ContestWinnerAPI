using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WinnersPortal.Infrastructure.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class FeedCounters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AwardsPaidCount",
                table: "Users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "RatingCount",
                table: "Users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "RatingSum",
                table: "Users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ActiveEntryCount",
                table: "Contests",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MilestoneCount",
                table: "Contests",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Backfill from truth — the same recomputation Recount runs on
            // every later write, so rows that predate these columns start at
            // the numbers the feed was computing per card until now.
            migrationBuilder.Sql("""
                UPDATE "Contests" c SET
                    "ActiveEntryCount" = (SELECT count(*) FROM "Entries" e
                        WHERE e."ContestId" = c."Id" AND e."Status" = 0),
                    "MilestoneCount" = (SELECT count(*) FROM "Milestones" m
                        WHERE m."ContestId" = c."Id");
                """);
            migrationBuilder.Sql("""
                UPDATE "Users" u SET
                    "AwardsPaidCount" = (SELECT count(*) FROM "Awards" a
                        JOIN "Contests" c ON c."Id" = a."ContestId"
                        WHERE c."ClientId" = u."Id" AND a."PaidAtUtc" IS NOT NULL),
                    "RatingCount" = (SELECT count(*) FROM "Ratings" r WHERE r."OfUserId" = u."Id"),
                    "RatingSum" = (SELECT coalesce(sum(r."Stars"), 0) FROM "Ratings" r WHERE r."OfUserId" = u."Id");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AwardsPaidCount",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "RatingCount",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "RatingSum",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ActiveEntryCount",
                table: "Contests");

            migrationBuilder.DropColumn(
                name: "MilestoneCount",
                table: "Contests");
        }
    }
}
