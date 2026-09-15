using System.Collections.ObjectModel;
using System.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqliteBrowser.App.Services;
using SqliteBrowser.Core.Models;
using SqliteBrowser.Core.Services;

namespace SqliteBrowser.App.ViewModels;

/// <summary>
/// Backs the Browse Data tab: table selection, paging, filter/order expressions, the editable grid
/// data, and add/delete/save-changes. All logic is exposed as plain properties/async methods so it
/// can be exercised directly by tests without going through actual grid UI gestures.
/// </summary>
public sealed partial class BrowseDataViewModel : ViewModelBase
{
    private readonly IUserInteractionService _ui;
    private DatabaseSession? _session;
    private TablePage? _currentPage;

    public BrowseDataViewModel(IUserInteractionService ui)
    {
        _ui = ui;
    }

    public ObservableCollection<SchemaObjectViewModel> Tables { get; } = [];

    [ObservableProperty]
    private SchemaObjectViewModel? _selectedTable;

    [ObservableProperty]
    private DataView? _rowsView;

    [ObservableProperty]
    private DataRowView? _selectedRow;

    [ObservableProperty]
    private int _pageSize = 100;

    [ObservableProperty]
    private long _totalRows;

    [ObservableProperty]
    private string? _filterText;

    [ObservableProperty]
    private string? _orderByText;

    [ObservableProperty]
    private bool _isTableReadOnly = true;

    [ObservableProperty]
    private bool _isDatabaseOpen;

    [ObservableProperty]
    private bool _hasPendingEdits;

    public int Offset { get; private set; }

    public string PageDescription => _currentPage is null
        ? "No table selected."
        : TotalRows == 0
            ? "0 rows"
            : $"Rows {Offset + 1}\u2013{Math.Min(Offset + PageSize, TotalRows)} of {TotalRows}";

    public string RowCountDescription => _currentPage is null
        ? "No rows loaded"
        : TotalRows == 1
            ? "1 row total"
            : $"{TotalRows:N0} rows total";

    public bool CanGoFirst => Offset > 0;

    public bool CanGoPrevious => Offset > 0;

    public bool CanGoNext => Offset + PageSize < TotalRows;

    public bool CanGoLast => Offset + PageSize < TotalRows;

    private void NotifyPagingChanged()
    {
        OnPropertyChanged(nameof(PageDescription));
        OnPropertyChanged(nameof(RowCountDescription));
        OnPropertyChanged(nameof(CanGoFirst));
        OnPropertyChanged(nameof(CanGoPrevious));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(CanGoLast));
        FirstPageCommand.NotifyCanExecuteChanged();
        PreviousPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
        LastPageCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedTableChanged(SchemaObjectViewModel? value) => _ = LoadPageAsync(resetOffset: true);

