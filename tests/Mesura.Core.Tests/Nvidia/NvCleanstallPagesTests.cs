using Mesura.Core.Nvidia;

namespace Mesura.Core.Tests.Nvidia;

/// <summary>
/// The automation of NVCleanstall's first pages against a stand-in window that has the pages, ids and list rows recorded from the real NVCleanstall 1.19.0 in Windows Sandbox
/// (docs/sandbox-results/nvcleanstall-first-pages-2026-11-02). They show the automation's decisions; that the real window behaves like the recording is shown by the sandbox run.
/// </summary>
public sealed class NvCleanstallPagesTests
{
    private static readonly string[] Rows =
    [
        "Display Driver", "Legacy Control Panel", "HD Audio via HDMI", "Microsoft Visual C 2017 Runtimes", "PhysX", "USB-C Driver", "FrameView SDK", "Virtual Audio", "Telemetry",
        "NV Platform Controllers", "NVIDIA DLISR", "NV App Components", "NVIDIA App", "NV Container", "ShadowPlay", "NV Backend", "NVIDIA App MessageBus",
    ];

    private sealed class FakeWindow : INvCleanstallWindow
    {
        private string _page = NvCleanstallIds.SourcePage;
        private int _preparingTicks = 2;

        public string Path { get; private set; } = "";
        public bool DiskChosen { get; private set; }
        public Dictionary<string, bool> Ticks { get; } = Rows.Where(r => r != "NV App Components").ToDictionary(r => r, r => r == "Display Driver");
        public List<string> Pressed { get; } = [];
        public bool Open { get; set; } = true;
        public bool KeepPath { get; set; } = true;
        public bool NextDisabled { get; set; }
        public bool IgnoreNext { get; set; }
        public bool TweaksPageExists { get; set; } = true;
        public bool IgnoreTick { get; set; }
        public bool IgnoreBoxTick { get; set; }
        public Dictionary<string, bool> Boxes { get; } = NvCleanstallIds.TweakBoxes.Values.ToDictionary(b => b, _ => false);
        public bool InstallPressed { get; private set; }

        // The expert tweaks are on the page only once "Show Expert Tweaks" is ticked.
        private static readonly HashSet<string> Expert = ["DisableDriverTelemetry", "DisableHDAudioSleepTimer", "EnableMSI", "DisableHDCP", "NvEncPatch", "RunProgram", "RebuildSignatureEAC", "SkipUnsignedDriverWarning", "DisableNvContainer"];

        public bool IsOpen => Open;

        public string PageTitle => _page;

        public bool Has(string id) =>
            id is NvCleanstallIds.Next or NvCleanstallIds.RadioDisk or NvCleanstallIds.DiskPath or NvCleanstallIds.ComponentList or NvCleanstallIds.RunInstaller
            || (Boxes.ContainsKey(id) && (!Expert.Contains(id) || Boxes[NvCleanstallIds.ExpertTweaks]));

        public bool IsTicked(string id) => Boxes[id];

        public bool? SignatureRebuildTicked() => SignatureBox;

        public bool? SignatureBox { get; set; } = true;

        public void Tick(string id, bool on)
        {
            if (!IgnoreBoxTick)
            {
                Boxes[id] = on;
            }
        }

        public bool IsEnabled(string id) => id switch
        {
            NvCleanstallIds.Next => !NextDisabled,
            NvCleanstallIds.DiskPath => DiskChosen,
            _ => true,
        };

        public string Text(string id) => id == NvCleanstallIds.DiskPath ? (KeepPath ? Path : "") : "Next";

        public void Choose(string id) => DiskChosen = id == NvCleanstallIds.RadioDisk || DiskChosen;

        public void SetText(string id, string text) => Path = text;

        public void Press(string id)
        {
            Pressed.Add($"{_page}|{id}");
            if (id == NvCleanstallIds.RunInstaller)
            {
                InstallPressed = true;
                return;
            }

            if (IgnoreNext)
            {
                return;
            }

            _page = _page switch
            {
                NvCleanstallIds.SourcePage => NvCleanstallIds.ComponentsPage,
                NvCleanstallIds.ComponentsPage => NvCleanstallIds.PreparingPage,
                NvCleanstallIds.TweaksPage => NvCleanstallIds.FinishedPage,
                _ => _page,
            };
        }

