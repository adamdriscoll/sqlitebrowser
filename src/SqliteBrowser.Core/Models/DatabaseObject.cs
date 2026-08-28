namespace SqliteBrowser.Core.Models;

/// <summary>
/// Describes a single schema object (table, view, index or trigger) discovered via
/// <c>sqlite_master</c>/<c>sqlite_schema</c> for one of the databases attached to a session
/// (as enumerated by <c>PRAGMA database_list</c>).
/// </summary>
/// <param name="Schema">The attached database/schema name (e.g. "main", "temp", or an ATTACH alias).</param>
/// <param name="Type">The object type: "table", "view", "index" or "trigger".</param>
/// <param name="Name">The object's own name.</param>
/// <param name="TableName">
/// The table the object belongs to. Equal to <paramref name="Name"/> for tables and views,
/// and the owning table for indexes/triggers.
/// </param>
/// <param name="Sql">The original <c>CREATE ...</c> statement, or <c>null</c> for internal/implicit objects.</param>
public sealed record DatabaseObject(string Schema, string Type, string Name, string TableName, string? Sql);
