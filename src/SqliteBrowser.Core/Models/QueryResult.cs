using System.Data;

namespace SqliteBrowser.Core.Models;

/// <summary>
/// The result of executing arbitrary SQL: the last result set produced (if any), the number of
/// rows affected across all statements, how long execution took, and an optional status message.
/// </summary>
public sealed class QueryResult
{
    /// <summary>The last result set produced by the executed SQL, or <c>null</c> if no statement produced rows.</summary>
    public DataTable? Data { get; init; }

    /// <summary>Total rows affected (inserted/updated/deleted) across all executed statements.</summary>
    public int RowsAffected { get; init; }

    /// <summary>Wall-clock time spent executing the SQL.</summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary>A human-readable status message (e.g. row/column counts), never an unhandled error.</summary>
    public string? Message { get; init; }
}
