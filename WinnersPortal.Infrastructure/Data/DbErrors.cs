using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace WinnersPortal.Infrastructure.Data;

/// <summary>
/// What a database's refusal means, whichever database it was. The one
/// refusal the services act on is a unique index hit: it is how a race of
/// two submits, two claims or two redeliveries becomes one row and one
/// handled exception rather than a duplicate. Postgres names it by SQLSTATE,
/// SQL Server by error number; the services ask here and never either.
/// </summary>
public static class DbErrors
{
    public const string PostgresUniqueViolation = "23505";

    public static bool IsUniqueViolation(DbUpdateException e) => IsUniqueViolation(e.InnerException);

    public static bool IsUniqueViolation(Exception? inner) => inner switch
    {
        Npgsql.PostgresException pg => pg.SqlState == PostgresUniqueViolation,
        SqlException sql => sql.Errors.Cast<SqlError>().Any(x => IsSqlServerUniqueViolation(x.Number)),
        _ => false,
    };

    /// <summary>2601 is a unique index, 2627 a unique constraint; the model uses both.</summary>
    public static bool IsSqlServerUniqueViolation(int number) => number is 2601 or 2627;
}
