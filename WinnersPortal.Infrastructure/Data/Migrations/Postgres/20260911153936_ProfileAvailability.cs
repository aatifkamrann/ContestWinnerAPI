using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WinnersPortal.Infrastructure.Data.Migrations.Postgres
{
    /// <summary>
    /// When a member can work: whether they can start, the lengths of work
    /// and the terms they prefer, and the hours of the day they keep. Four
    /// columns on Profiles. The hours a week were already there and stay
    /// where they are — the form moved that question into this section,
    /// and the column did not need to follow it.
    ///
    /// Nothing is written to an existing row: the status starts null
    /// (nothing said, and never "available now" on anybody's behalf) and
    /// the two sets start empty, which is the answer every existing member
    /// already gives.
    /// </summary>
    public partial class ProfileAvailability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Availability",
                table: "Profiles",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<List<string>>(
                name: "PreferredDurations",
                table: "Profiles",
                type: "text[]",
                nullable: false,
                // An empty array, not null, as the categories column was
                // added: a NOT NULL column on a table with rows needs one,
                // and "no preference" is what every row already says.
                defaultValueSql: "'{}'::text[]");

            migrationBuilder.AddColumn<List<string>>(
                name: "PreferredProjectTypes",
                table: "Profiles",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'::text[]");

            migrationBuilder.AddColumn<string>(
                name: "WorkingWindow",
                table: "Profiles",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Availability",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "PreferredDurations",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "PreferredProjectTypes",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "WorkingWindow",
                table: "Profiles");
        }
    }
}
