using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WinnersPortal.Infrastructure.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class ConfirmedContact : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CodeAttempts",
                table: "Users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CodeIssuedAtUtc",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EmailCodeHash",
                table: "Users",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EmailConfirmedAtUtc",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Phone",
                table: "Users",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PhoneCodeHash",
                table: "Users",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PhoneConfirmedAtUtc",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            // Every account that exists on the day this ships was made
            // before codes existed — registered, invited, seeded or set up
            // by hand — and has been signing in since. Stamping them as
            // confirmed at their creation keeps the gate from locking the
            // whole membership out of a portal they were using an hour ago.
            migrationBuilder.Sql(
                """UPDATE "Users" SET "EmailConfirmedAtUtc" = "CreatedAtUtc" WHERE "EmailConfirmedAtUtc" IS NULL;""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CodeAttempts",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "CodeIssuedAtUtc",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "EmailCodeHash",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "EmailConfirmedAtUtc",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "Phone",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "PhoneCodeHash",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "PhoneConfirmedAtUtc",
                table: "Users");
        }
    }
}