        public IReadOnlyList<NvListEntry> ListEntries(string id) =>
        [.. Rows.Select(r => r == "NV App Components" ? new NvListEntry(r, null, false) : new NvListEntry(r, Ticks[r], r != "Display Driver"))];

        public void SetChecked(string list, string entry, bool on)
        {
            if (!IgnoreTick)
            {
                Ticks[entry] = on;
            }
        }

        /// <summary>Time passes while NVCleanstall prepares the sources; then the next page (one Mesura has no automation for) shows.</summary>
        public bool Wait(TimeSpan _)
        {
            if (_page == NvCleanstallIds.PreparingPage && --_preparingTicks < 0)
            {
                _page = TweaksPageExists ? NvCleanstallIds.TweaksPage : NvCleanstallIds.PreparingPage;
            }

            return true;
        }
    }

    private const string Package = @"C:\Drivers\621.01-desktop-win10-win11-64bit-international-dch-whql.exe";

    [Fact]
    public void The_owners_configuration_runs_through_every_page_and_stops_at_finished_without_installing()
    {
        var window = new FakeWindow();

        var result = NvCleanstallPages.Run(window, NvidiaDriverConfig.Default, Package, window.Wait);

        Assert.Equal(NvRunStatus.Completed, result.Status);
        Assert.Equal(NvCleanstallIds.FinishedPage, result.StoppedAt);
        Assert.Equal(["Display Driver", "HD Audio via HDMI"], window.Ticks.Where(t => t.Value).Select(t => t.Key));
        Assert.Equal(Package, window.Path);
        Assert.Equal([$"{NvCleanstallIds.SourcePage}|ButtonNext", $"{NvCleanstallIds.ComponentsPage}|ButtonNext", $"{NvCleanstallIds.TweaksPage}|ButtonNext"], window.Pressed);
        Assert.False(window.InstallPressed); // the run ends on the finished page; Install is a separate, confirmed press
    }

    [Fact]
    public void The_tweaks_ticked_are_exactly_the_configurations_and_the_expert_switch_is_ticked_first()
    {
        var window = new FakeWindow();

        NvCleanstallPages.Run(window, NvidiaDriverConfig.Default, Package, window.Wait);

        var wanted = NvidiaDriverConfig.Default.Tweaks.Where(NvCleanstallIds.TweakBoxes.ContainsKey).Select(t => NvCleanstallIds.TweakBoxes[t]).Order().ToList();
        Assert.Equal(wanted, window.Boxes.Where(b => b.Value).Select(b => b.Key).Order());
        Assert.True(window.Boxes["ShowExpertOptions"]);
        Assert.True(window.Boxes["EnableMSI"]); // an expert tweak: only reachable because the expert switch was ticked before it
        Assert.False(window.Boxes["ShowDlssIndicator"]); // not in the configuration: left unticked
    }

    [Fact]
    public void The_run_reports_the_tweaks_and_the_signature_box_as_read_back_for_the_checklist()
    {
        var window = new FakeWindow { SignatureBox = true };

        var result = NvCleanstallPages.Run(window, NvidiaDriverConfig.Default, Package, window.Wait);

        Assert.Equal(NvidiaDriverConfig.Default.Tweaks.Where(NvCleanstallIds.TweakBoxes.ContainsKey).Order(), result.TweaksTicked!.Order());
        Assert.True(result.SignatureRebuild);
    }

    private static NvInstallChecklist Checklist(bool pointOk = true, bool backupOk = true, IReadOnlyList<string>? ticked = null, bool signatureRequired = true, bool? signature = true) =>
        new("made just now", pointOk, "done", backupOk, [.. NvidiaDriverConfig.Default.Tweaks.Where(NvCleanstallIds.TweakBoxes.ContainsKey)],
            ticked ?? [.. NvidiaDriverConfig.Default.Tweaks.Where(NvCleanstallIds.TweakBoxes.ContainsKey)], signatureRequired, signature, "621.01.exe", "560.94", "621.01");

