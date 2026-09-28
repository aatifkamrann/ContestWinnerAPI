using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using WinnersPortal.Infrastructure.Data;

#nullable disable

namespace WinnersPortal.Migrations.SqlServer
{
    /// <summary>
    /// Hand-written, not scaffolded: the model has no column for it. What
    /// the generated tsvector does on Postgres, a full-text index over the
    /// same two columns does here, and ContestSearch reads it with FREETEXT.
    /// Full-text DDL refuses to run inside a user transaction, so each
    /// statement runs bare — and the catalog is made only where it is not
    /// already, so the migration is safe to apply to a database an operator
    /// prepared by hand. No Designer file: the model does not change.
    /// </summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20260918235900_ContestFullText")]
    public partial class ContestFullText : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "IF NOT EXISTS (SELECT 1 FROM sys.fulltext_catalogs WHERE name = N'WinnersPortal') "
                + "CREATE FULLTEXT CATALOG [WinnersPortal] AS DEFAULT;",
                suppressTransaction: true);
            migrationBuilder.Sql(
                "CREATE FULLTEXT INDEX ON [Contests] ([Title] LANGUAGE 1033, [BriefMarkdown] LANGUAGE 1033) "
                + "KEY INDEX [PK_Contests] ON [WinnersPortal] WITH CHANGE_TRACKING AUTO;",
                suppressTransaction: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FULLTEXT INDEX ON [Contests];", suppressTransaction: true);
            migrationBuilder.Sql("DROP FULLTEXT CATALOG [WinnersPortal];", suppressTransaction: true);
        }
    }
}
