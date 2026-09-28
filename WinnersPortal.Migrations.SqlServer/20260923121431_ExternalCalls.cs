using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WinnersPortal.Migrations.SqlServer
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
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Request",
                table: "ActivityEvents",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Response",
                table: "ActivityEvents",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Service",
                table: "ActivityEvents",
                type: "nvarchar(16)",
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
