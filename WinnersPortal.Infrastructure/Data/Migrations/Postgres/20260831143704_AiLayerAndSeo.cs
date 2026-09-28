using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WinnersPortal.Infrastructure.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AiLayerAndSeo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MetaDescription",
                table: "Contests",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MetaTitle",
                table: "Contests",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AiArtifacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Feature = table.Column<int>(type: "integer", nullable: false),
                    SubjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    InputHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    OutputJson = table.Column<string>(type: "text", nullable: true),
                    Note = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    Provider = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Model = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiArtifacts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiArtifacts_Feature_SubjectId",
                table: "AiArtifacts",
                columns: new[] { "Feature", "SubjectId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AiArtifacts_Status_CreatedAtUtc",
                table: "AiArtifacts",
                columns: new[] { "Status", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiArtifacts");

            migrationBuilder.DropColumn(
                name: "MetaDescription",
                table: "Contests");

            migrationBuilder.DropColumn(
                name: "MetaTitle",
                table: "Contests");
        }
    }
}
