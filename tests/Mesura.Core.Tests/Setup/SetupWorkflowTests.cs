using Mesura.Core.Apps;
using Mesura.Core.Setup;
using Mesura.Core.Windows;

namespace Mesura.Core.Tests.Setup;

/// <summary>
/// The setup's own records (D031): the unfinished setup's steps in run order and its JSON, a record that grows over
/// several sittings, the Visual C++ runtimes Mesura offers and how their uninstall entries are recognised.
/// </summary>
public sealed class SetupWorkflowTests
{
    private static PendingSetup Setup() => PendingSetup.Create("20261023T120000Z-setup", "2026-10-23T12:00:00Z", "recommended-gaming", "defaults",
        [
            new SetupStep(StepKind.NvidiaRestore, []),
            new SetupStep(StepKind.Windows, ["explorer.show-extensions"]),
            new SetupStep(StepKind.Apps, []),
            new SetupStep(StepKind.Runtimes, ["vcredist-2013-x64"]),
            new SetupStep(StepKind.NvidiaDriver, []),
            new SetupStep(StepKind.NvidiaBackup, []),
        ],
        new NvidiaTarget("NVIDIA GeForce RTX 4070", "560.94", "621.01", ["display-driver", "hd-audio"], CleanInstall: false));

    [Fact]
    public void Steps_run_in_their_order_and_an_empty_step_is_left_out()
    {
        var setup = Setup();
        Assert.Equal([StepKind.Runtimes, StepKind.Windows, StepKind.NvidiaBackup, StepKind.NvidiaDriver, StepKind.NvidiaRestore],
            setup.Steps.Select(s => s.Kind));
        Assert.Equal(StepKind.Runtimes, setup.Next!.Kind);
        Assert.False(setup.Finished);
    }

    [Fact]
    public void A_waiting_step_comes_next_and_finished_steps_never_run_again()
    {
        var setup = Setup()
            .With(StepKind.Runtimes, StepState.Done)
            .With(StepKind.Windows, StepState.Failed, "1 of 1 didn't")
            .With(StepKind.NvidiaBackup, StepState.Done, value: "nvidia-profiles-before.nip")
            .With(StepKind.NvidiaDriver, StepState.Waiting);
        Assert.Equal(StepKind.NvidiaDriver, setup.Next!.Kind);
        Assert.Equal("nvidia-profiles-before.nip", setup.Step(StepKind.NvidiaBackup)!.Value);

        setup = setup.With(StepKind.NvidiaDriver, StepState.Done, value: "621.01");
        Assert.Equal(StepKind.NvidiaRestore, setup.Next!.Kind);
        Assert.Equal("nvidia-profiles-before.nip", setup.Step(StepKind.NvidiaBackup)!.Value); // a later With keeps earlier values

        setup = setup.With(StepKind.NvidiaRestore, StepState.Skipped);
        Assert.True(setup.Finished);
        Assert.Null(setup.Next);
    }

    [Fact]
    public void An_unfinished_setup_round_trips_and_a_foreign_file_is_refused()
    {
        var setup = Setup().With(StepKind.Runtimes, StepState.Done, "1 runtime(s) installed.");
        var back = PendingSetup.FromJson(setup.ToJson());
        Assert.Equal(setup.RunId, back.RunId);
        Assert.Equal(setup.Steps, back.Steps, (a, b) => a.Kind == b.Kind && a.State == b.State && a.Detail == b.Detail && a.Items.SequenceEqual(b.Items));
        Assert.Equal(["display-driver", "hd-audio"], back.Nvidia!.Components);
        Assert.Contains("\"state\": \"Done\"", setup.ToJson(), StringComparison.Ordinal);

        Assert.Throws<FormatException>(() => PendingSetup.FromJson("{ \"kind\": \"Mesura.SetupRun\", \"schemaVersion\": 1 }"));
        Assert.Throws<FormatException>(() => PendingSetup.FromJson(setup.ToJson().Replace("\"schemaVersion\": 1", "\"schemaVersion\": 9", StringComparison.Ordinal)));
        Assert.Throws<FormatException>(() => PendingSetup.FromJson("not json"));
    }

