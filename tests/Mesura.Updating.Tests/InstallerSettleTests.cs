using Mesura.Updating;

namespace Mesura.Updating.Tests;

/// <summary>
/// After the installer's client was killed, the Windows Installer service finishes (or rolls back) the install on its own. The updater waits for the install's log to say it
/// is over before it looks at what is installed; found by killing msiexec for real in the end-to-end update check.
/// </summary>
public sealed class InstallerSettleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mesura-settle-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _log;

    public InstallerSettleTests()
    {
        Directory.CreateDirectory(_dir);
        _log = Path.Combine(_dir, "install.log");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void A_log_that_ends_with_the_engine_returning_is_idle_at_once()
    {
        File.WriteAllText(_log, "MSI (s) (A4:BC) [00:45:24:348]: Doing action: InstallFiles\r\nMSI (s) (A4:BC) [00:45:24:348]: MainEngineThread is returning 0\r\n");

        var watch = System.Diagnostics.Stopwatch.StartNew();
        InstallerLauncher.WaitUntilIdle(_log, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(20));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"waited {watch.Elapsed}");
    }

    [Fact]
    public void A_utf16_log_is_read_the_same_way()
    {
        File.WriteAllText(_log, "MSI (s): MainEngineThread is returning 1603\r\n", System.Text.Encoding.Unicode);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        InstallerLauncher.WaitUntilIdle(_log, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(20));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"waited {watch.Elapsed}");
    }

    [Fact]
    public async Task A_log_that_is_still_growing_is_waited_for_until_it_ends()
    {
        File.WriteAllText(_log, "MSI (s): Doing action: InstallFiles\r\n");
        var writer = Task.Run(async () =>
        {
            for (var i = 0; i < 4; i++)
            {
                await Task.Delay(500, TestContext.Current.CancellationToken);
                File.AppendAllText(_log, $"MSI (s): step {i}\r\n");
            }

            File.AppendAllText(_log, "MSI (s): MainEngineThread is returning 0\r\n");
        }, TestContext.Current.CancellationToken);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        InstallerLauncher.WaitUntilIdle(_log, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(20)); // the quiet period alone would be 20 s: it is the last line that ends the wait
        await writer;

        Assert.InRange(watch.Elapsed.TotalSeconds, 2, 8);
        Assert.Contains("MainEngineThread", File.ReadAllText(_log), StringComparison.Ordinal);
    }

    [Fact]
    public void A_log_that_never_appeared_means_nothing_is_running_once_it_has_been_quiet()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        InstallerLauncher.WaitUntilIdle(Path.Combine(_dir, "none.log"), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1));

        Assert.InRange(watch.Elapsed.TotalSeconds, 1, 5);
    }

    [Fact]
    public async Task The_wait_is_capped_by_its_timeout()
    {
        File.WriteAllText(_log, "MSI (s): Doing action: InstallFiles\r\n");
        using var cancel = new CancellationTokenSource();
        var writer = Task.Run(async () =>
        {
            while (!cancel.IsCancellationRequested)
            {
                File.AppendAllText(_log, "MSI (s): still busy\r\n");
                await Task.Delay(200, TestContext.Current.CancellationToken);
            }
        }, TestContext.Current.CancellationToken);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        InstallerLauncher.WaitUntilIdle(_log, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30));
        await cancel.CancelAsync();
        await writer;

        Assert.InRange(watch.Elapsed.TotalSeconds, 1.5, 6);
    }
}
