using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WinnersPortal.Infrastructure.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class EntryWithdrawnReason : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "WithdrawnReason",
                table: "Entries",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WithdrawnReason",
                table: "Entries");
        }
    }
}
