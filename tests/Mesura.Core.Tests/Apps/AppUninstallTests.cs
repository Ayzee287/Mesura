using Mesura.Core.Apps;

namespace Mesura.Core.Tests.Apps;

public sealed class AppUninstallTests
{
    private static readonly AppPackage Notepad = AppCatalog.Default.ById("notepadpp")!;
    private static readonly AppPackage Obs = AppCatalog.Default.ById("obs")!;
    private static readonly AppPackage Firefox = AppCatalog.Default.ById("firefox")!;
    private static readonly AppPackage Brave = AppCatalog.Default.ById("brave")!;

    [Theory]
    [InlineData(0, InstallStatus.Succeeded)]
    [InlineData(unchecked((int)0x8A150014), InstallStatus.AlreadyInstalled)]
    [InlineData(unchecked((int)0x8A150109), InstallStatus.SucceededRebootRequired)]
    [InlineData(unchecked((int)0x8A150030), InstallStatus.Failed)] // what winget returns when an MSI uninstall fails with 1603
    [InlineData(unchecked((int)0x8A150101), InstallStatus.Failed)]
    [InlineData(-1, InstallStatus.Failed)]
    public void Uninstall_exit_codes_map_to_a_status(int exit, InstallStatus expected)
    {
        var (status, detail) = WingetOutput.DescribeUninstall(exit);

        Assert.Equal(expected, status);
        Assert.NotEmpty(detail);
    }

    /// <summary>A scripted winget: whether an uninstall really removes the app, and what it exits with.</summary>
    private sealed class FakeWinget(bool available = true, params string[] installedIds) : IWingetRunner
    {
        private readonly HashSet<string> _installed = [.. installedIds];
        public int ExitCode { get; set; }
        public bool RemovesTheApp { get; set; } = true;
        public bool Unreadable { get; set; }
        public string Refused { get; set; } = "";
        public List<string> Uninstalled { get; } = [];
        public CancellationTokenSource? CancelDuringUninstall { get; set; }

        public bool IsAvailable() => available;

        public WingetRunResult List(AppPackage package) =>
            Unreadable ? new WingetRunResult(true, unchecked((int)0x8A150107), "")
            : _installed.Contains(package.WingetId) ? new WingetRunResult(true, 0, $"Name Id Version\n---\n{package.Name} {package.WingetId} 1.0 winget")
            : new WingetRunResult(true, WingetOutput.NoApplicationsFound, "No installed package found matching input criteria.");

        public WingetRunResult Install(AppPackage package, Action<string>? onOutput, CancellationToken cancellationToken) => WingetRunResult.NotLaunched;

        public WingetRunResult Uninstall(AppPackage package, Action<string>? onOutput, CancellationToken cancellationToken)
        {
            if (Refused.Length > 0)
            {
                return WingetRunResult.Refused(Refused);
            }

            Uninstalled.Add(package.WingetId);
            onOutput?.Invoke("\\");
            onOutput?.Invoke($"fake uninstall {package.WingetId}");
            CancelDuringUninstall?.Cancel();
            if (ExitCode == 0 && RemovesTheApp)
            {
                _installed.Remove(package.WingetId);
            }

            return new WingetRunResult(true, ExitCode);
        }
    }

    [Fact]
    public void An_uninstall_that_removed_the_app_is_reported_as_uninstalled()
    {
        var winget = new FakeWinget(true, "Notepad++.Notepad++");
        var progress = new List<string>();

        var outcome = Assert.Single(new AppInstallService(winget).Uninstall([Notepad], (_, line) => progress.Add(line), CancellationToken.None));

        Assert.Equal(InstallStatus.Succeeded, outcome.Status);
        Assert.Equal("Uninstalled.", outcome.Detail);
        Assert.Contains("fake uninstall Notepad++.Notepad++", progress);
        Assert.DoesNotContain("\\", progress); // spinner frames are not progress
    }

