using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WinnersPortal.Migrations.SqlServer
{
    /// <summary>
    /// The seven JSON columns become nvarchar(max), so that SQL Server 2022
    /// runs the portal. Initial now creates them that way, and on a database
    /// it built this migration finds nothing to do; on a database built
    /// before it, on SQL Server 2025's json type, this is the conversion —
    /// the text is the same on either side.
    /// </summary>
    public partial class JsonColumnsAsText : Migration
    {
        public static readonly (string Table, string Column)[] Columns =
        [
            ("WebhookDeliveries", "Payload"),
            ("Profiles", "SecondaryCategories"),
            ("Profiles", "PreferredProjectTypes"),
            ("Profiles", "PreferredDurations"),
            ("ProfileProjects", "ImageIds"),
            ("Applications", "PortfolioJson"),
            ("Applications", "EvaluationJson"),
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var (table, column) in Columns)
                migrationBuilder.Sql(Convert(table, column));
        }

        /// <summary>
        /// One column's conversion, guarded by its current type so that a
        /// database Initial built as text is left alone. SQL Server allows
        /// no implicit conversion from json, and ALTER COLUMN is one, so a
        /// text column is added beside the old one, filled with an explicit
        /// CAST, and takes the old column's name once that is dropped. The
        /// column ends up last in the table's order, which nothing reads by.
        /// </summary>
        public static string Convert(string table, string column) => $"""
            IF EXISTS (SELECT 1 FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
                       WHERE c.object_id = OBJECT_ID(N'[{table}]') AND c.name = N'{column}' AND t.name = N'json')
            BEGIN
                ALTER TABLE [{table}] ADD [{column}__text] nvarchar(max) NULL;
                EXEC(N'UPDATE [{table}] SET [{column}__text] = CAST([{column}] AS nvarchar(max));');
                ALTER TABLE [{table}] DROP COLUMN [{column}];
                EXEC sp_rename N'[{table}].[{column}__text]', N'{column}', N'COLUMN';
                ALTER TABLE [{table}] ALTER COLUMN [{column}] nvarchar(max) NOT NULL;
            END
            """;

        /// <summary>
        /// Nothing to undo: nvarchar(max) is what Initial creates, and the
        /// json type this would return to does not exist on SQL Server 2022.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
