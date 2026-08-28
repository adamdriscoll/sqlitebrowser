using Avalonia.Headless.XUnit;
using SqliteBrowser.App.ViewModels;
using SqliteBrowser.App.Views;

namespace SqliteBrowser.App.Tests;

/// <summary>
/// Exercises opening a real temporary SQLite database through <see cref="MainWindow"/>'s bound
/// view-model and verifies the Structure tree and Browse Data table selector populate from it, plus
/// the dirty/commit/revert lifecycle.
/// </summary>
public class DatabaseLifecycleTests
{
    [AvaloniaFact]
    public async Task OpeningDatabase_PopulatesStructureTreeAndTableSelector()
    {
        string path = await TestDatabaseFactory.CreateSampleDatabaseAsync();
        try
        {
            var viewModel = new MainWindowViewModel(new FakeUserInteractionService());
            var window = new MainWindow { DataContext = viewModel };
            window.Show();

            await viewModel.OpenDatabaseAsync(path);

            Assert.False(viewModel.HasError);
            Assert.True(viewModel.IsDatabaseOpen);
            Assert.Equal(path, viewModel.CurrentPath);

            // Structure tab: the "Tables" group should contain our one table.
            var tablesGroup = viewModel.Structure.Groups.Single(g => g.DisplayName.StartsWith("Tables", StringComparison.Ordinal));
            Assert.Contains(tablesGroup.Items, item => item.Name == "people");

            // Browse Data tab: the table selector should list it too, and auto-select it.
            Assert.Contains(viewModel.BrowseData.Tables, t => t.Name == "people");
            Assert.NotNull(viewModel.BrowseData.SelectedTable);
            Assert.Equal("people", viewModel.BrowseData.SelectedTable!.Name);

            // Selecting the table object in Structure should populate its DDL and columns. The
            // selection-changed handler kicks off an unawaited detail-load task, so poll briefly
            // for it to complete rather than relying on a single fixed delay.
            viewModel.Structure.SelectedNode = tablesGroup.Items.Single(i => i.Name == "people");
            await TestAsync.WaitUntilAsync(() => viewModel.Structure.SelectedColumns is not null);

            Assert.Contains("people", viewModel.Structure.SelectedDdl, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(viewModel.Structure.SelectedColumns);
            Assert.Equal(3, viewModel.Structure.SelectedColumns!.Count);
        }
        finally
        {
            TestDatabaseFactory.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task EditCommitRevert_TracksDirtyStateCorrectly()
    {
        string path = await TestDatabaseFactory.CreateSampleDatabaseAsync();
        try
        {
            var viewModel = new MainWindowViewModel(new FakeUserInteractionService());
            var window = new MainWindow { DataContext = viewModel };
            window.Show();

            await viewModel.OpenDatabaseAsync(path);
            Assert.False(viewModel.IsDirty);
            Assert.False(viewModel.CommitCommand.CanExecute(null));

            // Executing a DDL/DML statement through the Execute SQL tab should mark the session dirty.
            viewModel.ExecuteSql.SqlText = "INSERT INTO people (name, age) VALUES ('Alan', 41);";
            await viewModel.ExecuteSql.ExecuteCommand.ExecuteAsync(null);
            Assert.False(viewModel.ExecuteSql.HasError, viewModel.ExecuteSql.ErrorMessage);

            Assert.True(viewModel.IsDirty);
            Assert.True(viewModel.CommitCommand.CanExecute(null));
            Assert.True(viewModel.RevertCommand.CanExecute(null));

            await viewModel.RevertChangesAsync();
            Assert.False(viewModel.HasError, viewModel.ErrorMessage);
            Assert.False(viewModel.IsDirty);

            // The insert should have been rolled back.
            viewModel.ExecuteSql.SqlText = "SELECT COUNT(*) AS n FROM people;";
            await viewModel.ExecuteSql.ExecuteCommand.ExecuteAsync(null);
            Assert.Equal(3, Convert.ToInt64(viewModel.ExecuteSql.ResultsView![0]["n"]));

            // Now commit a change and confirm the dirty flag clears without undoing it.
            viewModel.ExecuteSql.SqlText = "INSERT INTO people (name, age) VALUES ('Barbara', 63);";
            await viewModel.ExecuteSql.ExecuteCommand.ExecuteAsync(null);
            Assert.True(viewModel.IsDirty);

            await viewModel.CommitChangesAsync();
            Assert.False(viewModel.HasError, viewModel.ErrorMessage);
            Assert.False(viewModel.IsDirty);

            viewModel.ExecuteSql.SqlText = "SELECT COUNT(*) AS n FROM people;";
            await viewModel.ExecuteSql.ExecuteCommand.ExecuteAsync(null);
            Assert.Equal(4, Convert.ToInt64(viewModel.ExecuteSql.ResultsView![0]["n"]));
        }
        finally
        {
            TestDatabaseFactory.Delete(path);
        }
    }
}
