using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WinnersPortal.Infrastructure.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class EntryRemoval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AccessRevokedAtUtc",
                table: "Entries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RemovedAtUtc",
                table: "Entries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RemovedByUserId",
                table: "Entries",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RemovedReason",
                table: "Entries",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RepoAccessEndsAtUtc",
                table: "Entries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Entries_Status_RepoAccessEndsAtUtc",
                table: "Entries",
                columns: new[] { "Status", "RepoAccessEndsAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Entries_Status_RepoAccessEndsAtUtc",
                table: "Entries");

            migrationBuilder.DropColumn(
                name: "AccessRevokedAtUtc",
                table: "Entries");

            migrationBuilder.DropColumn(
                name: "RemovedAtUtc",
                table: "Entries");

            migrationBuilder.DropColumn(
                name: "RemovedByUserId",
                table: "Entries");

            migrationBuilder.DropColumn(
                name: "RemovedReason",
                table: "Entries");

            migrationBuilder.DropColumn(
                name: "RepoAccessEndsAtUtc",
                table: "Entries");
        }
    }
}
