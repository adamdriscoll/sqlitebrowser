using Avalonia.Headless.XUnit;
using SqliteBrowser.App.Models;
using SqliteBrowser.App.ViewModels;
using SqliteBrowser.App.Views;

namespace SqliteBrowser.App.Tests;

/// <summary>
/// Regression coverage for Save As and Vacuum. Both operations run against the underlying SQLite
/// connection outside of any transaction, but every writable session opened through the app keeps a
/// long-lived edit transaction active the whole time it is attached (see
/// <see cref="MainWindowViewModel.OpenDatabaseAsync"/>) - so naively calling either one directly would
/// always fail with "Cannot ... while an edit transaction is active." These tests exercise the full,
/// realistic app-level flow (an opened writable session with genuine pending changes) to prove the
/// view-model suspends/resumes the transaction correctly around each operation.
/// </summary>
public class MaintenanceOperationsTests
{
    [AvaloniaFact]
    public async Task SaveAs_FromDirtyWritableSession_SucceedsAndPreservesChangesAndEditability()
    {
        string sourcePath = await TestDatabaseFactory.CreateSampleDatabaseAsync();
        string destinationPath = TestDatabaseFactory.NewTempPath();
        try
        {
            var ui = new FakeUserInteractionService { NextDirtyChoice = DirtyCloseChoice.SaveAndProceed };
            var viewModel = new MainWindowViewModel(ui);
            var window = new MainWindow { DataContext = viewModel };
            window.Show();

            await viewModel.OpenDatabaseAsync(sourcePath);
            Assert.False(viewModel.HasError, viewModel.ErrorMessage);

            // A writable session always has an active edit transaction from the moment it is opened.
            Assert.True(viewModel.IsDatabaseOpen);
            Assert.False(viewModel.IsReadOnly);

            // Make a real pending (uncommitted) change so the session is genuinely dirty going into Save As.
            viewModel.ExecuteSql.SqlText = "INSERT INTO people (name, age) VALUES ('Katherine', 38);";
            await viewModel.ExecuteSql.ExecuteCommand.ExecuteAsync(null);
            Assert.False(viewModel.ExecuteSql.HasError, viewModel.ExecuteSql.ErrorMessage);
            Assert.True(viewModel.IsDirty);

            await viewModel.SaveAsAsync(destinationPath);

            // The operation must succeed (this used to always fail with an active edit transaction),
            // and any error must have been surfaced rather than swallowed.
            Assert.False(viewModel.HasError, viewModel.ErrorMessage);
            Assert.DoesNotContain(ui.Calls, c => c.StartsWith("Error:", StringComparison.Ordinal));
            Assert.True(File.Exists(destinationPath));

            // Because the user was asked and chose "save", the pending insert must have been kept -
            // never silently discarded - and be present in the saved copy.
            Assert.False(viewModel.IsDirty);

            // The original session must still be fully usable afterward: a new edit transaction was
            // restarted, so further edits/commits keep working normally.
            viewModel.ExecuteSql.SqlText = "SELECT COUNT(*) AS n FROM people;";
            await viewModel.ExecuteSql.ExecuteCommand.ExecuteAsync(null);
            Assert.False(viewModel.ExecuteSql.HasError, viewModel.ExecuteSql.ErrorMessage);
            Assert.Equal(4, Convert.ToInt64(viewModel.ExecuteSql.ResultsView![0]["n"]));

            viewModel.ExecuteSql.SqlText = "INSERT INTO people (name, age) VALUES ('Marie', 55);";
            await viewModel.ExecuteSql.ExecuteCommand.ExecuteAsync(null);
            Assert.False(viewModel.ExecuteSql.HasError, viewModel.ExecuteSql.ErrorMessage);
            Assert.True(viewModel.IsDirty);
            await viewModel.CommitChangesAsync();
            Assert.False(viewModel.HasError, viewModel.ErrorMessage);

            // The saved copy must contain the row that existed at Save As time (4), not the later one.
            await using var copy = await SqliteBrowser.Core.Services.DatabaseSession.OpenAsync(destinationPath, readOnly: true);
            var copyResults = await copy.ExecuteSqlAsync("SELECT COUNT(*) AS n FROM people;");
            Assert.Equal(4L, Convert.ToInt64(copyResults.Data!.Rows[0]["n"]));
            await copy.CloseAsync();
        }
        finally
        {
            TestDatabaseFactory.Delete(sourcePath);
            TestDatabaseFactory.Delete(destinationPath);
        }
    }

