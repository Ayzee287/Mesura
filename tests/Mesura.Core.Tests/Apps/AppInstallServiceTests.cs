using Mesura.Core.Apps;

namespace Mesura.Core.Tests.Apps;

public sealed class AppInstallServiceTests
{
    /// <summary>A scripted winget stand-in: fixed availability, canned list output, and recorded install calls.</summary>
    private sealed class FakeWinget(bool available, params string[] installedIds) : IWingetRunner
    {
        private readonly Dictionary<string, int> _exit = new(StringComparer.Ordinal);
        public List<string> Installed { get; } = [];
        public Action? DuringInstall { get; set; }

        public FakeWinget WithExit(string wingetId, int code) { _exit[wingetId] = code; return this; }

        public bool IsAvailable() => available;
        public WingetRunResult List(AppPackage package) =>
            installedIds.Contains(package.WingetId)
                ? new WingetRunResult(true, 0, $"Name Id Version Source\n---------------\n{package.Name} {package.WingetId} 1.0 winget")
                : new WingetRunResult(true, WingetOutput.NoApplicationsFound, "No installed package found matching input criteria.");

        public WingetRunResult Install(AppPackage package, Action<string>? onOutput, CancellationToken cancellationToken)
        {
            Installed.Add(package.WingetId);
            onOutput?.Invoke("-");
            onOutput?.Invoke($"fake install {package.WingetId}");
            DuringInstall?.Invoke();
            return new WingetRunResult(true, _exit.GetValueOrDefault(package.WingetId, 0));
        }
    }

    private static readonly AppPackage Brave = AppCatalog.Default.ById("brave")!;
    private static readonly AppPackage Spotify = AppCatalog.Default.ById("spotify")!;
    private static readonly AppPackage Vencord = AppCatalog.Default.ById("vencord")!;
    private static readonly AppPackage ManualTool = new("tool", "", "Tool", "Utility", AppSource.Manual, Url: "https://example.org/tool");

    private static AppPlanItem Item(AppPackage p, InstallState s) => new(p, s);

    [Fact]
    public void Build_plan_detects_installed_and_not_installed()
    {
        var plan = new AppInstallService(new FakeWinget(true, "Brave.Brave")).BuildPlan([Brave, Spotify, Vencord]);

        Assert.Equal(InstallState.Installed, plan.Single(p => p.Package.Id == "brave").State);
        Assert.Equal(InstallState.NotInstalled, plan.Single(p => p.Package.Id == "spotify").State);
        Assert.Equal(InstallState.Unknown, plan.Single(p => p.Package.Id == "vencord").State); // not detectable
    }

    [Fact]
    public void Build_plan_is_unknown_without_winget_and_drops_duplicates()
    {
        var plan = new AppInstallService(new FakeWinget(false, "Brave.Brave")).BuildPlan([Brave, Brave, Spotify]);

        Assert.Equal(2, plan.Count);
        Assert.All(plan, p => Assert.Equal(InstallState.Unknown, p.State));
        Assert.All(plan, p => Assert.Equal(UnknownReason.NoWinget, p.Reason));
    }

    [Fact]
    public void Every_unknown_in_a_plan_has_a_reason_and_a_known_state_has_none()
    {
        var plan = new AppInstallService(new FakeWinget(true, "Brave.Brave")).BuildPlan([Brave, Spotify, Vencord, ManualTool]);

        Assert.Equal(UnknownReason.None, plan.Single(p => p.Package.Id == "brave").Reason);
        Assert.Equal(UnknownReason.None, plan.Single(p => p.Package.Id == "spotify").Reason);
        Assert.Equal(UnknownReason.NotDetectable, plan.Single(p => p.Package.Id == "vencord").Reason);
        Assert.Equal(UnknownReason.NotDetectable, plan.Single(p => p.Package.Id == "tool").Reason);
        Assert.All(plan.Where(p => p.State == InstallState.Unknown), p => Assert.NotEqual(UnknownReason.None, p.Reason));
    }

    [Fact]
    public void A_winget_that_cannot_be_started_for_one_app_is_that_apps_reason_not_everyones()
    {
        var plan = new AppInstallService(new ScriptedWinget(new Dictionary<string, WingetRunResult>
        {
            ["Brave.Brave"] = WingetRunResult.Refused("Mesura couldn't start winget as the signed-in user (Windows error 1314)."),
            ["Spotify.Spotify"] = new WingetRunResult(true, WingetOutput.NoApplicationsFound),
        })).BuildPlan([Brave, Spotify]);

        var brave = plan.Single(p => p.Package.Id == "brave");
        Assert.Equal((InstallState.Unknown, UnknownReason.NotStarted), (brave.State, brave.Reason));
        Assert.Contains("1314", brave.Technical);
        Assert.Equal(InstallState.NotInstalled, plan.Single(p => p.Package.Id == "spotify").State);
    }

