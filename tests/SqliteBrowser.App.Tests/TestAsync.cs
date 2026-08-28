using Avalonia.Threading;

namespace SqliteBrowser.App.Tests;

/// <summary>Small polling helper for waiting on a fire-and-forget async side effect (e.g. a
/// selection-changed handler that loads details) to complete, without relying on a fixed sleep.
/// Also force-pumps the Avalonia dispatcher on every iteration so queued continuations that were
/// posted back to the UI thread are not left waiting for the test runner's own pump.</summary>
internal static class TestAsync
{
    public static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 2000, int pollMs = 10)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        Dispatcher.UIThread.RunJobs();
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition was not met within the timeout.");
            }

            await Task.Delay(pollMs);
            Dispatcher.UIThread.RunJobs();
        }
    }
}
