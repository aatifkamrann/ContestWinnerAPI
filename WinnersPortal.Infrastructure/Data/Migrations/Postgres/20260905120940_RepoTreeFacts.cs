using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WinnersPortal.Infrastructure.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class RepoTreeFacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TreeFileCount",
                table: "Entries",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "TreeHasCi",
                table: "Entries",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "TreeHasReadme",
                table: "Entries",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "TreeHasTests",
                table: "Entries",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "TreeReadAtUtc",
                table: "Entries",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TreeFileCount",
                table: "Entries");

            migrationBuilder.DropColumn(
                name: "TreeHasCi",
                table: "Entries");

            migrationBuilder.DropColumn(
                name: "TreeHasReadme",
                table: "Entries");

            migrationBuilder.DropColumn(
                name: "TreeHasTests",
                table: "Entries");

            migrationBuilder.DropColumn(
                name: "TreeReadAtUtc",
                table: "Entries");
        }
    }
}