    [Fact]
    public void Install_is_allowed_only_when_the_restore_point_the_backup_the_tweaks_and_the_signature_box_all_hold()
    {
        Assert.True(Checklist().CanInstall);
        Assert.Empty(Checklist().Blockers);

        Assert.False(Checklist(pointOk: false).CanInstall);
        Assert.False(Checklist(backupOk: false).CanInstall);
        Assert.False(Checklist(ticked: ["clean-install"]).CanInstall); // not the configuration's tweaks
        Assert.False(Checklist(signature: false).CanInstall); // "Rebuild digital signature" not ticked though the configuration needs it
        Assert.False(Checklist(signature: null).CanInstall); // box not found: unknown is not "on"
        Assert.True(Checklist(signatureRequired: false, signature: null).CanInstall); // not wanted, so its state does not matter
        Assert.Equal(4, Checklist(pointOk: false, backupOk: false, ticked: [], signature: false).Blockers.Count);
    }

    [Fact]
    public void A_tweak_box_that_does_not_keep_its_tick_fails_the_run_before_the_next_page()
    {
        var window = new FakeWindow { IgnoreBoxTick = true };

        var result = NvCleanstallPages.Run(window, NvidiaDriverConfig.Default, Package, window.Wait);

        Assert.Equal(NvRunStatus.Failed, result.Status);
        Assert.Equal(NvCleanstallIds.TweaksPage, result.StoppedAt);
        Assert.DoesNotContain(window.Pressed, p => p.StartsWith(NvCleanstallIds.TweaksPage, StringComparison.Ordinal));
    }

    [Fact]
    public void Install_is_pressed_only_on_the_finished_page_and_never_elsewhere()
    {
        var window = new FakeWindow();
        Assert.NotNull(NvCleanstallPages.PressInstall(window)); // still on the first page
        Assert.False(window.InstallPressed);

        NvCleanstallPages.Run(window, NvidiaDriverConfig.Default, Package, window.Wait);
        Assert.Null(NvCleanstallPages.PressInstall(window));
        Assert.True(window.InstallPressed);
    }

    [Fact]
    public void A_page_nobody_wrote_automation_for_is_not_touched()
    {
        var window = new FakeWindow();
        window.Press(NvCleanstallIds.Next);
        window.Press(NvCleanstallIds.Next); // Preparing
        var unknown = new UnknownPageWindow(window);

        var result = NvCleanstallPages.Run(unknown, NvidiaDriverConfig.Default, Package, _ => true);

        Assert.Equal(NvRunStatus.StoppedAtUnknownPage, result.Status);
        Assert.Equal("Select Something Else", result.StoppedAt);
        Assert.Empty(unknown.Pressed);
    }

    private sealed class UnknownPageWindow(FakeWindow inner) : INvCleanstallWindow
    {
        public List<string> Pressed { get; } = [];
        public bool IsOpen => true;
        public string PageTitle => "Select Something Else";
        public bool Has(string id) => inner.Has(id);
        public bool IsEnabled(string id) => inner.IsEnabled(id);
        public string Text(string id) => inner.Text(id);
        public void Choose(string id) => Pressed.Add(id);
        public void SetText(string id, string text) => Pressed.Add(id);
        public void Press(string id) => Pressed.Add(id);
        public IReadOnlyList<NvListEntry> ListEntries(string id) => inner.ListEntries(id);
        public void SetChecked(string list, string entry, bool on) => Pressed.Add(entry);
        public bool IsTicked(string id) => inner.IsTicked(id);
        public void Tick(string id, bool on) => Pressed.Add(id);
        public bool? SignatureRebuildTicked() => null;
    }

