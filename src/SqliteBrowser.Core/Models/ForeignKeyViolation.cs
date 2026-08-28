namespace SqliteBrowser.Core.Models;

/// <summary>A single violation row reported by <c>PRAGMA foreign_key_check</c>.</summary>
/// <param name="Table">The child table containing the violating row.</param>
/// <param name="RowId">The violating row's rowid, or <c>null</c> for WITHOUT ROWID tables.</param>
/// <param name="Parent">The referenced parent table.</param>
/// <param name="ForeignKeyId">The index of the failing foreign key constraint on <paramref name="Table"/>.</param>
public sealed record ForeignKeyViolation(string Table, long? RowId, string Parent, int ForeignKeyId);