    partial void OnSelectedRowChanged(DataRowView? value)
    {
        DeleteRowCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanInspectSelectedValue));
    }

    partial void OnHasPendingEditsChanged(bool value) => SaveChangesCommand.NotifyCanExecuteChanged();

    /// <summary>Attaches (or detaches) the session and (re)populates the table selector.</summary>
    public void AttachSession(DatabaseSession? session)
    {
        _session = session;
        IsDatabaseOpen = session is not null;
        Tables.Clear();
        _currentPage = null;
        RowsView = null;
        Offset = 0;
        TotalRows = 0;
        SelectedTable = null;
        NotifyPagingChanged();

        if (session is not null)
        {
            _ = LoadTablesAsync();
        }
    }

    /// <summary>Reloads the table/view selector (e.g. after a schema change from an import or DDL execution).</summary>
    public Task ReloadTablesAsync() => LoadTablesAsync();

    private async Task LoadTablesAsync()
    {
        if (_session is null)
        {
            return;
        }

        await RunGuardedAsync(async () =>
        {
            var objects = await _session.GetDatabaseObjectsAsync().ConfigureAwait(true);
            Tables.Clear();
            foreach (var obj in objects.Where(o => o.Type is "table" or "view").OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase))
            {
                Tables.Add(new SchemaObjectViewModel(obj));
            }

            SelectedTable = Tables.FirstOrDefault();
        });
    }

    [RelayCommand]
    public Task Refresh() => LoadPageAsync(resetOffset: false);

    [RelayCommand]
    public Task ApplyFilter() => LoadPageAsync(resetOffset: true);

    [RelayCommand(CanExecute = nameof(CanGoFirst))]
    private Task FirstPage() { Offset = 0; return LoadPageAsync(resetOffset: false); }

    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    private Task PreviousPage() { Offset = Math.Max(0, Offset - PageSize); return LoadPageAsync(resetOffset: false); }

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private Task NextPage() { Offset += PageSize; return LoadPageAsync(resetOffset: false); }

    [RelayCommand(CanExecute = nameof(CanGoLast))]
    private Task LastPage()
    {
        Offset = TotalRows == 0 ? 0 : (int)(((TotalRows - 1) / PageSize) * PageSize);
        return LoadPageAsync(resetOffset: false);
    }

    private async Task LoadPageAsync(bool resetOffset)
    {
        if (_session is null || SelectedTable is null)
        {
            _currentPage = null;
            RowsView = null;
            TotalRows = 0;
            HasPendingEdits = false;
            NotifyPagingChanged();
            return;
        }

        if (resetOffset)
        {
            Offset = 0;
        }

        await RunGuardedAsync(async () =>
        {
            var page = await _session.GetTablePageAsync(
                SelectedTable.Schema,
                SelectedTable.Name,
                Offset,
                PageSize,
                string.IsNullOrWhiteSpace(FilterText) ? null : FilterText,
                string.IsNullOrWhiteSpace(OrderByText) ? null : OrderByText).ConfigureAwait(true);

            _currentPage = page;
            TotalRows = page.TotalRows;
            IsTableReadOnly = page.IsReadOnly || (_session.IsReadOnly);
            HasPendingEdits = false;

            page.Data.RowChanged += (_, _) => HasPendingEdits = true;
            page.Data.RowDeleted += (_, _) => HasPendingEdits = true;

            var view = page.Data.DefaultView;
            RowsView = view;
            StatusMessage = $"Loaded {page.Data.Rows.Count} row(s).";
            NotifyPagingChanged();
            AddRowCommand.NotifyCanExecuteChanged();
        });
    }

    private bool CanAddOrDeleteRow => !IsTableReadOnly && _currentPage is not null;

    [RelayCommand(CanExecute = nameof(CanAddOrDeleteRow))]
    private void AddRow()
    {
        if (_currentPage is null)
        {
            return;
        }

        var row = _currentPage.Data.NewRow();
        _currentPage.Data.Rows.Add(row);
        HasPendingEdits = true;
    }

    private bool CanDeleteRow => CanAddOrDeleteRow && SelectedRow is not null;

    [RelayCommand(CanExecute = nameof(CanDeleteRow))]
    private void DeleteRow()
    {
        SelectedRow?.Row.Delete();
        HasPendingEdits = true;
    }

    private bool CanSave => CanAddOrDeleteRow && HasPendingEdits;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveChanges()
    {
        if (_session is null || _currentPage is null)
        {
            return;
        }

        await RunGuardedAsync(async () =>
        {
            int affected = await _session.ApplyChangesAsync(_currentPage).ConfigureAwait(true);
            StatusMessage = $"Saved {affected} row(s).";
            HasPendingEdits = false;
            NotifyMutated();
            await LoadPageAsync(resetOffset: false).ConfigureAwait(true);
        });
    }

    /// <summary>Discards in-memory edits by reloading the current page from the database.</summary>
    public Task DiscardChangesAsync() => LoadPageAsync(resetOffset: false);

    public bool CanInspectSelectedValue => SelectedRow is not null;

    /// <summary>Opens the typed inspector for one selected cell and applies a confirmed edit to the in-memory row.</summary>
    public async Task EditValueAsync(string columnName)
    {
        if (SelectedRow is null || _currentPage is null || !_currentPage.Data.Columns.Contains(columnName))
        {
            return;
        }

        object currentValue = SelectedRow[columnName];
        var result = await _ui.EditValueAsync(
            $"Value: {columnName}",
            currentValue,
            IsTableReadOnly).ConfigureAwait(true);

        if (result is null || IsTableReadOnly)
        {
            return;
        }

        SelectedRow[columnName] = result.Value;
        HasPendingEdits = true;
    }

    /// <summary>The currently selected table/view's schema and name, or <c>null</c> if none is selected.</summary>
    public (string Schema, string TableName)? CurrentTable => SelectedTable is { } t ? (t.Schema, t.Name) : null;

    /// <summary>
    /// Whether <paramref name="columnName"/> is declared with a <c>BLOB</c> type affinity in the
    /// currently loaded page. Used to keep binary cells (which the grid can only display as a
    /// "&lt;BLOB n bytes&gt;" placeholder, never edit safely as text) read-only in the grid while still
    /// letting the rest of the row be edited normally.
    /// </summary>
    public bool IsBlobColumn(string columnName) =>
        _currentPage?.Columns.Any(c =>
            string.Equals(c.Name, columnName, StringComparison.OrdinalIgnoreCase)
            && c.DataType.Contains("BLOB", StringComparison.OrdinalIgnoreCase)) == true;

    /// <summary>Builds the unfiltered/full SELECT for the currently selected table honoring the active filter/order, for export.</summary>
    public string? BuildSelectSql()
    {
        if (SelectedTable is null)
        {
            return null;
        }

        string sql = $"SELECT * FROM {SqlIdentifier.QuoteQualified(SelectedTable.Schema, SelectedTable.Name)}";
        if (!string.IsNullOrWhiteSpace(FilterText))
        {
            sql += $" WHERE ({FilterText})";
        }

        if (!string.IsNullOrWhiteSpace(OrderByText))
        {
            sql += $" ORDER BY {OrderByText}";
        }

        return sql + ";";
    }
}
