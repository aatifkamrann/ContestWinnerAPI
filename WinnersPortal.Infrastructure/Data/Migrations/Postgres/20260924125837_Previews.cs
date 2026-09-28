using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WinnersPortal.Infrastructure.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class Previews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Previews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EntryId = table.Column<Guid>(type: "uuid", nullable: false),
                    CheckpointId = table.Column<Guid>(type: "uuid", nullable: true),
                    Ref = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Sha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    DueAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RequestedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastSeenAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    StoppedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    StopRequested = table.Column<bool>(type: "boolean", nullable: false),
                    StopReason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Error = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    LogTail = table.Column<string>(type: "text", nullable: true),
                    Notes = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Previews", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Previews_Checkpoints_CheckpointId",
                        column: x => x.CheckpointId,
                        principalTable: "Checkpoints",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Previews_Entries_EntryId",
                        column: x => x.EntryId,
                        principalTable: "Entries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Previews_CheckpointId",
                table: "Previews",
                column: "CheckpointId",
                unique: true,
                filter: "\"CheckpointId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Previews_EntryId",
                table: "Previews",
                column: "EntryId");

            migrationBuilder.CreateIndex(
                name: "IX_Previews_Status_DueAtUtc",
                table: "Previews",
                columns: new[] { "Status", "DueAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Previews");
        }
    }
}
