using SqliteBrowser.App.Models;
using SqliteBrowser.App.Services;

namespace SqliteBrowser.App.Tests;

/// <summary>
/// A scriptable <see cref="IUserInteractionService"/> test double. Never shows a real dialog or
/// native file picker (which would hang under the headless test platform waiting for input);
/// instead every method returns a value queued by the test, or a sensible default, and every call
/// is recorded so tests can assert on which dialogs were shown.
/// </summary>
public sealed class FakeUserInteractionService : IUserInteractionService
{
    public List<string> Calls { get; } = [];

    public string? NextOpenDatabasePath { get; set; }
    public string? NextCreateDatabasePath { get; set; }
    public string? NextSaveDatabaseAsPath { get; set; }
    public string? NextOpenCsvPath { get; set; }
    public string? NextOpenSqlScriptPath { get; set; }
    public string? NextSaveCsvPath { get; set; }
    public string? NextSaveJsonPath { get; set; }
    public string? NextSaveSqlDumpPath { get; set; }
    public string? NextDdlResult { get; set; }
    public ValueEditResult? NextValueEditResult { get; set; }
    public CsvImportPrompt? NextCsvImportPrompt { get; set; }
    public DirtyCloseChoice NextDirtyChoice { get; set; } = DirtyCloseChoice.Cancel;
    public bool NextConfirmResult { get; set; } = true;

    public Task ShowMessageAsync(string title, string message)
    {
        Calls.Add($"Message:{title}");
        return Task.CompletedTask;
    }

    public Task ShowErrorAsync(string title, string message)
    {
        Calls.Add($"Error:{title}:{message}");
        return Task.CompletedTask;
    }

    public Task<bool> ConfirmAsync(string title, string message, string confirmText = "OK", string cancelText = "Cancel")
    {
        Calls.Add($"Confirm:{title}");
        return Task.FromResult(NextConfirmResult);
    }

    public Task<DirtyCloseChoice> ConfirmDirtyActionAsync(string title, string message)
    {
        Calls.Add($"ConfirmDirty:{title}");
        return Task.FromResult(NextDirtyChoice);
    }

    public Task ShowAboutAsync()
    {
        Calls.Add("About");
        return Task.CompletedTask;
    }

    public Task<string?> EditDdlAsync(string title, string initialSql)
    {
        Calls.Add($"EditDdl:{title}");
        return Task.FromResult(NextDdlResult);
    }

    public Task<ValueEditResult?> EditValueAsync(string title, object? initialValue, bool isReadOnly)
    {
        Calls.Add($"EditValue:{title}:{isReadOnly}");
        return Task.FromResult(NextValueEditResult);
    }

    public Task<CsvImportPrompt?> PromptCsvImportOptionsAsync(string suggestedTableName)
    {
        Calls.Add($"PromptCsvImport:{suggestedTableName}");
        return Task.FromResult(NextCsvImportPrompt);
    }

    public Task<string?> PickOpenDatabaseAsync()
    {
        Calls.Add("PickOpenDatabase");
        return Task.FromResult(NextOpenDatabasePath);
    }

    public Task<string?> PickCreateDatabaseAsync()
    {
        Calls.Add("PickCreateDatabase");
        return Task.FromResult(NextCreateDatabasePath);
    }

    public Task<string?> PickSaveDatabaseAsAsync(string? suggestedFileName)
    {
        Calls.Add("PickSaveDatabaseAs");
        return Task.FromResult(NextSaveDatabaseAsPath);
    }

    public Task<string?> PickOpenCsvAsync()
    {
        Calls.Add("PickOpenCsv");
        return Task.FromResult(NextOpenCsvPath);
    }

    public Task<string?> PickOpenSqlScriptAsync()
    {
        Calls.Add("PickOpenSqlScript");
        return Task.FromResult(NextOpenSqlScriptPath);
    }

    public Task<string?> PickSaveCsvAsync(string suggestedFileName)
    {
        Calls.Add("PickSaveCsv");
        return Task.FromResult(NextSaveCsvPath);
    }

    public Task<string?> PickSaveJsonAsync(string suggestedFileName)
    {
        Calls.Add("PickSaveJson");
        return Task.FromResult(NextSaveJsonPath);
    }

    public Task<string?> PickSaveSqlDumpAsync(string suggestedFileName)
    {
        Calls.Add("PickSaveSqlDump");
        return Task.FromResult(NextSaveSqlDumpPath);
    }
}
