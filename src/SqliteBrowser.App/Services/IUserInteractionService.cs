using SqliteBrowser.App.Models;

namespace SqliteBrowser.App.Services;

/// <summary>
/// Abstraction over every native dialog / file picker the application shows. Kept as a single
/// interface (rather than scattering <c>Window</c>/<c>StorageProvider</c> calls through view-models)
/// so view-model logic stays testable: unit and headless UI tests exercise the underlying async
/// methods on the view-models directly and never need a real native picker to be shown.
/// </summary>
public interface IUserInteractionService
{
    /// <summary>Shows a simple informational message.</summary>
    Task ShowMessageAsync(string title, string message);

    /// <summary>Shows an error message. Distinct from <see cref="ShowMessageAsync"/> only in presentation (e.g. styling/icon).</summary>
    Task ShowErrorAsync(string title, string message);

    /// <summary>Asks a yes/no question, returning <c>true</c> when the affirmative action was chosen.</summary>
    Task<bool> ConfirmAsync(string title, string message, string confirmText = "OK", string cancelText = "Cancel");

    /// <summary>Asks the user how to proceed when closing/reverting a database that has uncommitted changes.</summary>
    Task<DirtyCloseChoice> ConfirmDirtyActionAsync(string title, string message);

    /// <summary>Shows the About dialog (dual license notice + Ahtola provider warning).</summary>
    Task ShowAboutAsync();

    /// <summary>
    /// Shows a raw-SQL/DDL editor dialog seeded with <paramref name="initialSql"/>. Returns the
    /// (possibly edited) text if the user confirmed, or <c>null</c> if they cancelled.
    /// </summary>
    Task<string?> EditDdlAsync(string title, string initialSql);

    /// <summary>Inspects or edits one cell value without implicitly changing its SQLite storage class.</summary>
    Task<ValueEditResult?> EditValueAsync(string title, object? initialValue, bool isReadOnly);

    /// <summary>Prompts for CSV import options (destination table name, delimiter, header, type inference).</summary>
    Task<CsvImportPrompt?> PromptCsvImportOptionsAsync(string suggestedTableName);

    Task<string?> PickOpenDatabaseAsync();

    Task<string?> PickCreateDatabaseAsync();

    Task<string?> PickSaveDatabaseAsAsync(string? suggestedFileName);

    Task<string?> PickOpenCsvAsync();

    Task<string?> PickOpenSqlScriptAsync();

    Task<string?> PickSaveCsvAsync(string suggestedFileName);

    Task<string?> PickSaveJsonAsync(string suggestedFileName);

    Task<string?> PickSaveSqlDumpAsync(string suggestedFileName);
}
