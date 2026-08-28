using Avalonia;

namespace SqliteBrowser.App;

/// <summary>Native process entry point. Kept intentionally tiny: all startup logic lives in <see cref="App"/>.</summary>
internal static class Program
{
    /// <summary>
    /// The command-line arguments the process was launched with, captured before Avalonia's own
    /// argument handling runs so <see cref="App"/> can open a database path passed on the command line.
    /// </summary>
    internal static string[] StartupArgs { get; private set; } = Array.Empty<string>();

    [STAThread]
    public static int Main(string[] args)
    {
        StartupArgs = args;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>
    /// Builds the Avalonia application. Exposed publicly so the headless test host can reuse the exact
    /// same application configuration (theme, fonts) and only append <c>UseHeadless(...)</c>.
    /// </summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
