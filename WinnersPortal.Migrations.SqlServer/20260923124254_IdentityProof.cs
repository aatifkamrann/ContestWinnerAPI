using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WinnersPortal.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class IdentityProof : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DecisionJson",
                table: "IdentityVerifications",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DecisionReadAtUtc",
                table: "IdentityVerifications",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ProofAttempts",
                table: "IdentityVerifications",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ProofDueAtUtc",
                table: "IdentityVerifications",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProofError",
                table: "IdentityVerifications",
                type: "nvarchar(400)",
                maxLength: 400,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "IdentityDocuments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VerificationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    StorageSetup = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    StorageKey = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RemovedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdentityDocuments", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IdentityVerifications_ProofDueAtUtc",
                table: "IdentityVerifications",
                column: "ProofDueAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_IdentityDocuments_RemovedAtUtc",
                table: "IdentityDocuments",
                column: "RemovedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_IdentityDocuments_UserId",
                table: "IdentityDocuments",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_IdentityDocuments_VerificationId",
                table: "IdentityDocuments",
                column: "VerificationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IdentityDocuments");

            migrationBuilder.DropIndex(
                name: "IX_IdentityVerifications_ProofDueAtUtc",
                table: "IdentityVerifications");

            migrationBuilder.DropColumn(
                name: "DecisionJson",
                table: "IdentityVerifications");

            migrationBuilder.DropColumn(
                name: "DecisionReadAtUtc",
                table: "IdentityVerifications");

            migrationBuilder.DropColumn(
                name: "ProofAttempts",
                table: "IdentityVerifications");

            migrationBuilder.DropColumn(
                name: "ProofDueAtUtc",
                table: "IdentityVerifications");

            migrationBuilder.DropColumn(
                name: "ProofError",
                table: "IdentityVerifications");
        }
    }
}
