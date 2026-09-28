using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WinnersPortal.Infrastructure.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class ActivityDetail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Detail",
                table: "ActivityEvents",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Detail",
                table: "ActivityEvents");
        }
    }
}
