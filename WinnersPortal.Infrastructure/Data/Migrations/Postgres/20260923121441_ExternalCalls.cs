using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WinnersPortal.Infrastructure.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class ExternalCalls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DurationMs",
                table: "ActivityEvents",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Request",
                table: "ActivityEvents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Response",
                table: "ActivityEvents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Service",
                table: "ActivityEvents",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DurationMs",
                table: "ActivityEvents");

            migrationBuilder.DropColumn(
                name: "Request",
                table: "ActivityEvents");

            migrationBuilder.DropColumn(
                name: "Response",
                table: "ActivityEvents");

            migrationBuilder.DropColumn(
                name: "Service",
                table: "ActivityEvents");
        }
    }
}
