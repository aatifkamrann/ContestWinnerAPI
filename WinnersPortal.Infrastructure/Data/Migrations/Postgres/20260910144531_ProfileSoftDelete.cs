using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WinnersPortal.Infrastructure.Data.Migrations.Postgres
{
    /// <summary>
    /// A profile is closed rather than dropped when the account it belongs
    /// to is erased.
    ///
    /// Three columns say so: that it happened, when, and which
    /// administrator did it — the last as a foreign key that nulls itself
    /// if that administrator's own account is later removed outright, so an
    /// audit trail can never block a deletion. Existing rows default to
    /// false, which is what they are: nobody's profile has been deleted
    /// under the old behaviour, because the old behaviour deleted the
    /// person's payment rows and left the rest of the portfolio in place.
    ///
    /// Nothing reads a closed profile — the query filter on <c>Profile</c>
    /// takes it out of every query the portal makes — so this is a
    /// retention decision written into the schema, not a visibility one.
    /// </summary>
    public partial class ProfileSoftDelete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedAtUtc",
                table: "Profiles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DeletedByUserId",
                table: "Profiles",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "Profiles",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_Profiles_DeletedByUserId",
                table: "Profiles",
                column: "DeletedByUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_Profiles_Users_DeletedByUserId",
                table: "Profiles",
                column: "DeletedByUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Profiles_Users_DeletedByUserId",
                table: "Profiles");

            migrationBuilder.DropIndex(
                name: "IX_Profiles_DeletedByUserId",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "DeletedAtUtc",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "DeletedByUserId",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                table: "Profiles");
        }
    }
}
