using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WinnersPortal.Infrastructure.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class ProjectOutcome : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Outcome",
                table: "ProfileProjects",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Outcome",
                table: "ProfileProjects");
        }
    }
}
