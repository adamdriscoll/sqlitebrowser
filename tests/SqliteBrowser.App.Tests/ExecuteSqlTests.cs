using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using SqliteBrowser.App.ViewModels;
using SqliteBrowser.App.Views;

namespace SqliteBrowser.App.Tests;

/// <summary>
/// Exercises the Execute SQL tab through the real <see cref="MainWindow"/> controls: typing into the
/// actual SQL <see cref="TextBox"/> (via its bound <c>Text</c> property) and clicking the real
/// Execute button with a simulated mouse click, then asserting the real results <see cref="DataGrid"/>
/// updated accordingly.
/// </summary>
public class ExecuteSqlTests
{
    [AvaloniaFact]
    public async Task TypingSqlAndClickingExecute_PopulatesResultsGrid()
    {
        string path = await TestDatabaseFactory.CreateSampleDatabaseAsync();
        try
        {
            var viewModel = new MainWindowViewModel(new FakeUserInteractionService());
            var window = new MainWindow { DataContext = viewModel };
            window.Show();

            await viewModel.OpenDatabaseAsync(path);
            Assert.False(viewModel.HasError, viewModel.ErrorMessage);

            window.SelectTab("Tab.ExecuteSql");

            var sqlTextBox = (TextBox)window.FindByAutomationId("ExecuteSql.SqlTextBox");
            var executeButton = (Button)window.FindByAutomationId("ExecuteSql.ExecuteButton");
            var resultsGrid = (DataGrid)window.FindByAutomationId("ExecuteSql.ResultsGrid");

            // Real control -> real two-way binding -> view-model.
            sqlTextBox.Text = "SELECT id, name, age FROM people ORDER BY id;";
            Assert.Equal(sqlTextBox.Text, viewModel.ExecuteSql.SqlText);

            window.ClickWithMouse(executeButton);
            await TestAsync.WaitUntilAsync(() => viewModel.ExecuteSql.ResultsView is not null);

            Assert.False(viewModel.ExecuteSql.HasError, viewModel.ExecuteSql.ErrorMessage);
            Assert.Equal(3, viewModel.ExecuteSql.ResultsView!.Count);
            Assert.Same(viewModel.ExecuteSql.ResultsView, resultsGrid.ItemsSource);
            Assert.Equal("Ada", viewModel.ExecuteSql.ResultsView[0]["name"]);

            // The command log should now show the executed statement.
            Assert.Contains(viewModel.ExecuteSql.CommandLog, e => e.CommandText.Contains("ORDER BY id", StringComparison.Ordinal) && e.Success);
        }
        finally
        {
            TestDatabaseFactory.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task InvalidSql_ShowsInlineErrorAndDoesNotCrash()
    {
        string path = await TestDatabaseFactory.CreateSampleDatabaseAsync();
        try
        {
            var viewModel = new MainWindowViewModel(new FakeUserInteractionService());
            var window = new MainWindow { DataContext = viewModel };
            window.Show();

            await viewModel.OpenDatabaseAsync(path);

            viewModel.ExecuteSql.SqlText = "SELECT * FROM this_table_does_not_exist;";
            await viewModel.ExecuteSql.ExecuteCommand.ExecuteAsync(null);

            Assert.True(viewModel.ExecuteSql.HasError);
            Assert.False(string.IsNullOrEmpty(viewModel.ExecuteSql.ErrorMessage));

            // The main window itself must not have crashed or lost its own state.
            Assert.True(viewModel.IsDatabaseOpen);
        }
        finally
        {
            TestDatabaseFactory.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task Clear_ResetsSqlTextAndResults()
    {
        string path = await TestDatabaseFactory.CreateSampleDatabaseAsync();
        try
        {
            var viewModel = new MainWindowViewModel(new FakeUserInteractionService());
            var window = new MainWindow { DataContext = viewModel };
            window.Show();

            await viewModel.OpenDatabaseAsync(path);
            viewModel.ExecuteSql.SqlText = "SELECT 1;";
            await viewModel.ExecuteSql.ExecuteCommand.ExecuteAsync(null);
            Assert.NotNull(viewModel.ExecuteSql.ResultsView);

            viewModel.ExecuteSql.ClearCommand.Execute(null);

            Assert.Equal(string.Empty, viewModel.ExecuteSql.SqlText);
            Assert.Null(viewModel.ExecuteSql.ResultsView);
            Assert.False(viewModel.ExecuteSql.HasError);
        }
        finally
        {
            TestDatabaseFactory.Delete(path);
        }
    }
}
