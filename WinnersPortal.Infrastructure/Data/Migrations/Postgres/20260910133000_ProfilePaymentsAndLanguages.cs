using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WinnersPortal.Infrastructure.Data.Migrations.Postgres
{
    /// <summary>
    /// Two lists arrive on a profile, and one column leaves it.
    ///
    /// Languages stop being "English, Urdu, German" in a single column and
    /// become rows with a level each, the way skills already were. Nobody
    /// loses what they wrote: the copy below splits the old column on its
    /// commas before it is dropped, and lands every language on
    /// <c>Professional</c> — which is exactly what the field it came from
    /// claimed, since it was labelled "languages you work in".
    ///
    /// Payments are new, private, and encrypted by the application before
    /// they reach the Details column, so this migration creates a table it
    /// cannot read the contents of.
    /// </summary>
    public partial class ProfilePaymentsAndLanguages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProfileLanguages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Level = table.Column<int>(type: "integer", nullable: false)
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
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    Method = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Label = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    Details = table.Column<string>(type: "text", nullable: false)
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

            migrationBuilder.CreateIndex(
                name: "IX_ProfileLanguages_UserId_Order",
                table: "ProfileLanguages",
                columns: new[] { "UserId", "Order" });

            migrationBuilder.CreateIndex(
                name: "IX_ProfilePayments_UserId_Order",
                table: "ProfilePayments",
                columns: new[] { "UserId", "Order" });

            // What people already typed, moved across before the column goes:
            // split on the commas, blanks dropped, and the same language said
            // twice folded to the first spelling — the profile now refuses a
            // duplicate on save, and nobody should meet that refusal holding
            // a list they did not write today. Level 2 is Professional.
            migrationBuilder.Sql(
                """
                INSERT INTO "ProfileLanguages" ("Id", "UserId", "Order", "Name", "Level")
                SELECT gen_random_uuid(),
                       s."UserId",
                       (row_number() OVER (PARTITION BY s."UserId" ORDER BY s.ord))::int - 1,
                       s.name,
                       2
                FROM (
                    SELECT DISTINCT ON (p."UserId", lower(btrim(item.value)))
                           p."UserId",
                           item.ord,
                           left(btrim(item.value), 60) AS name
                    FROM "Profiles" p
                    CROSS JOIN LATERAL unnest(string_to_array(p."Languages", ','))
                        WITH ORDINALITY AS item(value, ord)
                    WHERE p."Languages" IS NOT NULL AND btrim(item.value) <> ''
                    ORDER BY p."UserId", lower(btrim(item.value)), item.ord
                ) s;
                """);

            migrationBuilder.DropColumn(
                name: "Languages",
                table: "Profiles");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Languages",
                table: "Profiles",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);

            // Back into one comma-separated column, in the order they were
            // listed. The levels are the part that cannot survive the trip;
            // going down is a rollback, not a round trip.
            migrationBuilder.Sql(
                """
                UPDATE "Profiles" p
                SET "Languages" = sub.list
                FROM (
                    SELECT "UserId", left(string_agg("Name", ', ' ORDER BY "Order"), 160) AS list
                    FROM "ProfileLanguages"
                    GROUP BY "UserId"
                ) sub
                WHERE p."UserId" = sub."UserId";
                """);

            migrationBuilder.DropTable(
                name: "ProfileLanguages");

            migrationBuilder.DropTable(
                name: "ProfilePayments");
        }
    }
}
