namespace SqliteBrowser.Core.Models;

/// <summary>Options controlling how a CSV file/stream is parsed and imported into a table.</summary>
public sealed class CsvImportOptions
{
    /// <summary>Field delimiter character. Defaults to comma.</summary>
    public char Delimiter { get; init; } = ',';

    /// <summary>Whether the first record is a header row naming the columns.</summary>
    public bool HasHeader { get; init; } = true;

    /// <summary>
    /// Whether to infer INTEGER/REAL/TEXT column affinities from the data when creating a new table.
    /// Ignored when the target table already exists.
    /// </summary>
    public bool InferTypes { get; init; } = true;

    /// <summary>Whether to create the destination table if it does not already exist.</summary>
    public bool CreateTable { get; init; } = true;
}

/// <summary>Options controlling how a result set is rendered to CSV.</summary>
public sealed class CsvExportOptions
{
    /// <summary>Field delimiter character. Defaults to comma.</summary>
    public char Delimiter { get; init; } = ',';

    /// <summary>Whether to write a header row naming the columns.</summary>
    public bool IncludeHeader { get; init; } = true;
}

/// <summary>The outcome of a CSV import operation.</summary>
/// <param name="Columns">The column names of the destination table, in order.</param>
/// <param name="RowsImported">Number of data rows imported.</param>
/// <param name="Elapsed">Wall-clock time spent importing.</param>
/// <param name="TableCreated">Whether a new table was created to hold the data.</param>
public sealed record CsvImportResult(IReadOnlyList<string> Columns, int RowsImported, TimeSpan Elapsed, bool TableCreated);
