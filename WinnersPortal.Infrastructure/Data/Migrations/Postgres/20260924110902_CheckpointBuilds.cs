using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WinnersPortal.Infrastructure.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class CheckpointBuilds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "TreeHasCompose",
                table: "Entries",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RequiresCompose",
                table: "Contests",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "BuildAttempts",
                table: "Checkpoints",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "BuildDueAtUtc",
                table: "Checkpoints",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BuildError",
                table: "Checkpoints",
                type: "character varying(400)",
                maxLength: 400,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "BuildFinishedAtUtc",
                table: "Checkpoints",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BuildLogKey",
                table: "Checkpoints",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BuildLogSetup",
                table: "Checkpoints",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BuildLogTail",
                table: "Checkpoints",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "BuildStartedAtUtc",
                table: "Checkpoints",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BuildStatus",
                table: "Checkpoints",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_Checkpoints_BuildStatus_BuildDueAtUtc",
                table: "Checkpoints",
                columns: new[] { "BuildStatus", "BuildDueAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Checkpoints_BuildStatus_BuildDueAtUtc",
                table: "Checkpoints");

            migrationBuilder.DropColumn(
                name: "TreeHasCompose",
                table: "Entries");

            migrationBuilder.DropColumn(
                name: "RequiresCompose",
                table: "Contests");

            migrationBuilder.DropColumn(
                name: "BuildAttempts",
                table: "Checkpoints");

            migrationBuilder.DropColumn(
                name: "BuildDueAtUtc",
                table: "Checkpoints");

            migrationBuilder.DropColumn(
                name: "BuildError",
                table: "Checkpoints");

            migrationBuilder.DropColumn(
                name: "BuildFinishedAtUtc",
                table: "Checkpoints");

            migrationBuilder.DropColumn(
                name: "BuildLogKey",
                table: "Checkpoints");

            migrationBuilder.DropColumn(
                name: "BuildLogSetup",
                table: "Checkpoints");

            migrationBuilder.DropColumn(
                name: "BuildLogTail",
                table: "Checkpoints");

            migrationBuilder.DropColumn(
                name: "BuildStartedAtUtc",
                table: "Checkpoints");

            migrationBuilder.DropColumn(
                name: "BuildStatus",
                table: "Checkpoints");
        }
    }
}
