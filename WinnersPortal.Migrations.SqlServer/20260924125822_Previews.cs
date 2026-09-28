using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WinnersPortal.Migrations.SqlServer
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
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EntryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CheckpointId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Ref = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Sha = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    DueAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RequestedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastSeenAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    StoppedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    StopRequested = table.Column<bool>(type: "bit", nullable: false),
                    StopReason = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Error = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    LogTail = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Previews", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Previews_Checkpoints_CheckpointId",
                        column: x => x.CheckpointId,
                        principalTable: "Checkpoints",
                        principalColumn: "Id");
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
                filter: "[CheckpointId] IS NOT NULL");

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
