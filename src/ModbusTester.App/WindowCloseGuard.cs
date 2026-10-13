namespace ModbusTester.App;

/// <summary>
/// Safe asynchronous close of a WPF window: the first Closing is cancelled while <c>cleanup</c> runs (disconnect,
/// release TCP / serial port); when it has finished – or failed, or exceeded <c>timeout</c> – the window is closed
/// for real. Cleanup runs only once however often Close is requested, and an exception never keeps the window open.
/// </summary>
internal sealed class WindowCloseGuard(Func<Task> cleanup, TimeSpan timeout)
{
    private Task? _run;

    /// <summary>True once cleanup has finished (or failed / timed out): the next Closing may proceed.</summary>
    public bool IsCleanedUp { get; private set; }

    public int CleanupRuns { get; private set; }

    /// <summary>Completes when the close sequence is done (for tests).</summary>
    public Task Completion => _run ?? Task.CompletedTask;

    /// <param name="closeAgain">Requests the real close afterwards; must not close synchronously (e.g. Dispatcher.BeginInvoke).</param>
    /// <returns>True if the window may close now; false = cancel this Closing (cleanup is running).</returns>
    public bool OnClosing(Action closeAgain)
    {
        if (IsCleanedUp)
            return true;
        _run ??= RunAsync(closeAgain);
        return false;
    }

    private async Task RunAsync(Action closeAgain)
    {
        CleanupRuns++;
        try
        {
            await Task.WhenAny(SafeCleanupAsync(), Task.Delay(timeout));
        }
        finally
        {
            IsCleanedUp = true;
            closeAgain();
        }
    }

    private async Task SafeCleanupAsync()
    {
        try
        {
            await cleanup();
        }
        catch (Exception)
        {
            // Closing must never fail: the process is ending and the OS releases what is left.
        }
    }
}
