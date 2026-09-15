using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SqliteBrowser.App.Converters;
using SqliteBrowser.App.ViewModels;
using SqliteBrowser.App.Views;

namespace SqliteBrowser.App.Tests;

/// <summary>Exercises Browse Data: switching tables and paging through a real temporary database.</summary>
public class BrowseDataTests
{
    [AvaloniaFact]
    public async Task LoadedRows_AreRenderedInGridCells()
    {
        string path = await TestDatabaseFactory.CreateSampleDatabaseAsync();
        try
        {
            var viewModel = new MainWindowViewModel(new FakeUserInteractionService());
            var window = new MainWindow { DataContext = viewModel };
            window.Show();
            window.SelectTab("Tab.BrowseData");

            await viewModel.OpenDatabaseAsync(path);
            await TestAsync.WaitUntilAsync(() => viewModel.BrowseData.RowsView?.Count == 3);
            Dispatcher.UIThread.RunJobs();

            var grid = (DataGrid)window.FindByAutomationId("BrowseData.Grid");
            string?[] renderedText = grid.GetVisualDescendants()
                .OfType<TextBlock>()
                .Select(text => text.Text)
                .ToArray();

            Assert.Contains("Ada", renderedText);
            Assert.Contains("Grace", renderedText);
            Assert.Contains("Linus", renderedText);

            grid.SelectedItem = viewModel.BrowseData.RowsView![0];
            grid.CurrentColumn = grid.Columns.Single(column => Equals(column.Header, "name"));
            Assert.True(grid.BeginEdit());
            Dispatcher.UIThread.RunJobs();

            var editor = grid.GetVisualDescendants()
                .OfType<TextBox>()
                .Single(textBox => textBox.Name == "CellTextBox");
            editor.Text = "Katherine";
            Assert.True(grid.CommitEdit());

            Assert.Equal("Katherine", viewModel.BrowseData.RowsView[0]["name"]);
        }
        finally
        {
            TestDatabaseFactory.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task SwitchingTableAndPaging_UpdatesGridAndPageDescription()
    {
        string path = await TestDatabaseFactory.CreateSampleDatabaseAsync();
        try
        {
            var viewModel = new MainWindowViewModel(new FakeUserInteractionService());
            var window = new MainWindow { DataContext = viewModel };
            window.Show();

            await viewModel.OpenDatabaseAsync(path);
            Assert.False(viewModel.HasError, viewModel.ErrorMessage);

            // Add a second table so we can exercise switching the table selector.
            viewModel.ExecuteSql.SqlText = "CREATE TABLE tags (id INTEGER PRIMARY KEY, label TEXT);" +
                                            "INSERT INTO tags (label) VALUES ('red');" +
                                            "INSERT INTO tags (label) VALUES ('blue');";
            await viewModel.ExecuteSql.ExecuteCommand.ExecuteAsync(null);
            Assert.False(viewModel.ExecuteSql.HasError, viewModel.ExecuteSql.ErrorMessage);
            await viewModel.BrowseData.ReloadTablesAsync();

            // Small page size so 3 "people" rows span more than one page.
            viewModel.BrowseData.PageSize = 2;
            viewModel.BrowseData.SelectedTable = viewModel.BrowseData.Tables.Single(t => t.Name == "people");
            await TestAsync.WaitUntilAsync(() => viewModel.BrowseData.RowsView is not null);

            Assert.Equal(3, viewModel.BrowseData.TotalRows);
            Assert.Equal("Rows 1\u20132 of 3", viewModel.BrowseData.PageDescription);
            Assert.Equal("3 rows total", viewModel.BrowseData.RowCountDescription);
            Assert.True(viewModel.BrowseData.CanGoNext);
            Assert.False(viewModel.BrowseData.CanGoPrevious);

            await viewModel.BrowseData.NextPageCommand.ExecuteAsync(null);
            Assert.Equal("Rows 3\u20133 of 3", viewModel.BrowseData.PageDescription);
            Assert.False(viewModel.BrowseData.CanGoNext);
            Assert.True(viewModel.BrowseData.CanGoPrevious);

            await viewModel.BrowseData.FirstPageCommand.ExecuteAsync(null);
            Assert.Equal("Rows 1\u20132 of 3", viewModel.BrowseData.PageDescription);

            // Switch to the "tags" table and confirm the grid reloads for it.
            viewModel.BrowseData.SelectedTable = viewModel.BrowseData.Tables.Single(t => t.Name == "tags");
            await TestAsync.WaitUntilAsync(() => viewModel.BrowseData.TotalRows == 2);
            Assert.Equal("Rows 1\u20132 of 2", viewModel.BrowseData.PageDescription);
            Assert.Equal("2 rows total", viewModel.BrowseData.RowCountDescription);
        }
        finally
        {
            TestDatabaseFactory.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task AddAndSaveRow_PersistsToDatabase()
    {
        string path = await TestDatabaseFactory.CreateSampleDatabaseAsync();
        try
        {
            var viewModel = new MainWindowViewModel(new FakeUserInteractionService());
            var window = new MainWindow { DataContext = viewModel };
            window.Show();

            await viewModel.OpenDatabaseAsync(path);
            viewModel.BrowseData.SelectedTable = viewModel.BrowseData.Tables.Single(t => t.Name == "people");
            await TestAsync.WaitUntilAsync(() => viewModel.BrowseData.RowsView is not null);

            Assert.False(viewModel.BrowseData.IsTableReadOnly);
            Assert.True(viewModel.BrowseData.AddRowCommand.CanExecute(null));

            viewModel.BrowseData.AddRowCommand.Execute(null);
            var newRow = viewModel.BrowseData.RowsView![^1];
            newRow["name"] = "Margaret";
            newRow["age"] = 36;

            Assert.True(viewModel.BrowseData.HasPendingEdits);
            Assert.True(viewModel.BrowseData.SaveChangesCommand.CanExecute(null));

            await viewModel.BrowseData.SaveChangesCommand.ExecuteAsync(null);
            Assert.False(viewModel.BrowseData.HasError, viewModel.BrowseData.ErrorMessage);
            Assert.False(viewModel.BrowseData.HasPendingEdits);
            Assert.Equal(4, viewModel.BrowseData.TotalRows);
        }
        finally
        {
            TestDatabaseFactory.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task BlobColumn_IsReadOnlyInGeneratedGrid_AndCannotBeCorrupted()
    {
        string path = await TestDatabaseFactory.CreateBlobSampleDatabaseAsync();
        try
        {
            var viewModel = new MainWindowViewModel(new FakeUserInteractionService());
            var window = new MainWindow { DataContext = viewModel };
            window.Show();

            await viewModel.OpenDatabaseAsync(path);
            Assert.False(viewModel.HasError, viewModel.ErrorMessage);

            viewModel.BrowseData.SelectedTable = viewModel.BrowseData.Tables.Single(t => t.Name == "assets");
            await TestAsync.WaitUntilAsync(() => viewModel.BrowseData.RowsView is not null);

            // The view-model must recognize "payload" as a declared BLOB column (and "name" as not).
            Assert.True(viewModel.BrowseData.IsBlobColumn("payload"));
            Assert.False(viewModel.BrowseData.IsBlobColumn("name"));

            // The seeded row must really hold a byte[] value going in.
            var row = viewModel.BrowseData.RowsView![0];
            var original = Assert.IsType<byte[]>(row["payload"]);
            Assert.Equal(new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0xFF }, original);

            // Force the Browse Data tab's DataGrid to realize and auto-generate its columns against
            // the real bound data, exactly as a user would see them.
            window.SelectTab("Tab.BrowseData");
            await TestAsync.WaitUntilAsync(() =>
                ((DataGrid)window.FindByAutomationId("BrowseData.Grid")).Columns.Count > 0);

            var grid = (DataGrid)window.FindByAutomationId("BrowseData.Grid");
            var payloadColumn = grid.Columns.Single(c => c.Header?.ToString() == "payload");
            var nameColumn = grid.Columns.Single(c => c.Header?.ToString() == "name");

            Assert.True(payloadColumn.IsReadOnly, "The generated BLOB column must be read-only in the grid.");
            Assert.False(nameColumn.IsReadOnly, "Non-BLOB columns must remain editable.");

            // Defense-in-depth: even outside the read-only column, the display converter itself must
            // refuse to round-trip its own "<BLOB n bytes>" placeholder text back over a real value.
            var convertBack = CellDisplayConverter.Instance.ConvertBack("<BLOB 6 bytes>", typeof(object), null, CultureInfo.InvariantCulture);
            Assert.Equal(BindingOperations.DoNothing, convertBack);

            // A normal string edit (not our placeholder) must still pass through unaffected, so
            // ordinary text columns keep working.
            var normalEdit = CellDisplayConverter.Instance.ConvertBack("Margaret", typeof(object), null, CultureInfo.InvariantCulture);
            Assert.Equal("Margaret", normalEdit);

            // The underlying byte[] must still be completely untouched after all of the above.
            Assert.Same(original, viewModel.BrowseData.RowsView![0]["payload"]);
            Assert.Equal(new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0xFF }, (byte[])viewModel.BrowseData.RowsView![0]["payload"]);
        }
        finally
        {
            TestDatabaseFactory.Delete(path);
        }
    }
}
