namespace WinnersPortal.Infrastructure.Data;

/// <summary>
/// A stored procedure the portal calls on SQL Server: its name in
/// <c>[dbo]</c>, and its definition — the <c>CREATE OR ALTER PROCEDURE</c>
/// script of the same name under <c>Data/Procedures</c>, embedded in this
/// assembly and installed by <see cref="StoredProcedures"/> after the
/// migrations. It is the only thing <see cref="Sql"/> will run: a command
/// is a procedure and its parameters, never text, so no value a caller
/// supplies can reach the server as SQL.
/// </summary>
public sealed class Procedure
{
    internal Procedure(string name)
    {
        Name = name;
    }

    /// <summary>The procedure's own name, which is also its script's file name.</summary>
    public string Name { get; }

    /// <summary>The name as a command calls it.</summary>
    public string QualifiedName => $"[dbo].[{Name}]";

    /// <summary>The script that creates or replaces it.</summary>
    public string Definition => StoredProcedures.Definition(this);

    public override string ToString() => QualifiedName;
}