    [Fact]
    public void A_setup_over_several_sittings_is_one_record_where_a_later_entry_replaces_an_earlier_one()
    {
        var first = SetupRun.Create("20261023T120000Z-setup", "setup", null, "2026-10-23T12:00:00Z", "2026-10-23T12:05:00Z",
            [new InstallOutcome("brave", "Brave.Brave", InstallStatus.Cancelled, "Cancelled.")], [], [],
            [new SetupStepOutcome(StepKind.NvidiaDriver, StepState.Waiting, "NVCleanstall started.")]);
        var later = SetupRun.Create("20261023T120000Z-setup", "setup", null, "2026-10-23T12:00:00Z", "2026-10-23T13:00:00Z",
            [new InstallOutcome("brave", "Brave.Brave", InstallStatus.Succeeded, "Installed.")], [], [],
            [new SetupStepOutcome(StepKind.NvidiaDriver, StepState.Done, "Driver 560.94 -> 621.01.", "621.01"),
             new SetupStepOutcome(StepKind.NvidiaRestore, StepState.Skipped, "No backup.")]);

        var run = first.Then(later);
        Assert.Equal("2026-10-23T12:00:00Z", run.StartedUtc);
        Assert.Equal("2026-10-23T13:00:00Z", run.FinishedUtc);
        Assert.Equal(InstallStatus.Succeeded, Assert.Single(run.Apps).Status);
        Assert.Equal([StepState.Done, StepState.Skipped], run.SetupSteps.Select(s => s.State));
        Assert.True(run.Ok);

        // The record keeps the steps through its JSON; a record from before setups existed has none.
        Assert.Equal(2, SetupRun.FromJson(run.ToJson()).SetupSteps.Count);
        Assert.Empty(SetupRun.FromJson(SetupRun.Create("x-apps-install", "apps-install", null, "a", "b", [], []).ToJson()).SetupSteps);
    }

    [Fact]
    public void A_re_run_that_finds_the_first_sittings_changes_done_keeps_them_for_undo()
    {
        var change = new RecordedChange(ChangeKind.Registry, @"HKCU\Software\X\Y", "HKCU", @"Software\X", "Y", RegistryKind.DWord, "0", "1");
        var first = SetupRun.Create("r", "setup", null, "a", "b",
            [new InstallOutcome("brave", "Brave.Brave", InstallStatus.SucceededRebootRequired, "Installed.")], [],
            [new WindowsOpOutcome("op.a", "A", WindowsOpStatus.Applied, "", Activation.Reboot, [change])]);
        var resumed = SetupRun.Create("r", "setup", null, "a", "c",
            [new InstallOutcome("brave", "Brave.Brave", InstallStatus.AlreadyInstalled, "Already installed; skipped."),
             new InstallOutcome("steam", "Valve.Steam", InstallStatus.Succeeded, "Installed.")], [],
            [new WindowsOpOutcome("op.a", "A", WindowsOpStatus.AlreadyApplied, "", Activation.Reboot, []),
             new WindowsOpOutcome("op.b", "B", WindowsOpStatus.Failed, "denied", Activation.Immediate, [])]);

        var run = first.Then(resumed);

        Assert.Equal(InstallStatus.SucceededRebootRequired, run.Apps.Single(a => a.AppId == "brave").Status);
        Assert.Equal(WindowsOpStatus.Applied, run.WindowsSteps.Single(w => w.OperationId == "op.a").Status);
        Assert.Single(run.WindowsSteps.Single(w => w.OperationId == "op.a").Changes);
        Assert.Equal(2, run.Apps.Count);
        Assert.Equal(2, run.WindowsSteps.Count);

        // A later entry that did something (or failed) still replaces the earlier one.
        var retried = first.Then(SetupRun.Create("r", "setup", null, "a", "d", [], [],
            [new WindowsOpOutcome("op.a", "A", WindowsOpStatus.Failed, "denied", Activation.Reboot, [])]));
        Assert.Equal(WindowsOpStatus.Failed, Assert.Single(retried.WindowsSteps).Status);
    }

    [Fact]
    public void A_waiting_or_failed_step_is_what_is_left_and_a_skipped_one_changed_nothing()
    {
        var waiting = SetupRun.Create("r", "setup", null, "a", "b", [], [], [], [new SetupStepOutcome(StepKind.NvidiaDriver, StepState.Waiting, "")]);
        Assert.Equal(RunStatus.Incomplete, RunFollowUp.Status(waiting));
        var failed = SetupRun.Create("r", "setup", null, "a", "b", [], [], [], [new SetupStepOutcome(StepKind.NvidiaRestore, StepState.Failed, "2 setting(s) aren't in place")]);
        Assert.Equal(RunStatus.NeedsAttention, RunFollowUp.Status(failed));
        Assert.Contains(RunFollowUp.For(failed, id => id), f => f.Text.Contains("NVIDIA profile restore", StringComparison.Ordinal));
        Assert.True(SetupRun.Create("r", "setup", null, "a", "b", [], [], [], [new SetupStepOutcome(StepKind.NvidiaBackup, StepState.Skipped, "")]).ChangedNothing);
    }