    [AvaloniaFact]
    public async Task Vacuum_OnDirtyWritableSession_CommitsAndSucceedsAndSessionRemainsUsable()
    {
        string path = await TestDatabaseFactory.CreateSampleDatabaseAsync();
        try
        {
            var ui = new FakeUserInteractionService { NextConfirmResult = true };
            var viewModel = new MainWindowViewModel(ui);
            var window = new MainWindow { DataContext = viewModel };
            window.Show();

            await viewModel.OpenDatabaseAsync(path);
            Assert.False(viewModel.HasError, viewModel.ErrorMessage);

            // Make a real pending change so Vacuum has to deal with a genuinely dirty, active transaction.
            viewModel.ExecuteSql.SqlText = "DELETE FROM people WHERE name = 'Linus';";
            await viewModel.ExecuteSql.ExecuteCommand.ExecuteAsync(null);
            Assert.False(viewModel.ExecuteSql.HasError, viewModel.ExecuteSql.ErrorMessage);
            Assert.True(viewModel.IsDirty);

            Assert.True(viewModel.VacuumCommand.CanExecute(null));
            await viewModel.VacuumCommand.ExecuteAsync(null);

            // Vacuum used to always fail here ("Cannot ... while an edit transaction is active.").
            Assert.False(viewModel.HasError, viewModel.ErrorMessage);
            Assert.DoesNotContain(ui.Calls, c => c.StartsWith("Error:", StringComparison.Ordinal));

            // The confirmation prompt must have been shown (dirty state requires explicit consent -
            // pending changes are committed, never silently discarded), and the change committed.
            Assert.Contains(ui.Calls, c => c.StartsWith("Confirm:Vacuum", StringComparison.Ordinal));
            Assert.False(viewModel.IsDirty);

            // The session must remain fully usable afterward (a fresh edit transaction was restarted).
            viewModel.ExecuteSql.SqlText = "SELECT COUNT(*) AS n FROM people;";
            await viewModel.ExecuteSql.ExecuteCommand.ExecuteAsync(null);
            Assert.False(viewModel.ExecuteSql.HasError, viewModel.ExecuteSql.ErrorMessage);
            Assert.Equal(2, Convert.ToInt64(viewModel.ExecuteSql.ResultsView![0]["n"]));

            viewModel.ExecuteSql.SqlText = "INSERT INTO people (name, age) VALUES ('Rosalind', 37);";
            await viewModel.ExecuteSql.ExecuteCommand.ExecuteAsync(null);
            Assert.False(viewModel.ExecuteSql.HasError, viewModel.ExecuteSql.ErrorMessage);
            Assert.True(viewModel.IsDirty);

            await viewModel.CommitChangesAsync();
            Assert.False(viewModel.HasError, viewModel.ErrorMessage);
        }
        finally
        {
            TestDatabaseFactory.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task Vacuum_WhenUserDeclinesToCommit_LeavesPendingChangesIntact()
    {
        string path = await TestDatabaseFactory.CreateSampleDatabaseAsync();
        try
        {
            var ui = new FakeUserInteractionService { NextConfirmResult = false };
            var viewModel = new MainWindowViewModel(ui);
            var window = new MainWindow { DataContext = viewModel };
            window.Show();

            await viewModel.OpenDatabaseAsync(path);

            viewModel.ExecuteSql.SqlText = "DELETE FROM people WHERE name = 'Linus';";
            await viewModel.ExecuteSql.ExecuteCommand.ExecuteAsync(null);
            Assert.True(viewModel.IsDirty);

            await viewModel.VacuumCommand.ExecuteAsync(null);

            // Declining the confirmation must abandon Vacuum without touching the pending edit at all.
            Assert.False(viewModel.HasError, viewModel.ErrorMessage);
            Assert.True(viewModel.IsDirty);

            viewModel.ExecuteSql.SqlText = "SELECT COUNT(*) AS n FROM people;";
            await viewModel.ExecuteSql.ExecuteCommand.ExecuteAsync(null);
            Assert.Equal(2, Convert.ToInt64(viewModel.ExecuteSql.ResultsView![0]["n"]));
        }
        finally
        {
            TestDatabaseFactory.Delete(path);
        }
    }
}
