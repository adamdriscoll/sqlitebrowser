using System.Collections.ObjectModel;
using System.Data;

namespace SqliteBrowser.Core.Models;

/// <summary>
/// Identifies how a <see cref="TablePage"/>'s rows can be re-located for update/delete purposes.
/// </summary>
public enum RowIdentityKind
{
    /// <summary>No stable identity is available; the page is browse-only.</summary>
    None,

    /// <summary>Rows are identified by SQLite's implicit <c>rowid</c> (exposed via a hidden column).</summary>
    RowId,

    /// <summary>Rows are identified by one or more declared primary key columns (e.g. WITHOUT ROWID tables).</summary>
    PrimaryKey,
}

/// <summary>
/// A single page of a table or view's data, ready for display and (when not read-only) editing.
/// </summary>
public sealed class TablePage
{
    /// <summary>Name of the internal hidden column used to smuggle SQLite's <c>rowid</c> through <see cref="Data"/>.</summary>
    public const string RowIdColumnName = "__rowid__";

    public required string Schema { get; init; }

    public required string TableName { get; init; }

    /// <summary>Whether this page can be persisted back to the database (false for views, or when identity can't be determined).</summary>
    public bool IsReadOnly { get; init; }

    public int Offset { get; init; }

    public int PageSize { get; init; }

    /// <summary>Total number of rows matching the page's filter, ignoring the LIMIT/OFFSET window.</summary>
    public long TotalRows { get; init; }

    /// <summary>The visible (non-identity) columns, in display order.</summary>
    public required IReadOnlyList<ColumnDefinition> Columns { get; init; }

    /// <summary>The page's rows. Contains one hidden <see cref="RowIdColumnName"/> column when <see cref="Identity"/> is <see cref="RowIdentityKind.RowId"/>.</summary>
    public required DataTable Data { get; init; }

    /// <summary>The filter expression (raw SQL boolean expression) applied to this page, if any.</summary>
    public string? Filter { get; init; }

    /// <summary>The ORDER BY clause applied to this page, if any.</summary>
    public string? OrderBy { get; init; }

    /// <summary>How rows in this page can be identified for update/delete.</summary>
    public RowIdentityKind Identity { get; init; } = RowIdentityKind.None;

    /// <summary>
    /// The column name(s) used to identify a row for update/delete: either <see cref="RowIdColumnName"/>
    /// (for <see cref="RowIdentityKind.RowId"/>) or the primary key column names (for <see cref="RowIdentityKind.PrimaryKey"/>).
    /// </summary>
    public IReadOnlyList<string> KeyColumns { get; init; } = Array.Empty<string>();

    public static TablePage Empty(string schema, string tableName) => new()
    {
        Schema = schema,
        TableName = tableName,
        IsReadOnly = true,
        Columns = new ReadOnlyCollection<ColumnDefinition>(Array.Empty<ColumnDefinition>()),
        Data = new DataTable(tableName),
    };
}
