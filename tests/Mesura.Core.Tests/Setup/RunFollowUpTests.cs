using Mesura.Core.Apps;
using Mesura.Core.Setup;
using Mesura.Core.Windows;

namespace Mesura.Core.Tests.Setup;

public sealed class RunFollowUpTests
{
    private static WindowsOpOutcome Step(string name, WindowsOpStatus status, Activation activation = Activation.Immediate, string detail = "") =>
        new(name.ToLowerInvariant(), name, status, detail, activation, []);

    private static ConfigImportOutcome Config(string app, ConfigImportStatus status, string[]? missing = null, string[]? waiting = null) =>
        new(app.ToLowerInvariant(), app, status, [], waiting ?? [], missing ?? [], [], status == ConfigImportStatus.Failed ? "file locked" : "");

    [Fact]
    public void A_clean_run_leaves_nothing_to_do()
    {
        var run = SetupRun.Create("id", "profile", "P", "a", "b",
            [new InstallOutcome("brave", "Brave.Brave", InstallStatus.Succeeded, ""), new InstallOutcome("steam", "Valve.Steam", InstallStatus.AlreadyInstalled, "")],
            [Config("Brave", ConfigImportStatus.Applied)],
            [Step("Show file name extensions", WindowsOpStatus.Applied), Step("Hide Task View", WindowsOpStatus.AlreadyApplied, Activation.ExplorerRestart)]);

        Assert.Empty(RunFollowUp.For(run, id => id));
    }

    [Fact]
    public void Each_kind_of_outcome_reads_differently_and_the_run_status_follows_the_worst_of_them()
    {
        Assert.Equal(
            ["installed", "restart needed", "already done", "manual", "cancelled", "no winget", "failed"],
            new[] { InstallStatus.Succeeded, InstallStatus.SucceededRebootRequired, InstallStatus.AlreadyInstalled, InstallStatus.Manual,
                InstallStatus.Cancelled, InstallStatus.WingetUnavailable, InstallStatus.Failed }.Select(StepWords.Install));
        Assert.Equal("waiting", StepWords.Config(Config("Spotify", ConfigImportStatus.Unchanged, missing: ["prefs"]))); // never started
        Assert.Equal("partly applied", StepWords.Config(Config("Steam", ConfigImportStatus.Applied, waiting: ["account"])));
        Assert.Equal("already done", StepWords.Config(Config("Brave", ConfigImportStatus.Unchanged)));
        Assert.Equal("failed", StepWords.Config(Config("Discord", ConfigImportStatus.Failed)));
        Assert.Equal("not applicable", StepWords.Windows(WindowsOpStatus.Inapplicable)); // not on this PC: not a failure
        Assert.Equal("needs admin", StepWords.Windows(WindowsOpStatus.NeedsElevation));

        SetupRun Run(params InstallOutcome[] apps) => SetupRun.Create("id", "apps-install", null, "a", "b", apps, []);
        Assert.Equal(RunStatus.Succeeded, RunFollowUp.Status(Run(new InstallOutcome("a", "A", InstallStatus.SucceededRebootRequired, ""))));
        Assert.Equal(RunStatus.Incomplete, RunFollowUp.Status(Run(new InstallOutcome("a", "A", InstallStatus.Cancelled, ""))));
        Assert.Equal(RunStatus.NeedsAttention, RunFollowUp.Status(Run(
            new InstallOutcome("a", "A", InstallStatus.Cancelled, ""), new InstallOutcome("b", "B", InstallStatus.Failed, "exit 1"))));
        Assert.True(Run(new InstallOutcome("a", "A", InstallStatus.AlreadyInstalled, ""), new InstallOutcome("m", "", InstallStatus.Manual, "")).ChangedNothing);
        Assert.False(Run(new InstallOutcome("a", "A", InstallStatus.Succeeded, "")).ChangedNothing);
    }

    [Fact]
    public void A_fresh_install_run_says_what_waits_on_the_user_what_failed_and_what_needs_a_restart()
    {
        var run = SetupRun.Create("id", "profile", "P", "a", "b",
            [
                new InstallOutcome("obs", "OBSProject.OBSStudio", InstallStatus.Failed, "winget exit code 0x8A15002B"),
                new InstallOutcome("nvpi", "Orbmu2k.nvidiaProfileInspector", InstallStatus.SucceededRebootRequired, ""),
            ],
            [
                Config("Brave", ConfigImportStatus.SkippedAppRunning),
                Config("Spotify", ConfigImportStatus.Unchanged, missing: ["prefs"]),
                Config("Steam", ConfigImportStatus.Applied, waiting: ["account"]),
                Config("Discord", ConfigImportStatus.Failed),
            ],
            [
                Step("High performance power plan", WindowsOpStatus.NeedsElevation),
                Step("Turn off Fast Startup", WindowsOpStatus.NeedsElevation),
                Step("Hide the Widgets button", WindowsOpStatus.Failed, detail: "Windows didn't keep it."),
                Step("Turn off Fast Startup again", WindowsOpStatus.Applied, Activation.Reboot),
                Step("Show file name extensions", WindowsOpStatus.Applied, Activation.ExplorerRestart),
            ]);

        var items = RunFollowUp.For(run, id => id == "obs" ? "OBS Studio" : id);

        Assert.Contains(items, i => i is { Kind: FollowUpKind.Problem } && i.Text.StartsWith("OBS Studio wasn't installed", StringComparison.Ordinal));
        Assert.Contains(items, i => i is { Kind: FollowUpKind.Finish } && i.Text.Contains("finish installing nvpi", StringComparison.Ordinal));
        Assert.Contains(items, i => i is { Kind: FollowUpKind.RunAgain } && i.Text.StartsWith("Brave was running", StringComparison.Ordinal));
        Assert.Contains(items, i => i is { Kind: FollowUpKind.RunAgain } && i.Text.StartsWith("Spotify hasn't created", StringComparison.Ordinal));
        Assert.Contains(items, i => i is { Kind: FollowUpKind.RunAgain } && i.Text.StartsWith("Steam: some settings wait", StringComparison.Ordinal));
        Assert.Contains(items, i => i is { Kind: FollowUpKind.Problem } && i.Text.Contains("file locked", StringComparison.Ordinal));
        var admin = Assert.Single(items, i => i.Text.Contains("administrator", StringComparison.Ordinal));
        Assert.Equal(FollowUpKind.RunAgain, admin.Kind);
        Assert.StartsWith("2 Windows setting(s)", admin.Text, StringComparison.Ordinal); // one line for all of them
        Assert.Contains(items, i => i is { Kind: FollowUpKind.Problem } && i.Text == "Hide the Widgets button: Windows didn't keep it.");
        Assert.Contains(items, i => i is { Kind: FollowUpKind.Finish } && i.Text.StartsWith("Restart Windows to finish: Turn off Fast Startup again", StringComparison.Ordinal));
        Assert.Contains(items, i => i is { Kind: FollowUpKind.Finish } && i.Text.StartsWith("Restart Explorer", StringComparison.Ordinal));
    }
}
