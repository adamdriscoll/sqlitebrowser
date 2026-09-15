using SqliteBrowser.Core.Models;

namespace SqliteBrowser.App.Models;

/// <summary>The user's choice when asked to close/revert a database that has unsaved (uncommitted) changes.</summary>
public enum DirtyCloseChoice
{
    /// <summary>Commit pending changes, then proceed.</summary>
    SaveAndProceed,

    /// <summary>Discard (revert) pending changes, then proceed.</summary>
    DiscardAndProceed,

    /// <summary>Abort the operation; leave everything as-is.</summary>
    Cancel,
}

/// <summary>The result of the CSV import options prompt: the destination table plus parse/import options.</summary>
public sealed record CsvImportPrompt(string TableName, CsvImportOptions Options);

/// <summary>The SQLite storage class selected in the rich value editor.</summary>
public enum SqliteStorageClass
{
    Null,
    Integer,
    Real,
    Text,
    Blob,
}

/// <summary>A confirmed value edit. The wrapper distinguishes cancellation from an SQL NULL value.</summary>
public sealed record ValueEditResult(object Value);
