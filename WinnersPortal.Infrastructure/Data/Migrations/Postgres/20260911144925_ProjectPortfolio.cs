using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WinnersPortal.Infrastructure.Data.Migrations.Postgres
{
    /// <summary>
    /// Past work becomes a portfolio: which kind of work each project was,
    /// the month it finished beside the year that was already there, whether
    /// the member has said they may show it publicly, and the pictures.
    ///
    /// Two defaults do the work of a data migration here. MayShowPublicly
    /// starts true, because every project already on the portal was already
    /// being published and a column added underneath somebody must not
    /// quietly unpublish their portfolio; the form starts a new project
    /// unticked instead, where it is a question being asked rather than an
    /// answer being changed. ImageIds starts as an empty array, so an
    /// existing row needs nothing written to it at all.
    /// </summary>
    public partial class ProjectPortfolio : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Category",
                table: "ProfileProjects",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<List<Guid>>(
                name: "ImageIds",
                table: "ProfileProjects",
                type: "uuid[]",
                nullable: false,
                defaultValueSql: "'{}'::uuid[]");

            migrationBuilder.AddColumn<bool>(
                name: "MayShowPublicly",
                table: "ProfileProjects",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "Month",
                table: "ProfileProjects",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ProfileImages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Bytes = table.Column<byte[]>(type: "bytea", nullable: false),
                    ContentType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    UploadedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
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

            migrationBuilder.CreateIndex(
                name: "IX_ProfileImages_UserId",
                table: "ProfileImages",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProfileImages");

            migrationBuilder.DropColumn(
                name: "Category",
                table: "ProfileProjects");

            migrationBuilder.DropColumn(
                name: "ImageIds",
                table: "ProfileProjects");

            migrationBuilder.DropColumn(
                name: "MayShowPublicly",
                table: "ProfileProjects");

            migrationBuilder.DropColumn(
                name: "Month",
                table: "ProfileProjects");
        }
    }
}