    [Fact]
    public void A_setup_profile_carries_the_nvidia_configuration_and_refuses_an_unknown_one()
    {
        var profile = new SetupProfile("PC", "", ["brave"], [], null, [], [], [], Mesura.Core.Nvidia.NvidiaDriverConfig.Default);
        var back = SetupProfile.Parse(profile.ToJson(), AppCatalog.Default);
        Assert.NotNull(back.Nvidia);
        Assert.True(back.Nvidia!.IsDefault);
        Assert.Contains("\"interruptPolicy\": \"Default\"", profile.ToJson(), StringComparison.Ordinal);

        // A profile with only an NVIDIA configuration is a profile.
        Assert.True(SetupProfile.Parse(new SetupProfile("NV", "", [], [], null, [], [], [], Mesura.Core.Nvidia.NvidiaDriverConfig.Default).ToJson(),
            AppCatalog.Default).SelectsAnything);

        var unknownTweak = profile.ToJson().Replace("\"disable-hdcp\"", "\"made-up\"", StringComparison.Ordinal);
        Assert.Contains("made-up", Assert.Throws<FormatException>(() => SetupProfile.Parse(unknownTweak, AppCatalog.Default)).Message, StringComparison.Ordinal);
        var otherFile = profile.ToJson().Replace(Mesura.Core.Nvidia.NvidiaProfiles.Designated.Sha256, new string('A', 64), StringComparison.Ordinal);
        Assert.Contains("SHA-256", Assert.Throws<FormatException>(() => SetupProfile.Parse(otherFile, AppCatalog.Default)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Microsofts_runtimes_are_offered_and_2005_is_opt_in()
    {
        Assert.Equal(12, VcRuntimes.All.Count);
        Assert.All(VcRuntimes.All, r => Assert.StartsWith("Microsoft.VCRedist.", r.WingetId, StringComparison.Ordinal));
        Assert.All(VcRuntimes.All, r => Assert.Equal(r.Id, r.Package.Detector));
        Assert.All(VcRuntimes.All, r => Assert.False(r.Package.Listed));
        Assert.Equal(["vcredist-2015plus-x64", "vcredist-2015plus-x86"], VcRuntimes.All.Where(r => r.Supported).Select(r => r.Id));
        Assert.Equal(["vcredist-2005-x64", "vcredist-2005-x86"], VcRuntimes.All.Where(r => !r.Default).Select(r => r.Id));
        Assert.DoesNotContain(VcRuntimes.All, r => r.WingetId.Contains("abbodi", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("Microsoft Visual C++ 2005 Redistributable", "vcredist-2005-x86")]
    [InlineData("Microsoft Visual C++ 2005 Redistributable (x64)", "vcredist-2005-x64")]
    [InlineData("Microsoft Visual C++ 2008 Redistributable - x64 9.0.30729.7523", "vcredist-2008-x64")]
    [InlineData("Microsoft Visual C++ 2010  x86 Redistributable - 10.0.40219", "vcredist-2010-x86")]
    [InlineData("Microsoft Visual C++ 2012 x64 Minimum Runtime - 11.0.61135", "vcredist-2012-x64")]
    [InlineData("Microsoft Visual C++ 2013 x86 Additional Runtime - 12.0.40664", "vcredist-2013-x86")]
    [InlineData("Microsoft Visual C++ 2013 Redistributable (x64) - 12.0.40664", "vcredist-2013-x64")]
    public void Older_runtimes_are_recognised_by_their_uninstall_entry(string name, string id) =>
        Assert.Equal(id, VcRuntimes.FromDisplayName(name)?.Id);

    [Theory]
    [InlineData("Microsoft Visual C++ 2022 X64 Minimum Runtime - 14.51.36247")] // v14 is read from its own key
    [InlineData("Microsoft Visual C++ v14 Redistributable (x64) - 14.51.36247")]
    [InlineData("Microsoft Visual C++ 2010  x64 Debug Runtime - 10.0.40219")]
    [InlineData("Microsoft Visual C++ 2012 Redistributable")]
    [InlineData("Microsoft Visual Studio 2013")]
    [InlineData(null)]
    public void Other_entries_are_not_a_runtime_mesura_offers(string? name) => Assert.Null(VcRuntimes.FromDisplayName(name));

    [Fact]
    public void A_runtime_package_installs_through_the_existing_winget_installer()
    {
        var runtime = VcRuntimes.ById("vcredist-2015plus-x64")!;
        Assert.Equal(AppSource.Winget, runtime.Package.Source);
        Assert.Equal("winget", runtime.Package.SourceArgument);
        Assert.Equal("14.51.36247", VcRuntimes.V14Version(14, 51, 36247));
        Assert.Equal("x86", VcRuntimes.V14("X86")!.Arch);
    }
}
