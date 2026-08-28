using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;

[assembly: AvaloniaTestApplication(typeof(SqliteBrowser.App.Tests.TestAppBuilder))]

namespace SqliteBrowser.App.Tests;

/// <summary>
/// Bootstraps the Avalonia headless platform for <c>[AvaloniaFact]</c> tests. Reuses the real
/// <see cref="SqliteBrowser.App.App"/> so tests exercise the application's actual styles, fonts,
/// and resources instead of a stripped-down stand-in.
/// </summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<SqliteBrowser.App.App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
