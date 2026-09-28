using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WinnersPortal.Infrastructure.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class SetupTests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SetupTests",
                columns: table => new
                {
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SetupId = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Ok = table.Column<bool>(type: "boolean", nullable: false),
                    Detail = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    TestedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TestedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SetupTests", x => new { x.Kind, x.SetupId });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SetupTests");
        }
    }
}
