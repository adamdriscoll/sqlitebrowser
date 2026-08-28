using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using SqliteBrowser.App.Models;
using SqliteBrowser.App.Views;

namespace SqliteBrowser.App.Services;

/// <summary>
/// Real implementation of <see cref="IUserInteractionService"/> backed by Avalonia windows and the
/// platform <see cref="IStorageProvider"/>. Resolves its owner window lazily from the classic desktop
/// application lifetime so it can be constructed once and reused for the life of the app.
/// </summary>
public sealed class UserInteractionService : IUserInteractionService
{
    private static Window? OwnerWindow =>
        Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow
            : null;

    private static readonly FilePickerFileType SqliteFileType = new("SQLite Database")
    {
        Patterns = ["*.sqlite3", "*.sqlite", "*.db", "*.db3"],
    };

    private static readonly FilePickerFileType CsvFileType = new("CSV File") { Patterns = ["*.csv"] };

    private static readonly FilePickerFileType JsonFileType = new("JSON File") { Patterns = ["*.json"] };

    private static readonly FilePickerFileType SqlFileType = new("SQL Script") { Patterns = ["*.sql"] };

    private static readonly FilePickerFileType AllFilesType = new("All Files") { Patterns = ["*"] };

    public Task ShowMessageAsync(string title, string message) => MessageDialog.ShowInfoAsync(OwnerWindow, title, message);

    public Task ShowErrorAsync(string title, string message) => MessageDialog.ShowErrorAsync(OwnerWindow, title, message);

    public Task<bool> ConfirmAsync(string title, string message, string confirmText = "OK", string cancelText = "Cancel") =>
        MessageDialog.ShowConfirmAsync(OwnerWindow, title, message, confirmText, cancelText);

    public Task<DirtyCloseChoice> ConfirmDirtyActionAsync(string title, string message) =>
        DirtyChoiceDialog.ShowAsync(OwnerWindow, title, message);

    public Task ShowAboutAsync() => AboutWindow.ShowAsync(OwnerWindow);

    public Task<string?> EditDdlAsync(string title, string initialSql) => DdlEditorDialog.ShowAsync(OwnerWindow, title, initialSql);

    public Task<CsvImportPrompt?> PromptCsvImportOptionsAsync(string suggestedTableName) =>
        CsvImportDialog.ShowAsync(OwnerWindow, suggestedTableName);

    public async Task<string?> PickOpenDatabaseAsync()
    {
        var owner = OwnerWindow;
        if (owner is null)
        {
            return null;
        }

        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Database",
            AllowMultiple = false,
            FileTypeFilter = [SqliteFileType, AllFilesType],
        }).ConfigureAwait(true);

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickCreateDatabaseAsync()
    {
        var owner = OwnerWindow;
        if (owner is null)
        {
            return null;
        }

        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Create Database",
            SuggestedFileName = "database.sqlite3",
            DefaultExtension = "sqlite3",
            FileTypeChoices = [SqliteFileType, AllFilesType],
        }).ConfigureAwait(true);

        return file?.TryGetLocalPath();
    }

    public async Task<string?> PickSaveDatabaseAsAsync(string? suggestedFileName)
    {
        var owner = OwnerWindow;
        if (owner is null)
        {
            return null;
        }

        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Database As",
            SuggestedFileName = suggestedFileName ?? "database.sqlite3",
            DefaultExtension = "sqlite3",
            FileTypeChoices = [SqliteFileType, AllFilesType],
        }).ConfigureAwait(true);

        return file?.TryGetLocalPath();
    }

    public async Task<string?> PickOpenCsvAsync()
    {
        var owner = OwnerWindow;
        if (owner is null)
        {
            return null;
        }

        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import CSV",
            AllowMultiple = false,
            FileTypeFilter = [CsvFileType, AllFilesType],
        }).ConfigureAwait(true);

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickOpenSqlScriptAsync()
    {
        var owner = OwnerWindow;
        if (owner is null)
        {
            return null;
        }

        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import SQL Script",
            AllowMultiple = false,
            FileTypeFilter = [SqlFileType, AllFilesType],
        }).ConfigureAwait(true);

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickSaveCsvAsync(string suggestedFileName)
    {
        var owner = OwnerWindow;
        if (owner is null)
        {
            return null;
        }

        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export CSV",
            SuggestedFileName = suggestedFileName,
            DefaultExtension = "csv",
            FileTypeChoices = [CsvFileType, AllFilesType],
        }).ConfigureAwait(true);

        return file?.TryGetLocalPath();
    }

    public async Task<string?> PickSaveJsonAsync(string suggestedFileName)
    {
        var owner = OwnerWindow;
        if (owner is null)
        {
            return null;
        }

        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export JSON",
            SuggestedFileName = suggestedFileName,
            DefaultExtension = "json",
            FileTypeChoices = [JsonFileType, AllFilesType],
        }).ConfigureAwait(true);

        return file?.TryGetLocalPath();
    }

    public async Task<string?> PickSaveSqlDumpAsync(string suggestedFileName)
    {
        var owner = OwnerWindow;
        if (owner is null)
        {
            return null;
        }

        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export SQL Dump",
            SuggestedFileName = suggestedFileName,
            DefaultExtension = "sql",
            FileTypeChoices = [SqlFileType, AllFilesType],
        }).ConfigureAwait(true);

        return file?.TryGetLocalPath();
    }
}
