using Mesura.Core.Apps;

namespace Mesura.Core.Tests.Apps;

public sealed class AppUpdateTests
{
    private static readonly AppPackage SevenZip = AppCatalog.Default.ById("sevenzip")!;
    private static readonly AppPackage Git = AppCatalog.Default.ById("git")!;
    private static readonly AppPackage Brave = AppCatalog.Default.ById("brave")!;

    private static WingetRunResult Run(int exit, string output) => new(true, exit, output);

    // What winget 1.x prints for `list --exact --id Git.Git --upgrade-available` when an update exists (captured 2026-10-30).
    private const string GitListing = "Name Id      Version  Available\r\n-------------------------------\r\nGit  Git.Git 2.55.0.3 2.56.0.4\r\n1 upgrades available.\r\n";

    [Fact]
    public void An_id_line_in_the_listing_is_an_update_with_its_versions()
    {
        var check = WingetOutput.ParseUpgradeAvailable(Git, Run(0, GitListing));

        Assert.Equal(UpdateState.Available, check.State);
        Assert.Equal("2.55.0.3", check.InstalledVersion);
        Assert.Equal("2.56.0.4", check.AvailableVersion);
    }

    [Fact]
    public void A_source_column_is_not_taken_for_a_version()
    {
        var check = WingetOutput.ParseUpgradeAvailable(SevenZip, Run(0, "Name Id Version Available Source\r\n---\r\n7-Zip 7zip.7zip 26.00 26.05 winget\r\n"));

        Assert.Equal(("26.00", "26.05"), (check.InstalledVersion, check.AvailableVersion));
    }

    [Fact]
    public void Unreadable_columns_leave_the_versions_empty_but_the_update_stays_available()
    {
        var check = WingetOutput.ParseUpgradeAvailable(SevenZip, Run(0, "7-Zip 7zip.7zip < 26.00 26.05 winget\r\n"));

        Assert.Equal(UpdateState.Available, check.State);
        Assert.Null(check.InstalledVersion);
        Assert.Null(check.AvailableVersion);
    }

    [Theory]
    [InlineData("No installed package found matching input criteria.")]
    [InlineData("Установленный пакет не найден по заданным критериям.")]
    [InlineData("")]
    public void Exit_zero_without_the_id_is_up_to_date_in_any_language(string message)
    {
        // winget exits 0 whether or not an update exists; only the id in a table line says there is one.
        Assert.Equal(UpdateState.UpToDate, WingetOutput.ParseUpgradeAvailable(Git, Run(0, message)).State);
    }

    [Fact]
    public void Another_package_whose_id_only_starts_the_same_is_not_this_one()
    {
        Assert.Equal(UpdateState.UpToDate, WingetOutput.ParseUpgradeAvailable(Git, Run(0, "GitHub CLI Git.GitHub 1.0 2.0\r\n")).State);
    }

    [Fact]
    public void No_application_found_is_up_to_date_and_every_other_failure_is_unknown()
    {
        Assert.Equal(UpdateState.UpToDate, WingetOutput.ParseUpgradeAvailable(Git, Run(WingetOutput.NoApplicationsFound, "")).State);
        Assert.Equal(UpdateState.Unknown, WingetOutput.ParseUpgradeAvailable(Git, Run(unchecked((int)0x8A150107), GitListing)).State);
        Assert.Equal(UpdateState.Unknown, WingetOutput.ParseUpgradeAvailable(Git, WingetRunResult.NotLaunched).State);
        Assert.Equal(UpdateState.Unknown, WingetOutput.ParseUpgradeAvailable(Git, WingetRunResult.Refused("not this user")).State);
    }

    [Theory]
    [InlineData(0, InstallStatus.Succeeded)]
    [InlineData(unchecked((int)0x8A15002B), InstallStatus.AlreadyInstalled)]
    [InlineData(unchecked((int)0x8A150109), InstallStatus.SucceededRebootRequired)]
    [InlineData(unchecked((int)0x8A150101), InstallStatus.Failed)]
    [InlineData(unchecked((int)0x8A150103), InstallStatus.Failed)]
    [InlineData(unchecked((int)0x8A150014), InstallStatus.Failed)]
    [InlineData(unchecked((int)0x80004005), InstallStatus.Failed)]
    [InlineData(-1, InstallStatus.Failed)]
    public void Upgrade_exit_codes_map_to_a_status(int exit, InstallStatus expected)
    {
        var (status, detail) = WingetOutput.DescribeUpgrade(exit);

        Assert.Equal(expected, status);
        Assert.NotEmpty(detail);
    }

