using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WinnersPortal.Infrastructure.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class ContestCategoriesAndSkills : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Category",
                table: "Contests",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MinMeritScore",
                table: "Contests",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Subcategory",
                table: "Contests",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ContestSkills",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ContestId = table.Column<Guid>(type: "uuid", nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Key = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContestSkills", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ContestSkills_Contests_ContestId",
                        column: x => x.ContestId,
                        principalTable: "Contests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Contests_Category_Status_PublishedAtUtc",
                table: "Contests",
                columns: new[] { "Category", "Status", "PublishedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ContestSkills_ContestId_Order",
                table: "ContestSkills",
                columns: new[] { "ContestId", "Order" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ContestSkills");

            migrationBuilder.DropIndex(
                name: "IX_Contests_Category_Status_PublishedAtUtc",
                table: "Contests");

            migrationBuilder.DropColumn(
                name: "Category",
                table: "Contests");

            migrationBuilder.DropColumn(
                name: "MinMeritScore",
                table: "Contests");

            migrationBuilder.DropColumn(
                name: "Subcategory",
                table: "Contests");
        }
    }
}
