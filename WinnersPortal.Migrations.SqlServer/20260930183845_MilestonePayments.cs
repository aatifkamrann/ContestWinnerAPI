using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WinnersPortal.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class MilestonePayments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "Opportunities",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "Amount",
                table: "Milestones",
                type: "decimal(12,2)",
                precision: 12,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ApprovedAtUtc",
                table: "Checkpoints",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ChangesNote",
                table: "Checkpoints",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ChangesRequestedAtUtc",
                table: "Checkpoints",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PaidAtUtc",
                table: "Checkpoints",
                type: "datetimeoffset",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Kind",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "Amount",
                table: "Milestones");

            migrationBuilder.DropColumn(
                name: "ApprovedAtUtc",
                table: "Checkpoints");

            migrationBuilder.DropColumn(
                name: "ChangesNote",
                table: "Checkpoints");

            migrationBuilder.DropColumn(
                name: "ChangesRequestedAtUtc",
                table: "Checkpoints");

            migrationBuilder.DropColumn(
                name: "PaidAtUtc",
                table: "Checkpoints");
        }
    }
}
