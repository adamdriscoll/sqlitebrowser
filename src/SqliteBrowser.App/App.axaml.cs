using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using SqliteBrowser.App.ViewModels;
using SqliteBrowser.App.Views;

namespace SqliteBrowser.App;

/// <summary>Application root. Creates the single <see cref="MainWindow"/> for the classic desktop lifetime.</summary>
public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = new MainWindowViewModel();
            var window = new MainWindow { DataContext = viewModel };
            desktop.MainWindow = window;

            // Command-line convenience: "SqliteBrowser.App <path-to-db>" opens it on startup.
            string[] args = desktop.Args ?? Program.StartupArgs;
            string? startupPath = args.FirstOrDefault(a => !a.StartsWith('-'));
            if (!string.IsNullOrWhiteSpace(startupPath))
            {
                window.Opened += async (_, _) => await window.OpenStartupPathAsync(startupPath);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