    [Fact]
    public void A_refusal_of_the_runner_makes_every_unknown_a_not_started_with_its_words()
    {
        var plan = new AppInstallService(new RefusingWinget()).BuildPlan([Brave, Spotify]);

        Assert.All(plan, p => Assert.Equal(UnknownReason.NotStarted, p.Reason));
        Assert.All(plan, p => Assert.Contains("1058", p.Technical));
    }

    private sealed class RefusingWinget : IWingetRunner
    {
        public bool IsAvailable() => false;
        public string Refusal => "Secondary Logon is disabled (Windows error 1058).";
        public WingetRunResult List(AppPackage package) => throw new NotSupportedException();
        public WingetRunResult Install(AppPackage package, Action<string>? onOutput, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class ScriptedWinget(Dictionary<string, WingetRunResult> answers) : IWingetRunner
    {
        public bool IsAvailable() => true;
        public WingetRunResult List(AppPackage package) => answers[package.WingetId];
        public WingetRunResult Install(AppPackage package, Action<string>? onOutput, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    [Fact]
    public void Skip_installed_does_not_reinstall()
    {
        var winget = new FakeWinget(true);
        var outcomes = new AppInstallService(winget).Install(
            [Item(Brave, InstallState.Installed), Item(Spotify, InstallState.NotInstalled)], skipInstalled: true, null, CancellationToken.None);

        Assert.Equal(InstallStatus.AlreadyInstalled, outcomes.Single(o => o.AppId == "brave").Status);
        Assert.Equal(InstallStatus.Succeeded, outcomes.Single(o => o.AppId == "spotify").Status);
        Assert.Equal(["Spotify.Spotify"], winget.Installed);
    }

    [Fact]
    public void A_failure_does_not_break_the_rest_of_the_batch()
    {
        var winget = new FakeWinget(true).WithExit("Brave.Brave", unchecked((int)0x80004005));
        var outcomes = new AppInstallService(winget).Install(
            [Item(Brave, InstallState.NotInstalled), Item(Spotify, InstallState.NotInstalled)], skipInstalled: false, null, CancellationToken.None);

        Assert.Equal(InstallStatus.Failed, outcomes[0].Status);
        Assert.Equal(InstallStatus.Succeeded, outcomes[1].Status);
        Assert.Equal(["Brave.Brave", "Spotify.Spotify"], winget.Installed);
    }

    [Fact]
    public void Winget_unavailable_reports_every_app_and_manual_entries_still_report_manual()
    {
        var winget = new FakeWinget(false);
        var outcomes = new AppInstallService(winget).Install(
            [Item(Brave, InstallState.Unknown), Item(ManualTool, InstallState.Unknown)], skipInstalled: true, null, CancellationToken.None);

        Assert.Equal(InstallStatus.WingetUnavailable, outcomes[0].Status);
        Assert.Equal(InstallStatus.Manual, outcomes[1].Status);
        Assert.Contains("https://example.org/tool", outcomes[1].Detail, StringComparison.Ordinal);
        Assert.Empty(winget.Installed);
    }

    [Fact]
    public void Cancelling_mid_batch_marks_the_running_and_remaining_apps_cancelled()
    {
        using var cts = new CancellationTokenSource();
        var winget = new FakeWinget(true) { DuringInstall = cts.Cancel };
        var outcomes = new AppInstallService(winget).Install(
            [Item(Brave, InstallState.NotInstalled), Item(Spotify, InstallState.NotInstalled)], skipInstalled: false, null, cts.Token);

        Assert.All(outcomes, o => Assert.Equal(InstallStatus.Cancelled, o.Status));
        Assert.Equal(["Brave.Brave"], winget.Installed); // Spotify never started
    }

    [Fact]
    public void Progress_omits_spinner_noise()
    {
        var lines = new List<string>();
        new AppInstallService(new FakeWinget(true)).Install(
            [Item(Brave, InstallState.NotInstalled)], skipInstalled: false, (_, line) => lines.Add(line), CancellationToken.None);

        Assert.DoesNotContain("-", lines);
        Assert.Contains("fake install Brave.Brave", lines);
    }
}