    [Fact]
    public void A_zero_exit_that_leaves_the_app_installed_is_not_called_uninstalled()
    {
        // Firefox: winget said "Successfully uninstalled" and exited 0 with the app still there.
        var winget = new FakeWinget(true, "Notepad++.Notepad++") { RemovesTheApp = false };

        var outcome = Assert.Single(new AppInstallService(winget).Uninstall([Notepad], null, CancellationToken.None));

        Assert.Equal(InstallStatus.Failed, outcome.Status);
        Assert.Contains("still installed", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void When_winget_cannot_be_asked_afterwards_the_result_says_so()
    {
        var winget = new FakeWinget(true, "Notepad++.Notepad++") { Unreadable = true };

        var outcome = Assert.Single(new AppInstallService(winget).Uninstall([Notepad], null, CancellationToken.None));

        Assert.Equal(InstallStatus.Succeeded, outcome.Status);
        Assert.Contains("could not be re-checked", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failing_uninstaller_is_a_failure_and_the_next_app_still_runs()
    {
        var winget = new FakeWinget(true, "Notepad++.Notepad++", "OBSProject.OBSStudio") { ExitCode = unchecked((int)0x8A150030) };

        var outcomes = new AppInstallService(winget).Uninstall([Notepad, Obs], null, CancellationToken.None);

        Assert.All(outcomes, o => Assert.Equal(InstallStatus.Failed, o.Status));
        Assert.Equal(["Notepad++.Notepad++", "OBSProject.OBSStudio"], winget.Uninstalled);
        Assert.Contains("0x8A150030", outcomes[0].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_administrator_rights_the_runner_refuses_and_the_reason_is_the_result()
    {
        var winget = new FakeWinget(true, "Notepad++.Notepad++") { Refused = "Uninstalling an app needs Mesura to run as administrator." };

        var outcome = Assert.Single(new AppInstallService(winget).Uninstall([Notepad], null, CancellationToken.None));

        Assert.Equal(InstallStatus.Failed, outcome.Status);
        Assert.Contains("administrator", outcome.Detail, StringComparison.Ordinal);
        Assert.Empty(winget.Uninstalled);
    }

    [Theory]
    [InlineData("firefox")]
    [InlineData("brave")]
    [InlineData("steam")]
    [InlineData("vencord")]
    public void An_app_Mesura_has_not_verified_is_refused_without_running_anything(string id)
    {
        var winget = new FakeWinget(true, AppCatalog.Default.ById(id)!.WingetId);

        var outcome = Assert.Single(new AppInstallService(winget).Uninstall([AppCatalog.Default.ById(id)!], null, CancellationToken.None));

        Assert.Equal(InstallStatus.Failed, outcome.Status);
        Assert.Empty(winget.Uninstalled);
    }

    [Fact]
    public void Cancelling_stops_before_the_next_app()
    {
        using var cancel = new CancellationTokenSource();
        var winget = new FakeWinget(true, "Notepad++.Notepad++", "OBSProject.OBSStudio") { CancelDuringUninstall = cancel };

        var outcomes = new AppInstallService(winget).Uninstall([Notepad, Obs], null, cancel.Token);

        Assert.Equal([InstallStatus.Cancelled, InstallStatus.Cancelled], outcomes.Select(o => o.Status));
        Assert.Equal(["Notepad++.Notepad++"], winget.Uninstalled);
    }

    [Fact]
    public void Without_winget_nothing_is_attempted()
    {
        var winget = new FakeWinget(false, "Notepad++.Notepad++");

        var outcome = Assert.Single(new AppInstallService(winget).Uninstall([Notepad], null, CancellationToken.None));

        Assert.Equal(InstallStatus.WingetUnavailable, outcome.Status);
        Assert.Empty(winget.Uninstalled);
    }

    [Fact]
    public void The_catalog_offers_uninstall_only_for_the_apps_it_was_run_on()
    {
        var uninstallable = AppCatalog.Default.Packages.Where(p => p.Uninstallable).Select(p => p.Id).Order().ToList();

        Assert.Equal(["notepadpp", "obs", "vlc"], uninstallable);
        Assert.False(Firefox.Uninstallable); // winget reports success for it and the app stays
        Assert.False(Brave.Uninstallable);
        Assert.All(AppCatalog.Default.Packages.Where(p => p.Uninstallable), p => Assert.True(p.Source == AppSource.Winget && p.Context == AppRunContext.Any)); // machine-wide apps only: a per-user uninstall is not what was run
    }
}