    [Fact]
    public void A_component_whose_label_differs_only_in_punctuation_is_still_matched()
    {
        var window = new FakeWindow();
        var config = NvidiaDriverConfig.Default with { Components = [NvidiaComponents.DisplayDriver, "vcredist-2017", "nv-container"] };

        NvCleanstallPages.Run(window, config, Package, window.Wait);

        Assert.True(window.Ticks["Microsoft Visual C 2017 Runtimes"]); // Mesura says "C++", NVCleanstall shows "C"
        Assert.True(window.Ticks["NV Container"]);
        Assert.False(window.Ticks["HD Audio via HDMI"]);
    }

    [Fact]
    public void A_path_nvcleanstall_does_not_keep_stops_the_run_before_anything_is_pressed()
    {
        var window = new FakeWindow { KeepPath = false };

        var result = NvCleanstallPages.Run(window, NvidiaDriverConfig.Default, Package, window.Wait);

        Assert.Equal(NvRunStatus.Failed, result.Status);
        Assert.Empty(window.Pressed);
    }

    [Fact]
    public void A_tick_that_is_not_kept_is_found_by_reading_back_and_fails_the_run()
    {
        var window = new FakeWindow { IgnoreTick = true };

        var result = NvCleanstallPages.Run(window, NvidiaDriverConfig.Default, Package, window.Wait);

        Assert.Equal(NvRunStatus.Failed, result.Status);
        Assert.Contains("HD Audio via HDMI", result.Detail, StringComparison.Ordinal);
        Assert.Single(window.Pressed); // Next on the driver page only; never Next on the components page
    }

    [Fact]
    public void A_disabled_next_button_a_closed_window_or_a_page_that_does_not_move_fail_instead_of_pressing_on()
    {
        Assert.Equal(NvRunStatus.Failed, NvCleanstallPages.Run(new FakeWindow { NextDisabled = true }, NvidiaDriverConfig.Default, Package, _ => true).Status);
        Assert.Equal(NvRunStatus.Failed, NvCleanstallPages.Run(new FakeWindow { Open = false }, NvidiaDriverConfig.Default, Package, _ => true).Status);

        var stuck = new FakeWindow { IgnoreNext = true };
        var result = NvCleanstallPages.Run(stuck, NvidiaDriverConfig.Default, Package, stuck.Wait);
        Assert.Equal(NvRunStatus.Failed, result.Status);
        Assert.Contains("stayed on the driver page", result.Detail, StringComparison.Ordinal);
        Assert.Single(stuck.Pressed); // it pressed Next once and did not press it again
    }

    [Fact]
    public void An_interrupted_wait_stops_the_run_while_the_package_is_being_prepared()
    {
        var window = new FakeWindow { TweaksPageExists = false };
        var calls = 0;

        var result = NvCleanstallPages.Run(window, NvidiaDriverConfig.Default, Package, _ => ++calls < 8);

        Assert.Equal(NvRunStatus.Failed, result.Status);
        Assert.Equal(NvCleanstallIds.PreparingPage, result.StoppedAt);
    }

    [Theory]
    [InlineData("Microsoft Visual C 2017 Runtimes", "Microsoft Visual C++ 2017 Runtimes")]
    [InlineData("HD Audio via HDMI", "hd audio via hdmi")]
    [InlineData("NV Backend", "NV  Backend")]
    public void Labels_are_compared_without_case_spaces_or_punctuation(string a, string b) =>
        Assert.Equal(NvCleanstallIds.Normalize(a), NvCleanstallIds.Normalize(b));

    [Fact]
    public void Every_row_nvcleanstall_shows_for_this_driver_is_a_component_mesura_knows_and_quadro_view_is_the_one_it_does_not_show_here()
    {
        var known = NvidiaComponents.All.ToDictionary(c => NvCleanstallIds.Normalize(c.Label), c => c.Id);
        var shown = Rows.Where(r => r != "NV App Components").Select(NvCleanstallIds.Normalize).ToList();

        Assert.All(shown, s => Assert.Contains(s, known.Keys));
        // NVCleanstall 1.19.0 offers 16 rows for the 621.01 GeForce package; "Quadro View" (which Mesura's list has) is not among them, so a configuration that names it is simply not ticked there.
        Assert.Equal(["quadro-view"], known.Where(k => !shown.Contains(k.Key)).Select(k => k.Value));
    }
}
