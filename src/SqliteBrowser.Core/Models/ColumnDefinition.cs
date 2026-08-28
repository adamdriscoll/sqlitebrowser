namespace SqliteBrowser.Core.Models;

/// <summary>
/// Column metadata for a table or view, sourced from <c>PRAGMA table_xinfo</c> so that
/// hidden columns (e.g. generated columns, or columns of virtual tables) are represented.
/// </summary>
/// <param name="Id">The column's <c>cid</c> ordinal position.</param>
/// <param name="Name">The column name.</param>
/// <param name="DataType">The declared type affinity as written in the schema (may be empty).</param>
/// <param name="NotNull">Whether the column has a <c>NOT NULL</c> constraint.</param>
/// <param name="DefaultValue">The literal default value expression, if any.</param>
/// <param name="PrimaryKeyOrder">
/// 1-based position within the table's primary key, or 0 if the column is not part of the primary key.
/// </param>
/// <param name="Hidden">Whether the column is hidden (generated/virtual-table bookkeeping column).</param>
public sealed record ColumnDefinition(
    int Id,
    string Name,
    string DataType,
    bool NotNull,
    string? DefaultValue,
    int PrimaryKeyOrder,
    bool Hidden);
