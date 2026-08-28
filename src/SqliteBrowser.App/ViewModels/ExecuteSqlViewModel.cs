using System.Collections.ObjectModel;
using System.Data;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqliteBrowser.App.Services;
using SqliteBrowser.Core.Models;
using SqliteBrowser.Core.Services;

namespace SqliteBrowser.App.ViewModels;

/// <summary>
/// Backs the Execute SQL tab: a free-form multi-statement editor, cancellable execution, results
/// grid, and a running log of every command sent to the session (including ones issued by other tabs).
/// </summary>
public sealed partial class ExecuteSqlViewModel : ViewModelBase
{
    private DatabaseSession? _session;
    private CancellationTokenSource? _executionCts;
    private DataTable? _lastResultTable;

    [ObservableProperty]
    private string _sqlText = string.Empty;

    [ObservableProperty]
    private DataView? _resultsView;

    [ObservableProperty]
    private long _elapsedMilliseconds;

    [ObservableProperty]
    private int _rowsAffected;

    [ObservableProperty]
    private bool _isDatabaseOpen;

    [ObservableProperty]
    private bool _isExecuting;

    public ObservableCollection<SqlCommandLogEntry> CommandLog { get; } = [];

    public bool HasResults => _lastResultTable is { Rows.Count: > 0 };

    public void AttachSession(DatabaseSession? oldSession, DatabaseSession? newSession)
    {
        if (oldSession is not null)
        {
            oldSession.CommandExecuted -= OnCommandExecuted;
        }

        _session = newSession;
        IsDatabaseOpen = newSession is not null;
        ResultsView = null;
        _lastResultTable = null;
        StatusMessage = null;
        ElapsedMilliseconds = 0;
        RowsAffected = 0;
        CommandLog.Clear();

        if (newSession is not null)
        {
            newSession.CommandExecuted += OnCommandExecuted;
        }
    }

    private void OnCommandExecuted(object? sender, SqlCommandLogEventArgs e)
    {
        // The session may raise this event from a background thread while a statement is still
        // in flight, so hop back onto the UI thread before touching the bound collection.
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnCommandExecuted(sender, e));
            return;
        }

        CommandLog.Insert(0, e.Entry);
        while (CommandLog.Count > 200)
        {
            CommandLog.RemoveAt(CommandLog.Count - 1);
        }
    }

    private bool CanExecute => !IsExecuting && _session is not null && !string.IsNullOrWhiteSpace(SqlText);

    [RelayCommand(CanExecute = nameof(CanExecute))]
    public async Task Execute()
    {
        if (_session is null || string.IsNullOrWhiteSpace(SqlText))
        {
            return;
        }

        _executionCts = new CancellationTokenSource();
        IsExecuting = true;
        ErrorMessage = null;
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await _session.ExecuteSqlAsync(SqlText, _executionCts.Token).ConfigureAwait(true);
            _lastResultTable = result.Data;
            ResultsView = result.Data?.DefaultView;
            RowsAffected = result.RowsAffected;
            ElapsedMilliseconds = (long)result.Elapsed.TotalMilliseconds;
            StatusMessage = result.Message;
            NotifyMutated();
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Execution cancelled.";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            sw.Stop();
            IsExecuting = false;
            _executionCts.Dispose();
            _executionCts = null;
            OnPropertyChanged(nameof(HasResults));
        }
    }

    private bool CanCancel => IsExecuting;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _executionCts?.Cancel();

    partial void OnIsExecutingChanged(bool value)
    {
        ExecuteCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    partial void OnSqlTextChanged(string value) => ExecuteCommand.NotifyCanExecuteChanged();

    [RelayCommand]
    private void Clear()
    {
        SqlText = string.Empty;
        ResultsView = null;
        _lastResultTable = null;
        StatusMessage = null;
        ErrorMessage = null;
        RowsAffected = 0;
        ElapsedMilliseconds = 0;
        OnPropertyChanged(nameof(HasResults));
    }

    /// <summary>Writes the last executed statement's result set to a CSV file. Does not re-run the SQL.</summary>
    public async Task ExportResultsCsvAsync(string path, char delimiter = ',', bool includeHeader = true)
    {
        if (_lastResultTable is null)
        {
            throw new InvalidOperationException("There are no results to export.");
        }

        await DataTableExport.WriteCsvAsync(_lastResultTable, path, delimiter, includeHeader).ConfigureAwait(false);
    }

    /// <summary>Writes the last executed statement's result set to a JSON file. Does not re-run the SQL.</summary>
    public async Task ExportResultsJsonAsync(string path)
    {
        if (_lastResultTable is null)
        {
            throw new InvalidOperationException("There are no results to export.");
        }

        await DataTableExport.WriteJsonAsync(_lastResultTable, path).ConfigureAwait(false);
    }
}
