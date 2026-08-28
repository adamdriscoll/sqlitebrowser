namespace SqliteBrowser.Core.Models;

/// <summary>
/// The result of a single <c>PRAGMA</c> read, capturing its (possibly multi-row, multi-column) result set.
/// Most pragmas return a single scalar row/column (see <see cref="ScalarValue"/>), but some
/// (e.g. <c>table_info</c>) return one row per column.
/// </summary>
/// <param name="Name">The pragma name that was queried.</param>
/// <param name="Columns">The result set's column names.</param>
/// <param name="Rows">The result set's rows, each with one value per column in <see cref="Columns"/>.</param>
public sealed record PragmaValue(string Name, IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<object?>> Rows)
{
    /// <summary>
    /// The single scalar value when the pragma returned exactly one row with one column; otherwise <c>null</c>.
    /// </summary>
    public object? ScalarValue => Rows.Count == 1 && Columns.Count == 1 ? Rows[0][0] : null;
}
