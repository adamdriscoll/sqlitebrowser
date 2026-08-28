using System.Data;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;
using SqliteBrowser.App.ViewModels;
using SqliteBrowser.App.Converters;
using SqliteBrowser.Core.Models;

namespace SqliteBrowser.App.Views;

/// <summary>
/// The application shell. Code-behind here is limited to window lifecycle (closing confirmation,
/// command-line startup path, exit wiring) and DataGrid column-generation plumbing that has no
/// reasonable declarative equivalent — see the remarks on <see cref="RebuildColumns"/> for why the
/// Browse Data / Execute SQL grids cannot simply use <c>AutoGenerateColumns</c>.
/// All application behavior lives in <see cref="MainWindowViewModel"/> and its child view-models.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
        DataContextChanged += OnDataContextChanged;
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (ViewModel is { } vm)
        {
            vm.ExitRequested += (_, _) => Close();
            vm.BrowseData.PropertyChanged += OnBrowseDataPropertyChanged;
            vm.ExecuteSql.PropertyChanged += OnExecuteSqlPropertyChanged;

            // The view-model may already have a page loaded (e.g. a test attaches DataContext after
            // opening a database), so seed the grids' columns immediately rather than waiting for the
            // next change notification.
            RebuildColumns(BrowseDataGrid, vm.BrowseData.RowsView, vm.BrowseData.IsBlobColumn);
            RebuildColumns(ExecuteSqlResultsGrid, vm.ExecuteSql.ResultsView, ColumnHoldsAnyBlobValue(vm.ExecuteSql.ResultsView));
        }
    }

    private void OnBrowseDataPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModels.BrowseDataViewModel.RowsView) && ViewModel is { } vm)
        {
            RebuildColumns(BrowseDataGrid, vm.BrowseData.RowsView, vm.BrowseData.IsBlobColumn);
        }
    }

    private void OnExecuteSqlPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModels.ExecuteSqlViewModel.ResultsView))
        {
            var resultsView = ViewModel?.ExecuteSql.ResultsView;
            RebuildColumns(ExecuteSqlResultsGrid, resultsView, ColumnHoldsAnyBlobValue(resultsView));
        }
    }

    private bool _closeConfirmed;

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeConfirmed || ViewModel is not { } vm)
        {
            return;
        }

        e.Cancel = true;
        bool proceed = await vm.ConfirmProceedPastDirtyAsync("exiting").ConfigureAwait(true);
        if (proceed)
        {
            await vm.CloseDatabaseAsync().ConfigureAwait(true);
            _closeConfirmed = true;
            Close();
        }
    }

    /// <summary>Opens a database file supplied on the command line once the window is ready.</summary>
    public async Task OpenStartupPathAsync(string path)
    {
        if (ViewModel is { } vm)
        {
            await vm.OpenDatabaseAsync(path).ConfigureAwait(true);
        }
    }

    private async void OnApplyPragmaClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: PragmaRowViewModel row } && ViewModel is { } vm)
        {
            await vm.Pragmas.ApplyAsync(row).ConfigureAwait(true);
        }
    }

    private void OnExitClick(object? sender, RoutedEventArgs e) => ViewModel?.ExitCommand.Execute(null);

    /// <summary>
    /// Rebuilds <paramref name="grid"/>'s columns from the schema of the <see cref="DataTable"/> backing
    /// <paramref name="view"/>, hiding the synthetic rowid identity column and formatting NULL/BLOB cells
    /// for display via <see cref="CellDisplayConverter"/>.
    /// </summary>
    /// <remarks>
    /// This cannot be done with <c>AutoGenerateColumns</c> bound to a plain ADO.NET <see cref="DataView"/>:
    /// unlike WPF, Avalonia's <see cref="DataGrid"/> generates columns by reflecting the CLR properties of
    /// a representative item using plain reflection — it has no special-case support for
    /// <see cref="System.ComponentModel.ICustomTypeDescriptor"/>/<see cref="System.ComponentModel.ITypedList"/>.
    /// Since each row is a <see cref="DataRowView"/>, that reflection surfaces <em>its own</em> members
    /// (<c>Row</c>, <c>RowVersion</c>, <c>IsNew</c>, <c>IsEdit</c>, the <c>Item</c> indexer, ...) instead of
    /// the table's actual columns, so <c>AutoGenerateColumns</c> is unusable here. Building the columns
    /// explicitly from <see cref="DataTable.Columns"/> (as done here) is what makes it possible to
    /// correctly recognize and lock down BLOB columns in the first place: <see cref="DataTable"/> always
    /// declares every column as <see cref="object"/> (SQLite is dynamically typed per-row), so BLOB-ness
    /// must come from <paramref name="isReadOnlyColumn"/> — schema metadata for Browse Data
    /// (<see cref="BrowseDataViewModel.IsBlobColumn"/>), an actual-value scan for ad hoc Execute SQL
    /// results (<see cref="ColumnHoldsAnyBlobValue(DataTable, string)"/>) — rather than the column's CLR type.
    /// </remarks>
    private static void RebuildColumns(DataGrid grid, DataView? view, Func<string, bool> isReadOnlyColumn)
    {
        grid.Columns.Clear();
        if (view?.Table is not { } table)
        {
            return;
        }

        foreach (DataColumn column in table.Columns)
        {
            if (column.ColumnName == TablePage.RowIdColumnName)
            {
                continue;
            }

            grid.Columns.Add(new DataGridTextColumn
            {
                Header = column.ColumnName,
                Binding = new Binding($"[{column.ColumnName}]") { Converter = CellDisplayConverter.Instance },
                IsReadOnly = isReadOnlyColumn(column.ColumnName),
            });
        }
    }

    /// <summary>
    /// Ad hoc Execute SQL results have no column-affinity metadata to consult (unlike Browse Data's
    /// <see cref="TablePage"/>), so BLOB-ness is instead detected from the actual loaded values: if any
    /// row currently holds a real <c>byte[]</c> in this column, it is kept read-only in the grid.
    /// </summary>
    private static bool ColumnHoldsAnyBlobValue(DataTable table, string columnName)
    {
        foreach (DataRow row in table.Rows)
        {
            if (row[columnName] is byte[])
            {
                return true;
            }
        }

        return false;
    }

    private static Func<string, bool> ColumnHoldsAnyBlobValue(DataView? view) =>
        columnName => view?.Table is { } table && ColumnHoldsAnyBlobValue(table, columnName);
}
