using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WinnersPortal.Infrastructure.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AiTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "InputTokens",
                table: "AiUsages",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "OutputTokens",
                table: "AiUsages",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "InputTokens",
                table: "AiArtifacts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OutputTokens",
                table: "AiArtifacts",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AiSpends",
                columns: table => new
                {
                    Day = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Feature = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Provider = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Model = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Calls = table.Column<int>(type: "integer", nullable: false),
                    InputTokens = table.Column<long>(type: "bigint", nullable: false),
                    OutputTokens = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiSpends", x => new { x.Day, x.Feature, x.Provider, x.Model });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiSpends");

            migrationBuilder.DropColumn(
                name: "InputTokens",
                table: "AiUsages");

            migrationBuilder.DropColumn(
                name: "OutputTokens",
                table: "AiUsages");

            migrationBuilder.DropColumn(
                name: "InputTokens",
                table: "AiArtifacts");

            migrationBuilder.DropColumn(
                name: "OutputTokens",
                table: "AiArtifacts");
        }
    }
}
