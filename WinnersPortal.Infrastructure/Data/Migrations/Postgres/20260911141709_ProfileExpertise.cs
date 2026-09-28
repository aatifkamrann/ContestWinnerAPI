using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WinnersPortal.Infrastructure.Data.Migrations.Postgres
{
    /// <summary>
    /// What a member says they do: one kind of work, up to three more, and
    /// whether this is their whole week. Three columns on Profiles and
    /// nothing else — the skill scale grew a fourth rung in the same change,
    /// and needs no data written: what was stored as "strong" (2) is
    /// "advanced" now, and "expert" (3) is a claim nobody has made yet.
    /// </summary>
    public partial class ProfileExpertise : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PrimaryCategory",
                table: "Profiles",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PrimaryWorkType",
                table: "Profiles",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<List<string>>(
                name: "SecondaryCategories",
                table: "Profiles",
                type: "text[]",
                nullable: false,
                // An empty array, not null: "no other kinds of work" is the
                // answer every existing row already gives, and a NOT NULL
                // column added to a table with rows in it needs one.
                defaultValueSql: "'{}'::text[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PrimaryCategory",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "PrimaryWorkType",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "SecondaryCategories",
                table: "Profiles");
        }
    }
}