    [Fact]
    public void A_running_app_is_told_to_close()
    {
        var (_, detail) = WingetOutput.DescribeUpgrade(unchecked((int)0x8A150101));

        Assert.Contains("close", detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("0x8A150101", detail, StringComparison.Ordinal);
    }

    /// <summary>A scripted winget: which ids have an update waiting, and whether an upgrade really clears it.</summary>
    private sealed class FakeWinget(bool available, params string[] pendingIds) : IWingetRunner
    {
        private readonly HashSet<string> _pending = [.. pendingIds];
        public int ExitCode { get; set; }
        public bool UpgradeClearsUpdate { get; set; } = true;
        public bool Unreachable { get; set; }
        public List<string> Upgraded { get; } = [];
        public List<string> Listed { get; } = [];
        public CancellationTokenSource? CancelDuringUpgrade { get; set; }

        public bool IsAvailable() => available;

        public WingetRunResult List(AppPackage package) =>
            new(true, 0, $"Name Id Version\n---\n{package.Name} {package.WingetId} 1.0 winget");

        public WingetRunResult ListUpgradeAvailable(AppPackage package)
        {
            Listed.Add(package.WingetId);
            if (Unreachable)
            {
                return new WingetRunResult(true, unchecked((int)0x8A150107), "");
            }

            return _pending.Contains(package.WingetId)
                ? new WingetRunResult(true, 0, $"Name Id Version Available\n---\n{package.Name} {package.WingetId} 1.0 2.0\n1 upgrades available.")
                : new WingetRunResult(true, 0, "No installed package found matching input criteria.");
        }

        public WingetRunResult Install(AppPackage package, Action<string>? onOutput, CancellationToken cancellationToken) => WingetRunResult.NotLaunched;

        public WingetRunResult Upgrade(AppPackage package, Action<string>? onOutput, CancellationToken cancellationToken)
        {
            Upgraded.Add(package.WingetId);
            onOutput?.Invoke("-");
            onOutput?.Invoke($"fake upgrade {package.WingetId}");
            CancelDuringUpgrade?.Cancel();
            if (ExitCode == 0 && UpgradeClearsUpdate)
            {
                _pending.Remove(package.WingetId);
            }

            return new WingetRunResult(true, ExitCode);
        }
    }

    private static AppPlanItem Installed(AppPackage p) => new(p, InstallState.Installed);

    [Fact]
    public void Only_installed_apps_that_Mesura_updates_are_asked_about()
    {
        var winget = new FakeWinget(true, "Git.Git");
        var checks = new AppInstallService(winget).FindUpdates(
            [Installed(Git), Installed(SevenZip), Installed(Brave), new AppPlanItem(AppCatalog.Default.ById("vlc")!, InstallState.NotInstalled)]);

        Assert.Equal(["7zip.7zip", "Git.Git"], winget.Listed.Order(StringComparer.Ordinal).ToList()); // run in parallel: order is not the plan's
        Assert.Equal(UpdateState.Available, checks.Single(c => c.Package.Id == "git").State);
        Assert.Equal(UpdateState.UpToDate, checks.Single(c => c.Package.Id == "sevenzip").State);
        Assert.DoesNotContain(checks, c => c.Package.Id is "brave" or "vlc"); // self-updating, and not installed: no answer, so no action
    }

    [Fact]
    public void Without_winget_the_answer_is_unknown_never_up_to_date()
    {
        var winget = new FakeWinget(false, "Git.Git");
        var checks = new AppInstallService(winget).FindUpdates([Installed(Git)]);

        Assert.Equal(UpdateState.Unknown, Assert.Single(checks).State);
        Assert.Empty(winget.Listed);
    }

    [Fact]
    public void A_source_error_is_unknown_for_that_app()
    {
        var checks = new AppInstallService(new FakeWinget(true) { Unreachable = true }).FindUpdates([Installed(Git)]);

        Assert.Equal(UpdateState.Unknown, Assert.Single(checks).State);
    }

    [Fact]
    public void An_update_that_took_is_reported_with_the_versions()
    {
        var winget = new FakeWinget(true, "Git.Git");
        var progress = new List<string>();

        var outcome = Assert.Single(new AppInstallService(winget).Upgrade([Git], (_, line) => progress.Add(line), CancellationToken.None));

        Assert.Equal(InstallStatus.Succeeded, outcome.Status);
        Assert.Equal("Updated 1.0 to 2.0.", outcome.Detail);
        Assert.Equal(["Git.Git"], winget.Upgraded);
        Assert.Contains("fake upgrade Git.Git", progress);
        Assert.DoesNotContain("-", progress); // spinner frames are not progress
    }

    [Fact]
    public void A_zero_exit_that_leaves_the_update_on_offer_is_not_called_updated()
    {
        var winget = new FakeWinget(true, "Git.Git") { UpgradeClearsUpdate = false };

        var outcome = Assert.Single(new AppInstallService(winget).Upgrade([Git], null, CancellationToken.None));

        Assert.Equal(InstallStatus.Failed, outcome.Status);
        Assert.Contains("2.0", outcome.Detail, StringComparison.Ordinal);
        Assert.Contains("still offers", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failed_upgrade_is_a_failure_with_the_code_and_the_next_app_still_runs()
    {
        var winget = new FakeWinget(true, "Git.Git", "7zip.7zip") { ExitCode = unchecked((int)0x8A150101) };

        var outcomes = new AppInstallService(winget).Upgrade([Git, SevenZip], null, CancellationToken.None);

        Assert.All(outcomes, o => Assert.Equal(InstallStatus.Failed, o.Status));
        Assert.Equal(["Git.Git", "7zip.7zip"], winget.Upgraded);
        Assert.Contains("0x8A150101", outcomes[0].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void An_app_Mesura_does_not_update_is_refused_without_running_anything()
    {
        var winget = new FakeWinget(true, "Brave.Brave");

        var outcome = Assert.Single(new AppInstallService(winget).Upgrade([Brave], null, CancellationToken.None));

        Assert.Equal(InstallStatus.Failed, outcome.Status);
        Assert.Empty(winget.Upgraded);
    }

    [Fact]
    public void Cancelling_stops_before_the_next_app()
    {
        using var cancel = new CancellationTokenSource();
        var winget = new FakeWinget(true, "Git.Git", "7zip.7zip") { CancelDuringUpgrade = cancel };

        var outcomes = new AppInstallService(winget).Upgrade([Git, SevenZip], null, cancel.Token);

        Assert.Equal([InstallStatus.Cancelled, InstallStatus.Cancelled], outcomes.Select(o => o.Status));
        Assert.Equal(["Git.Git"], winget.Upgraded);
    }

    [Fact]
    public void Without_winget_nothing_is_attempted()
    {
        var winget = new FakeWinget(false, "Git.Git");

        var outcome = Assert.Single(new AppInstallService(winget).Upgrade([Git], null, CancellationToken.None));

        Assert.Equal(InstallStatus.WingetUnavailable, outcome.Status);
        Assert.Empty(winget.Upgraded);
    }

    [Fact]
    public void The_catalog_offers_updates_only_for_apps_that_do_not_update_themselves()
    {
        var upgradable = AppCatalog.Default.Packages.Where(p => p.Upgradable).Select(p => p.Id).Order().ToList();

        Assert.Equal(["firefox", "git", "notepadpp", "obs", "sevenzip", "vlc", "vscode"], upgradable);
        Assert.All(AppCatalog.Default.Packages.Where(p => p.Upgradable), p => Assert.Equal(AppSource.Winget, p.Source));
        foreach (var selfUpdating in new[] { "brave", "chrome", "discord", "spotify", "telegram", "ayugram", "steam", "medal", "vencord", "nvpi", "nvcleanstall" })
        {
            Assert.False(AppCatalog.Default.ById(selfUpdating)!.Upgradable, selfUpdating);
        }
    }
}
