using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WinnersPortal.Infrastructure.Data.Migrations.Postgres
{
    /// <summary>
    /// A withdrawal now gives the application an answer of its own. Nothing
    /// in the schema moves — the status is stored as its number — but a
    /// selection whose entry was already withdrawn still reads Selected, so
    /// those rows are brought to what a withdrawal leaves from now on:
    /// Withdrawn, stamped with when it happened. Application Selected is 1
    /// and Withdrawn 4; entry Withdrawn is 1.
    /// </summary>
    public partial class ApplicationWithdrawn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                UPDATE ""Applications"" AS a
                SET ""Status"" = 4, ""DecidedAtUtc"" = e.""WithdrawnAtUtc""
                FROM ""Entries"" AS e
                WHERE a.""EntryId"" = e.""Id"" AND a.""Status"" = 1 AND e.""Status"" = 1;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The selection time a withdrawal overwrote is not kept; the
            // status goes back, the stamp stays at the withdrawal.
            migrationBuilder.Sql(@"UPDATE ""Applications"" SET ""Status"" = 1 WHERE ""Status"" = 4;");
        }
    }
}
