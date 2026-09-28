using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace WinnersPortal.Infrastructure.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class ActivityLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ActivityEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Visitor = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    Kind = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Method = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Path = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Action = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Page = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Ip = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    UserAgent = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    AtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActivityEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ActivityEvents_AtUtc",
                table: "ActivityEvents",
                column: "AtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ActivityEvents_UserId_AtUtc",
                table: "ActivityEvents",
                columns: new[] { "UserId", "AtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ActivityEvents_Visitor_AtUtc",
                table: "ActivityEvents",
                columns: new[] { "Visitor", "AtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ActivityEvents");
        }
    }
}
