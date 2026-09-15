using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SqliteBrowser.App.ViewModels;
using SqliteBrowser.App.Views;

namespace SqliteBrowser.App.Tests;

/// <summary>Small shared helpers for locating controls in the real visual tree by AutomationId, and
/// for driving them with simulated real input through the headless platform.</summary>
internal static class VisualTreeTestExtensions
{
    public static Control FindByAutomationId(this Visual root, string automationId) =>
        TryFindByAutomationId(root, automationId)
        ?? throw new InvalidOperationException($"No control with AutomationId '{automationId}' was found.");

    /// <summary>
    /// Looks up a control by <c>AutomationProperties.AutomationId</c>. Searches the logical tree
    /// (not just the visual tree) so that controls which are not currently realized visually - e.g.
    /// MenuItems inside an unopened drop-down, or the content of a non-selected TabItem - are still
    /// found, exactly like a real UI Automation client would locate them by AutomationId.
    /// </summary>
    public static Control? TryFindByAutomationId(this Visual root, string automationId)
    {
        if (root is Control { } self && AutomationProperties.GetAutomationId(self) == automationId)
        {
            return self;
        }

        if (root is ILogical logicalRoot)
        {
            foreach (var descendant in logicalRoot.GetLogicalDescendants())
            {
                if (descendant is Control control && AutomationProperties.GetAutomationId(control) == automationId)
                {
                    return control;
                }
            }
        }

        foreach (var descendant in root.GetVisualDescendants())
        {
            if (descendant is Control control && AutomationProperties.GetAutomationId(control) == automationId)
            {
                return control;
            }
        }

        return null;
    }

    /// <summary>Selects the tab whose header carries <paramref name="tabAutomationId"/> (e.g.
    /// <c>Tab.ExecuteSql</c>), forcing its content to be realized in the visual tree.</summary>
    public static void SelectTab(this Window window, string tabAutomationId)
    {
        var tabItem = (TabItem)window.FindByAutomationId(tabAutomationId);
        tabItem.IsSelected = true;
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Simulates a real mouse click (down+up) at the center of <paramref name="target"/>,
    /// going through the headless input pipeline exactly as a real pointer click would.</summary>
    public static void ClickWithMouse(this Window window, Control target)
    {
        Dispatcher.UIThread.RunJobs();
        var topLeft = target.TranslatePoint(new Point(0, 0), window) ?? default;
        var center = topLeft + new Point(target.Bounds.Width / 2, target.Bounds.Height / 2);
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }
}

/// <summary>
/// Exercises the real <see cref="MainWindow"/> shell: initial (no database open) disabled state,
/// presence of the primary AutomationIds the rest of the test suite (and any future UI automation)
/// relies on, and that a failed operation leaves a visible, persistent error state rather than a
/// dialog-only or silently-swallowed failure.
/// </summary>
public class MainWindowShellTests
{
    [AvaloniaFact]
    public void InitialState_HasNoDatabaseOpen_AndMutatingCommandsAreDisabled()
    {
        var ui = new FakeUserInteractionService();
        var viewModel = new MainWindowViewModel(ui);
        var window = new MainWindow { DataContext = viewModel };
        window.Show();

        Assert.False(viewModel.IsDatabaseOpen);
        Assert.False(viewModel.CloseCommand.CanExecute(null));
        Assert.False(viewModel.SaveAsCommand.CanExecute(null));
        Assert.False(viewModel.CommitCommand.CanExecute(null));
        Assert.False(viewModel.RevertCommand.CanExecute(null));
        Assert.False(viewModel.VacuumCommand.CanExecute(null));
        Assert.False(viewModel.ExecuteSql.ExecuteCommand.CanExecute(null));

        // Opening/creating a database is always available regardless of current state.
        Assert.True(viewModel.OpenCommand.CanExecute(null));
        Assert.True(viewModel.NewCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void MainWindow_ExposesAutomationIdsForKeyControls()
    {
        var window = new MainWindow { DataContext = new MainWindowViewModel(new FakeUserInteractionService()) };
        window.Show();

        string[] expectedIds =
        [
            "TitleBar", "TitleBar.Minimize", "TitleBar.Maximize", "TitleBar.Close",
            "MainMenu", "Menu.File.New", "Menu.File.Open", "Menu.File.Close", "Menu.Edit.Commit", "Menu.Edit.Revert",
            "Menu.Tools.Vacuum", "Menu.Help.About",
            "Toolbar.New", "Toolbar.Open", "Toolbar.Close",
            "StatusBar.Path", "StatusBar.Error", "StatusBar.Status", "StatusBar.Busy",
            "MainTabs", "Tab.Structure", "Tab.BrowseData", "Tab.ExecuteSql", "Tab.Pragmas",
            "Structure.Tree", "Structure.RefreshButton", "Structure.DdlText", "Structure.ColumnsGrid",
            "BrowseData.TableSelector", "BrowseData.Grid", "BrowseData.FirstPageButton", "BrowseData.NextPageButton",
            "BrowseData.RowCount", "BrowseData.PageSizeBox", "BrowseData.AddRowButton", "BrowseData.DeleteRowButton", "BrowseData.SaveChangesButton",
            "ExecuteSql.SqlTextBox", "ExecuteSql.ExecuteButton", "ExecuteSql.CancelButton", "ExecuteSql.ClearButton",
            "ExecuteSql.ResultsGrid", "ExecuteSql.CommandLogGrid",
            "Pragmas.RefreshButton", "Pragmas.List",
        ];

        foreach (var id in expectedIds)
        {
            Assert.True(window.TryFindByAutomationId(id) is not null, $"Expected to find a control with AutomationId '{id}'.");
        }
    }

    [AvaloniaFact]
    public async Task FailedOperation_LeavesErrorMessageVisibleInStatusBar()
    {
        var ui = new FakeUserInteractionService();
        var viewModel = new MainWindowViewModel(ui);
        var window = new MainWindow { DataContext = viewModel };
        window.Show();

        // Opening a path that does not exist must surface an error rather than throwing out of the
        // command or silently doing nothing.
        string missingPath = Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}.db");
        await viewModel.OpenDatabaseAsync(missingPath);

        Assert.True(viewModel.HasError);
        Assert.False(string.IsNullOrEmpty(viewModel.ErrorMessage));

        var errorText = window.FindByAutomationId("StatusBar.Error");
        Assert.True(errorText.IsVisible);

        var statusText = window.FindByAutomationId("StatusBar.Status");
        Assert.False(statusText.IsVisible);

        // The error must still be visible after further (unrelated) UI reads - it is not a
        // transient/one-shot notification that disappears on its own.
        Assert.True(viewModel.HasError);
        Assert.True(errorText.IsVisible);
    }
}
