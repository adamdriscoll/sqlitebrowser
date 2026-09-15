using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqliteBrowser.App.Models;
using SqliteBrowser.App.Services;
using SqliteBrowser.Core.Models;
using SqliteBrowser.Core.Services;

namespace SqliteBrowser.App.ViewModels;

/// <summary>
/// The application's root view-model. Owns the current <see cref="DatabaseSession"/> (if any) and
/// hands it out to the four tab view-models. Every command that would normally show a native dialog
/// (open/save pickers, confirmations, DDL editor, CSV import options) is a thin wrapper around a
/// plain async method of the same intent, so tests can exercise the real logic — opening a database,
/// committing/reverting, running a tool, importing/exporting — without needing an actual dialog.
/// </summary>
public sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly IUserInteractionService _ui;
    private DatabaseSession? _session;

    public MainWindowViewModel()
        : this(new UserInteractionService())
    {
    }

    public MainWindowViewModel(IUserInteractionService ui)
    {
        _ui = ui;
        BrowseData = new BrowseDataViewModel(ui);
        Structure.OnMutated = RefreshDirtyState;
        BrowseData.OnMutated = RefreshDirtyState;
        ExecuteSql.OnMutated = RefreshDirtyState;
        Pragmas.OnMutated = RefreshDirtyState;
    }

    public StructureViewModel Structure { get; } = new();

    public BrowseDataViewModel BrowseData { get; }

    public ExecuteSqlViewModel ExecuteSql { get; } = new();

    public PragmasViewModel Pragmas { get; } = new();

    public bool IsDatabaseOpen => _session is not null;

    public bool IsReadOnly => _session?.IsReadOnly ?? false;

    public bool IsDirty => _session?.IsDirty ?? false;

    public string? CurrentPath => _session?.Path;

    public string WindowTitle => _session is null
        ? "SQLite Browser"
        : $"{(IsDirty ? "*" : string.Empty)}{(_session.IsInMemory ? "(in-memory)" : _session.Path)}{(IsReadOnly ? " [read-only]" : string.Empty)} \u2014 SQLite Browser";

    public string StatusPathText => _session is null ? "No database open." : (_session.IsInMemory ? ":memory:" : _session.Path!);

    // --------------------------------------------------------------------------------------
    // Session attach / detach plumbing
    // --------------------------------------------------------------------------------------

    private void AttachSession(DatabaseSession session)
    {
        var previous = _session;
        _session = session;
        Structure.AttachSession(session);
        BrowseData.AttachSession(session);
        ExecuteSql.AttachSession(previous, session);
        Pragmas.AttachSession(session);
        RaiseSessionChanged();
    }

    private void DetachSession()
    {
        var previous = _session;
        _session = null;
        Structure.AttachSession(null);
        BrowseData.AttachSession(null);
        ExecuteSql.AttachSession(previous, null);
        Pragmas.AttachSession(null);
        RaiseSessionChanged();
    }

    private void RaiseSessionChanged()
    {
        OnPropertyChanged(nameof(IsDatabaseOpen));
        OnPropertyChanged(nameof(IsReadOnly));
        OnPropertyChanged(nameof(CurrentPath));
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(StatusPathText));
        RefreshDirtyState();
    }

    private void RefreshDirtyState()
    {
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(WindowTitle));
        CloseCommand.NotifyCanExecuteChanged();
        SaveAsCommand.NotifyCanExecuteChanged();
        CommitCommand.NotifyCanExecuteChanged();
        RevertCommand.NotifyCanExecuteChanged();
        VacuumCommand.NotifyCanExecuteChanged();
        OptimizeCommand.NotifyCanExecuteChanged();
        IntegrityCheckCommand.NotifyCanExecuteChanged();
        QuickCheckCommand.NotifyCanExecuteChanged();
        ForeignKeyCheckCommand.NotifyCanExecuteChanged();
        ImportCsvCommand.NotifyCanExecuteChanged();
        ImportSqlScriptCommand.NotifyCanExecuteChanged();
        ExportTableCsvCommand.NotifyCanExecuteChanged();
        ExportTableJsonCommand.NotifyCanExecuteChanged();
        ExportQueryCsvCommand.NotifyCanExecuteChanged();
        ExportQueryJsonCommand.NotifyCanExecuteChanged();
        ExportSqlDumpCommand.NotifyCanExecuteChanged();
    }

    private bool CanOperateOnOpenDatabase => _session is not null;

    private bool CanWriteToDatabase => _session is { IsReadOnly: false };

    private bool CanCommitOrRevert => CanWriteToDatabase && IsDirty;

    /// <summary>Runs <paramref name="action"/> guarded like <see cref="RunGuardedAsync"/>, additionally surfacing any error as a modal dialog.</summary>
    private async Task RunGuardedWithDialogAsync(Func<Task> action)
    {
        await RunGuardedAsync(action).ConfigureAwait(true);
        if (HasError)
        {
            await _ui.ShowErrorAsync("Error", ErrorMessage!).ConfigureAwait(true);
        }
    }

    // --------------------------------------------------------------------------------------
    // Lifecycle: testable core methods
    // --------------------------------------------------------------------------------------

    /// <summary>Opens an existing database file. Closes any currently-open database first, discarding it without prompting.</summary>
    public async Task OpenDatabaseAsync(string path, bool readOnly = false)
    {
        await RunGuardedWithDialogAsync(async () =>
        {
            await ForceCloseCurrentAsync().ConfigureAwait(true);
            var session = await DatabaseSession.OpenAsync(path, readOnly).ConfigureAwait(true);
            if (!readOnly)
            {
                await session.BeginEditAsync().ConfigureAwait(true);
            }

            AttachSession(session);
            StatusMessage = $"Opened '{path}'{(readOnly ? " (read-only)" : string.Empty)}.";
        });
    }

    /// <summary>Creates a brand-new database file, overwriting any existing file at <paramref name="path"/>.</summary>
    public async Task CreateDatabaseAsync(string path)
    {
        await RunGuardedWithDialogAsync(async () =>
        {
            await ForceCloseCurrentAsync().ConfigureAwait(true);
            var session = await DatabaseSession.CreateAsync(path, overwrite: true).ConfigureAwait(true);
            await session.BeginEditAsync().ConfigureAwait(true);
            AttachSession(session);
            StatusMessage = $"Created '{path}'.";
        });
    }

    /// <summary>
    /// Saves a full copy of the current database to <paramref name="path"/> (SQLite online backup).
    /// </summary>
    /// <remarks>
    /// Every writable session keeps a long-lived edit transaction open the whole time it is attached
    /// (see <see cref="OpenDatabaseAsync"/>/<see cref="CreateDatabaseAsync"/>), but
    /// <see cref="DatabaseSession.SaveAsAsync"/> refuses to run while one is active. If there are
    /// uncommitted changes, the user is asked whether to keep or discard them first (their intent is
    /// never silently discarded); the transaction is then ended (committed or rolled back per that
    /// choice, or simply committed if it was empty), the copy is made, and a fresh edit transaction is
    /// always restarted afterward — even if the copy itself failed — so the session is left exactly as
    /// usable as it was before Save As was invoked.
    /// </remarks>
    public async Task SaveAsAsync(string path)
    {
        if (_session is null)
        {
            return;
        }

        await RunGuardedWithDialogAsync(async () =>
        {
            bool resumeEditAfter = false;

            if (_session.IsDirty)
            {
                var choice = await _ui.ConfirmDirtyActionAsync(
                    "Unsaved Changes",
                    "This database has uncommitted changes. Save them before saving a copy to a new file?").ConfigureAwait(true);

                switch (choice)
                {
                    case DirtyCloseChoice.SaveAndProceed:
                        await _session.CommitEditAsync().ConfigureAwait(true);
                        resumeEditAfter = !_session.IsReadOnly;
                        break;
                    case DirtyCloseChoice.DiscardAndProceed:
                        await _session.RevertEditAsync().ConfigureAwait(true);
                        resumeEditAfter = !_session.IsReadOnly;
                        break;
                    default:
                        // User cancelled: leave the session exactly as it was and abandon Save As.
                        return;
                }
            }
            else if (_session.HasPendingEdit)
            {
                // No pending edits, but SQLite's online backup still cannot run inside an open
                // transaction (even an empty one) — suspend it around the copy.
                await _session.CommitEditAsync().ConfigureAwait(true);
                resumeEditAfter = !_session.IsReadOnly;
            }

            try
            {
                await _session.SaveAsAsync(path).ConfigureAwait(true);
                StatusMessage = $"Saved a copy to '{path}'.";
            }
            finally
            {
                if (resumeEditAfter && !_session.HasPendingEdit)
                {
                    await _session.BeginEditAsync().ConfigureAwait(true);
                }

                RefreshDirtyState();
            }
        });
    }

    /// <summary>Commits the active edit transaction and immediately starts a new one so editing can continue.</summary>
    public async Task CommitChangesAsync()
    {
        if (_session is null)
        {
            return;
        }

        await RunGuardedWithDialogAsync(async () =>
        {
            await _session.CommitEditAsync().ConfigureAwait(true);
            if (!_session.IsReadOnly)
            {
                await _session.BeginEditAsync().ConfigureAwait(true);
            }

            RefreshDirtyState();
            StatusMessage = "Changes committed.";
        });
    }

    /// <summary>Reverts (rolls back) the active edit transaction, then starts a new one, and reloads all tabs.</summary>
    public async Task RevertChangesAsync()
    {
        if (_session is null)
        {
            return;
        }

        await RunGuardedWithDialogAsync(async () =>
        {
            await _session.RevertEditAsync().ConfigureAwait(true);
            if (!_session.IsReadOnly)
            {
                await _session.BeginEditAsync().ConfigureAwait(true);
            }

            Structure.AttachSession(_session);
            BrowseData.AttachSession(_session);
            Pragmas.AttachSession(_session);
            RefreshDirtyState();
            StatusMessage = "Changes reverted.";
        });
    }

    /// <summary>Closes the current database without any prompting, rolling back any pending edit transaction first.</summary>
    public async Task CloseDatabaseAsync()
    {
        if (_session is null)
        {
            return;
        }

        await RunGuardedWithDialogAsync(async () =>
        {
            await ForceCloseCurrentAsync().ConfigureAwait(true);
            StatusMessage = "Database closed.";
        });
    }

    private async Task ForceCloseCurrentAsync()
    {
        if (_session is null)
        {
            return;
        }

        var session = _session;
        DetachSession();
        if (session.HasPendingEdit)
        {
            await session.RevertEditAsync().ConfigureAwait(true);
        }

        if (session.IsOpen)
        {
            await session.CloseAsync().ConfigureAwait(true);
        }

        await session.DisposeAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// If a database is open and dirty, asks the user how to proceed. Returns <c>true</c> when it is
    /// safe to continue with the operation that triggered the prompt (e.g. Open/New/Close/Exit).
    /// </summary>
    public async Task<bool> ConfirmProceedPastDirtyAsync(string reason)
    {
        if (_session is null || !_session.IsDirty)
        {
            return true;
        }

        var choice = await _ui.ConfirmDirtyActionAsync(
            "Unsaved Changes",
            $"This database has uncommitted changes. Save them before {reason}?").ConfigureAwait(true);

        switch (choice)
        {
            case DirtyCloseChoice.SaveAndProceed:
                await CommitChangesAsync().ConfigureAwait(true);
                return !HasError;
            case DirtyCloseChoice.DiscardAndProceed:
                await RevertChangesAsync().ConfigureAwait(true);
                return !HasError;
            default:
                return false;
        }
    }

    // --------------------------------------------------------------------------------------
    // Lifecycle: UI-facing commands (native dialogs)
    // --------------------------------------------------------------------------------------

    [RelayCommand]
    private async Task Open()
    {
        if (!await ConfirmProceedPastDirtyAsync("opening another database").ConfigureAwait(true))
        {
            return;
        }

        var path = await _ui.PickOpenDatabaseAsync().ConfigureAwait(true);
        if (path is not null)
        {
            await OpenDatabaseAsync(path).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task OpenReadOnly()
    {
        if (!await ConfirmProceedPastDirtyAsync("opening another database").ConfigureAwait(true))
        {
            return;
        }

        var path = await _ui.PickOpenDatabaseAsync().ConfigureAwait(true);
        if (path is not null)
        {
            await OpenDatabaseAsync(path, readOnly: true).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task New()
    {
        if (!await ConfirmProceedPastDirtyAsync("creating a new database").ConfigureAwait(true))
        {
            return;
        }

        var path = await _ui.PickCreateDatabaseAsync().ConfigureAwait(true);
        if (path is not null)
        {
            await CreateDatabaseAsync(path).ConfigureAwait(true);
        }
    }

    [RelayCommand(CanExecute = nameof(CanOperateOnOpenDatabase))]
    private async Task SaveAs()
    {
        var suggested = _session?.IsInMemory == false ? Path.GetFileName(_session.Path) : "database.sqlite3";
        var path = await _ui.PickSaveDatabaseAsAsync(suggested).ConfigureAwait(true);
        if (path is not null)
        {
            await SaveAsAsync(path).ConfigureAwait(true);
        }
    }

    [RelayCommand(CanExecute = nameof(CanOperateOnOpenDatabase))]
    private async Task Close()
    {
        if (!await ConfirmProceedPastDirtyAsync("closing").ConfigureAwait(true))
        {
            return;
        }

        await CloseDatabaseAsync().ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanCommitOrRevert))]
    private Task Commit() => CommitChangesAsync();

    [RelayCommand(CanExecute = nameof(CanCommitOrRevert))]
    private async Task Revert()
    {
        bool confirmed = await _ui.ConfirmAsync(
            "Revert Changes",
            "Discard every uncommitted change since the last Write Changes? This cannot be undone.",
            confirmText: "Discard").ConfigureAwait(true);
        if (confirmed)
        {
            await RevertChangesAsync().ConfigureAwait(true);
        }
    }

    /// <summary>Requested when the user asks to exit; the view raises this after any dirty-confirmation succeeds.</summary>
    public event EventHandler? ExitRequested;

    [RelayCommand]
    private async Task Exit()
    {
        if (await ConfirmProceedPastDirtyAsync("exiting").ConfigureAwait(true))
        {
            ExitRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    [RelayCommand]
    private Task About() => _ui.ShowAboutAsync();

    // --------------------------------------------------------------------------------------
    // Structure tab dialogs (create/edit/delete schema object)
    // --------------------------------------------------------------------------------------

    private bool CanEditSelectedSchemaObject => CanWriteToDatabase && Structure.SelectedObject is not null;

    [RelayCommand(CanExecute = nameof(CanWriteToDatabase))]
    private async Task NewSchemaObject()
    {
        var ddl = await _ui.EditDdlAsync("New Schema Object", "CREATE TABLE new_table (\n    id INTEGER PRIMARY KEY,\n    name TEXT NOT NULL\n);").ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(ddl))
        {
            await Structure.ExecuteDdlAsync(ddl).ConfigureAwait(true);
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditSelectedSchemaObject))]
    private async Task EditSchemaObject()
    {
        if (Structure.SelectedObject is not { } node)
        {
            return;
        }

        var ddl = await _ui.EditDdlAsync($"Edit {node.Type} '{node.Name}'", node.Sql ?? string.Empty).ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(ddl))
        {
            await Structure.ExecuteDdlAsync(ddl).ConfigureAwait(true);
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditSelectedSchemaObject))]
    private async Task DeleteSchemaObject()
    {
        if (Structure.SelectedObject is not { } node)
        {
            return;
        }

        bool confirmed = await _ui.ConfirmAsync(
            "Delete Schema Object",
            $"Permanently drop {node.Type} '{node.Name}'? This cannot be undone.",
            confirmText: "Drop").ConfigureAwait(true);
        if (confirmed)
        {
            await Structure.DeleteObjectAsync(node).ConfigureAwait(true);
        }
    }

    // --------------------------------------------------------------------------------------
    // Tools
    // --------------------------------------------------------------------------------------

    private async Task RunToolAsync(string title, Func<Task<string>> action)
    {
        await RunGuardedAsync(async () =>
        {
            string message = await action().ConfigureAwait(true);
            StatusMessage = $"{title}: {(message.Length > 120 ? message[..120] + "..." : message)}";
            await _ui.ShowMessageAsync(title, message).ConfigureAwait(true);
        }).ConfigureAwait(true);

        if (HasError)
        {
            await _ui.ShowErrorAsync(title, ErrorMessage!).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Runs <c>VACUUM</c>. SQLite refuses to vacuum inside an open transaction, but every writable
    /// session keeps a long-lived edit transaction active the whole time it is attached. If there are
    /// uncommitted changes the user must explicitly agree to commit them first (vacuuming can never
    /// silently discard pending edits); the transaction is then ended, VACUUM runs, and a fresh edit
    /// transaction is always restarted afterward — even if VACUUM itself failed.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanWriteToDatabase))]
    private async Task Vacuum()
    {
        if (_session is null)
        {
            return;
        }

        if (_session.IsDirty)
        {
            bool confirmed = await _ui.ConfirmAsync(
                "Vacuum",
                "Vacuuming requires committing all uncommitted changes first (SQLite cannot VACUUM inside an open transaction). Commit changes and continue?",
                confirmText: "Commit && Vacuum").ConfigureAwait(true);
            if (!confirmed)
            {
                return;
            }
        }

        await RunToolAsync("Vacuum", async () =>
        {
            bool resumeEditAfter = !_session.IsReadOnly && _session.HasPendingEdit;
            if (_session.HasPendingEdit)
            {
                await _session.CommitEditAsync().ConfigureAwait(true);
            }

            try
            {
                await _session.VacuumAsync().ConfigureAwait(true);
                return "VACUUM completed successfully.";
            }
            finally
            {
                if (resumeEditAfter && !_session.HasPendingEdit)
                {
                    await _session.BeginEditAsync().ConfigureAwait(true);
                }

                NotifyMutatedSelf();
            }
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanWriteToDatabase))]
    private Task Optimize() => RunToolAsync("Optimize", async () =>
    {
        await _session!.OptimizeAsync().ConfigureAwait(true);
        return "PRAGMA optimize completed successfully.";
    });

    [RelayCommand(CanExecute = nameof(CanOperateOnOpenDatabase))]
    private Task IntegrityCheck() => RunToolAsync("Integrity Check", async () =>
        string.Join(Environment.NewLine, await _session!.IntegrityCheckAsync().ConfigureAwait(true)));

    [RelayCommand(CanExecute = nameof(CanOperateOnOpenDatabase))]
    private Task QuickCheck() => RunToolAsync("Quick Check", async () =>
        string.Join(Environment.NewLine, await _session!.QuickCheckAsync().ConfigureAwait(true)));

    [RelayCommand(CanExecute = nameof(CanOperateOnOpenDatabase))]
    private Task ForeignKeyCheck() => RunToolAsync("Foreign Key Check", async () =>
    {
        var violations = await _session!.ForeignKeyCheckAsync().ConfigureAwait(true);
        return violations.Count == 0
            ? "No foreign key violations found."
            : string.Join(Environment.NewLine, violations.Select(v => $"{v.Table} (rowid {v.RowId?.ToString() ?? "n/a"}) violates FK #{v.ForeignKeyId} referencing {v.Parent}."));
    });

    private void NotifyMutatedSelf() => RefreshDirtyState();

    // --------------------------------------------------------------------------------------
    // Import / export
    // --------------------------------------------------------------------------------------

    /// <summary>Imports a CSV file into <paramref name="tableName"/> (schema always "main").</summary>
    public async Task ImportCsvAsync(string csvPath, string tableName, CsvImportOptions options)
    {
        if (_session is null)
        {
            return;
        }

        await RunGuardedWithDialogAsync(async () =>
        {
            var result = await _session.ImportCsvFileAsync("main", tableName, csvPath, options).ConfigureAwait(true);
            StatusMessage = $"Imported {result.RowsImported} row(s) into '{tableName}'{(result.TableCreated ? " (table created)" : string.Empty)}.";
            RefreshDirtyState();
            await Structure.RefreshAsync().ConfigureAwait(true);
            await BrowseData.ReloadTablesAsync().ConfigureAwait(true);
        });
    }

    /// <summary>Executes a SQL script file (e.g. a previously exported dump) against the current session.</summary>
    public async Task ImportSqlScriptAsync(string path)
    {
        if (_session is null)
        {
            return;
        }

        await RunGuardedWithDialogAsync(async () =>
        {
            var result = await _session.ImportSqlScriptFileAsync(path).ConfigureAwait(true);
            StatusMessage = result.Message;
            RefreshDirtyState();
            await Structure.RefreshAsync().ConfigureAwait(true);
            await BrowseData.ReloadTablesAsync().ConfigureAwait(true);
        });
    }

    /// <summary>Exports the Browse Data tab's currently selected table (honoring its filter/order) as CSV.</summary>
    public async Task ExportTableCsvAsync(string path, char delimiter = ',', bool includeHeader = true)
    {
        if (_session is null)
        {
            return;
        }

        string? sql = BrowseData.BuildSelectSql();
        if (sql is null)
        {
            await _ui.ShowMessageAsync("Export", "Select a table on the Browse Data tab first.").ConfigureAwait(true);
            return;
        }

        await RunGuardedWithDialogAsync(async () =>
        {
            int rows = await _session.ExportCsvFileAsync(sql, path, new CsvExportOptions { Delimiter = delimiter, IncludeHeader = includeHeader }).ConfigureAwait(true);
            StatusMessage = $"Exported {rows} row(s) to '{path}'.";
        });
    }

    /// <summary>Exports the Browse Data tab's currently selected table (honoring its filter/order) as JSON.</summary>
    public async Task ExportTableJsonAsync(string path)
    {
        if (_session is null)
        {
            return;
        }

        string? sql = BrowseData.BuildSelectSql();
        if (sql is null)
        {
            await _ui.ShowMessageAsync("Export", "Select a table on the Browse Data tab first.").ConfigureAwait(true);
            return;
        }

        await RunGuardedWithDialogAsync(async () =>
        {
            int rows = await _session.ExportJsonFileAsync(sql, path).ConfigureAwait(true);
            StatusMessage = $"Exported {rows} row(s) to '{path}'.";
        });
    }

    /// <summary>Exports the Execute SQL tab's last result set (without re-running the SQL) as CSV.</summary>
    public Task ExportQueryResultsCsvAsync(string path) => RunGuardedWithDialogAsync(() => ExecuteSql.ExportResultsCsvAsync(path));

    /// <summary>Exports the Execute SQL tab's last result set (without re-running the SQL) as JSON.</summary>
    public Task ExportQueryResultsJsonAsync(string path) => RunGuardedWithDialogAsync(() => ExecuteSql.ExportResultsJsonAsync(path));

    /// <summary>Exports a full <c>.dump</c>-style SQL script of the database.</summary>
    public async Task ExportSqlDumpAsync(string path)
    {
        if (_session is null)
        {
            return;
        }

        await RunGuardedWithDialogAsync(async () =>
        {
            await _session.ExportSqlDumpFileAsync(path).ConfigureAwait(true);
            StatusMessage = $"Exported SQL dump to '{path}'.";
        });
    }

    [RelayCommand(CanExecute = nameof(CanWriteToDatabase))]
    private async Task ImportCsv()
    {
        var csvPath = await _ui.PickOpenCsvAsync().ConfigureAwait(true);
        if (csvPath is null)
        {
            return;
        }

        string suggestedTable = Path.GetFileNameWithoutExtension(csvPath);
        var prompt = await _ui.PromptCsvImportOptionsAsync(suggestedTable).ConfigureAwait(true);
        if (prompt is not null)
        {
            await ImportCsvAsync(csvPath, prompt.TableName, prompt.Options).ConfigureAwait(true);
        }
    }

    [RelayCommand(CanExecute = nameof(CanWriteToDatabase))]
    private async Task ImportSqlScript()
    {
        var path = await _ui.PickOpenSqlScriptAsync().ConfigureAwait(true);
        if (path is not null)
        {
            await ImportSqlScriptAsync(path).ConfigureAwait(true);
        }
    }

    [RelayCommand(CanExecute = nameof(CanOperateOnOpenDatabase))]
    private async Task ExportTableCsv()
    {
        var suggested = (BrowseData.SelectedTable?.Name ?? "table") + ".csv";
        var path = await _ui.PickSaveCsvAsync(suggested).ConfigureAwait(true);
        if (path is not null)
        {
            await ExportTableCsvAsync(path).ConfigureAwait(true);
        }
    }

    [RelayCommand(CanExecute = nameof(CanOperateOnOpenDatabase))]
    private async Task ExportTableJson()
    {
        var suggested = (BrowseData.SelectedTable?.Name ?? "table") + ".json";
        var path = await _ui.PickSaveJsonAsync(suggested).ConfigureAwait(true);
        if (path is not null)
        {
            await ExportTableJsonAsync(path).ConfigureAwait(true);
        }
    }

    [RelayCommand(CanExecute = nameof(CanOperateOnOpenDatabase))]
    private async Task ExportQueryCsv()
    {
        var path = await _ui.PickSaveCsvAsync("results.csv").ConfigureAwait(true);
        if (path is not null)
        {
            await ExportQueryResultsCsvAsync(path).ConfigureAwait(true);
        }
    }

    [RelayCommand(CanExecute = nameof(CanOperateOnOpenDatabase))]
    private async Task ExportQueryJson()
    {
        var path = await _ui.PickSaveJsonAsync("results.json").ConfigureAwait(true);
        if (path is not null)
        {
            await ExportQueryResultsJsonAsync(path).ConfigureAwait(true);
        }
    }

    [RelayCommand(CanExecute = nameof(CanOperateOnOpenDatabase))]
    private async Task ExportSqlDump()
    {
        var suggested = _session?.IsInMemory == false ? Path.GetFileNameWithoutExtension(_session.Path) + ".sql" : "dump.sql";
        var path = await _ui.PickSaveSqlDumpAsync(suggested).ConfigureAwait(true);
        if (path is not null)
        {
            await ExportSqlDumpAsync(path).ConfigureAwait(true);
        }
    }
}
