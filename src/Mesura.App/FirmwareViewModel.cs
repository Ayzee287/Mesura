using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using Mesura.Core.Firmware;
using Mesura.Core.SystemInfo;
using Mesura.Platform.Windows.Windows;

namespace Mesura.App;

/// <summary>A link to the maker's or Microsoft's own page about a setting; opened in the browser on a click.</summary>
public sealed record FirmwareLink(string Title, string Url, ICommand Open)
{
    public override string ToString() => Title;
}

/// <summary>One fact in the strip under the title: what it is and its value (the board, the BIOS, the boot mode).</summary>
public sealed record FirmwareFact(string Label, string Value)
{
    public override string ToString() => Label + ": " + Value;
}

/// <summary>A setting's card: how it stands on this PC, how that was read, what changing it could cost and, on demand, how.</summary>
public sealed class FirmwareCard : Observable
{
    private bool _expanded;

    public FirmwareCard(FirmwareFinding finding, FirmwareSetting setting, FirmwareVendor vendor, ICommand openLink)
    {
        var language = Loc.Instance.Language;
        Id = finding.Id;
        Title = setting.Title.In(language);
        Status = finding.Status;
        StatusText = Loc.Get($"Firmware.Status.{finding.Status}");
        StatusTone = finding.Status switch
        {
            FirmwareStatus.InPlace => Tone.Ok,
            FirmwareStatus.Review => Tone.Warn,
            _ => Tone.Muted,
        };
        Seen = finding.Seen.Select(o => Loc.Format($"Firmware.Obs.{o.Key}", [.. o.Args])).ToList();
        CertaintyText = Loc.Get($"Firmware.Certainty.{finding.Certainty}");
        RiskText = Loc.Get($"Firmware.Risk.{finding.Risk}");
        RiskTone = finding.Risk switch { FirmwareRisk.Safe => Tone.Ok, FirmwareRisk.Careful => Tone.Warn, _ => Tone.Danger };
        What = setting.What.In(language);
        Why = setting.Why.In(language);
        Cost = setting.Risk.In(language);
        Verify = setting.Verify.In(language);
        var own = setting.HasOwnSteps(vendor);
        StepsHeading = own ? Loc.Format("Firmware.StepsFor", MakerName(vendor)) : Loc.Get("Firmware.StepsCommon");
        Steps = setting.StepsFor(vendor).Select((s, i) => $"{i + 1}. {s.In(language)}").ToList();
        Links = setting.Links.Select(l => new FirmwareLink(l.Title.In(language), l.Url, openLink)).ToList();
        HasSteps = finding.Status != FirmwareStatus.NotApplicable && Steps.Count > 0;
        _expanded = finding.Status == FirmwareStatus.Review;
    }

    public string Id { get; }

    public string Title { get; }

    public FirmwareStatus Status { get; }

    public string StatusText { get; }

    public string StatusTone { get; }

    public IReadOnlyList<string> Seen { get; }

    public string CertaintyText { get; }

    public string RiskText { get; }

    public string RiskTone { get; }

    public string What { get; }

    public string Why { get; }

    public string Cost { get; }

    public string Verify { get; }

    public string StepsHeading { get; }

    public IReadOnlyList<string> Steps { get; }

    public IReadOnlyList<FirmwareLink> Links { get; }

    public bool HasSteps { get; }

    public bool HasLinks => Links.Count > 0;

    /// <summary>Whether the card shows its explanation and steps; a setting worth a look opens by itself.</summary>
    public bool Expanded
    {
        get => _expanded;
        set => Set(ref _expanded, value);
    }

    /// <summary>What assistive technology reads for this card: its name and where it stands, never the type name.</summary>
    public override string ToString() => Title + ", " + StatusText;

    internal static string MakerName(FirmwareVendor vendor) => vendor switch
    {
        FirmwareVendor.Gigabyte => "Gigabyte",
        FirmwareVendor.Asus => "ASUS",
        FirmwareVendor.Msi => "MSI",
        FirmwareVendor.AsRock => "ASRock",
        _ => "",
    };
}

/// <summary>
/// The BIOS &amp; Firmware page (0.4, docs/firmware.md): what is worth setting in this PC's BIOS or UEFI, from what Windows can read, with the
/// steps for the board's maker, and after a restart what changed. It reads and explains; the one thing it does is restart into the firmware
/// setup when asked (Windows' own <c>shutdown /r /fw</c>), and it never writes firmware, UEFI variables or the BIOS itself.
/// </summary>
public sealed class FirmwareViewModel : Observable, IPageState
{
    private readonly Services _services;
    private FirmwareReport? _report;
    private FirmwareHistory? _history;
    private bool _loading;
    private bool _loaded;
    private bool _restartScheduled;
    private string _status = "";

    public FirmwareViewModel(Services services)
    {
        _services = services;
        Refresh = new AsyncCommand(LoadAsync, () => !_loading, Fail);
        Restart = new RelayCommand(RestartToFirmware, () => !_restartScheduled);
        CancelRestart = new RelayCommand(CancelScheduledRestart, () => _restartScheduled);
        OpenLink = new RelayCommand<string>(url => { if (url is { Length: > 0 } && ShellLauncher.Open(url) is { } error) { Result.Show(Tone.Warn, Loc.Get("Firmware.CantOpenLink"), technical: error); } });
        Loc.Instance.LanguageChanged += Build;
    }

    public AsyncCommand Refresh { get; }

    public RelayCommand Restart { get; }

    public RelayCommand CancelRestart { get; }

    public RelayCommand<string> OpenLink { get; }

    public Banner Result { get; } = new();

    public ObservableCollection<FirmwareCard> Cards { get; } = [];

    public ObservableCollection<FirmwareFact> Facts { get; } = [];

    /// <summary>What changed since the last different reading ("Memory speed: 4800 → 6000 MT/s"); empty when nothing did.</summary>
    public ObservableCollection<string> Changes { get; } = [];

    public bool HasChanges => Changes.Count > 0;

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public bool Loading
    {
        get => _loading;
        private set
        {
            Set(ref _loading, value);
            Raise(nameof(ShowEmpty));
            Refresh.RaiseCanExecuteChanged();
        }
    }

    public bool ShowEmpty => !_loading && Cards.Count == 0;

    public bool RestartScheduled => _restartScheduled;

    /// <summary>The page's own caveat about menu names (the knowledge file's), in the window's language.</summary>
    public string MenuNote => FirmwareKnowledge.Default.Note.In(Loc.Instance.Language);

    /// <summary>The line in the command dock: how many settings are worth a look.</summary>
    public string DockSummary => _report is null ? Loc.Get("Firmware.Reading") : _report.ReviewCount == 0 ? Loc.Get("Firmware.DockNone") : Loc.Plural("Firmware.DockReview", _report.ReviewCount);

    public void Enter()
    {
        if (!_loaded && !_loading)
        {
            Refresh.Execute(null);
        }
    }

    public void Leave() => Result.Hide();

    private async Task LoadAsync()
    {
        Loading = true;
        Result.Hide();
        try
        {
            var version = Branding.ShortVersion;
            var report = await Task.Run(() =>
            {
#if DEBUG
                // A Debug build started with MESURA_PREVIEW_FIRMWARE=1 shows a stand-in desktop (named as one), for screenshots on a PC that has nothing to review.
                if (Environment.GetEnvironmentVariable("MESURA_PREVIEW_FIRMWARE") == "1")
                {
                    return FirmwareAnalyzer.Analyze(PreviewFacts());
                }
#endif
                var system = SystemInfoReader.Read(version);
                return FirmwareAnalyzer.Analyze(FirmwareFacts.From(system, FirmwareReader.Read()));
            });
            _report = report;
            _loaded = true;
            if (!Preview)
            {
                RememberReading(report); // a stand-in PC never goes into the history of the real one
            }

            Build();
            _services.Log($"BIOS & Firmware read: {report.Findings.Count} settings, {report.ReviewCount} worth a look, vendor {report.Vendor}.");
        }
        finally
        {
            Loading = false;
        }
    }

    /// <summary>Keeps the values this reading holds, so the next visit after a restart can say what changed; a store that can't be written is not an error here.</summary>
    private void RememberReading(FirmwareReport report)
    {
        try
        {
            var old = FirmwareHistory.TryParse(_services.Store.ReadFirmwareHistory());
            _history = FirmwareHistory.Advance(old, FirmwareSnapshot.Of(report.Facts));
            if (!Equals(old, _history))
            {
                _services.Store.WriteFirmwareHistory(_history.ToJson());
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _services.Log("The firmware readings couldn't be saved: " + e.Message);
        }
    }

#if DEBUG
    private static FirmwareFacts PreviewFacts()
    {
        SystemMemoryModule Dimm(string slot, string bank) => new(slot, 16UL << 30, "DDR5", 4800, "G.Skill", "DIMM", 4800, 4800, "F5-6000J3038F16GX2-FX5", bank);
        return new FirmwareFacts(DateTimeOffset.Now, 26200, "Desktop", "Gigabyte Technology Co., Ltd.", "Design preview", "Gigabyte Technology Co., Ltd.", "B850 EAGLE ICE (design preview)",
            "American Megatrends International, LLC.", "F3", "08/12/2025", "UEFI", true,
            new FirmwareExtras { TpmPresent = true, TpmVersion = "2.0", Virtualization = false, CpuVendor = "AuthenticAMD", GpuWindows = [] },
            "AMD Ryzen 7 7700 8-Core Processor", [Dimm("DIMM_A2", "P0 CHANNEL A"), Dimm("DIMM_B2", "P0 CHANNEL B")], 4,
            [new FirmwareGpu("NVIDIA GeForce RTX 4070 (design preview)", "NVIDIA", false, 12UL << 30, 256UL << 20)]);
    }
#endif

    private static bool Preview =>
#if DEBUG
        Environment.GetEnvironmentVariable("MESURA_PREVIEW_FIRMWARE") == "1";
#else
        false;
#endif

    private void Fail(Exception error)
    {
        _services.Log("The BIOS & Firmware page couldn't read this PC: " + error.Message);
        Result.Show(Tone.Warn, Loc.Get("Firmware.Failed"), Loc.Get("Firmware.FailedHint"), technical: error.Message);
        Loading = false;
    }

    private void Build()
    {
        if (_report is not { } report)
        {
            return;
        }

        var knowledge = FirmwareKnowledge.Default;
        var cards = report.Findings
            .Select((f, i) => (Finding: f, Index: i, Setting: knowledge.Get(f.Id)))
            .Where(x => x.Setting is not null)
            .OrderBy(x => Rank(x.Finding.Status)).ThenBy(x => x.Index)
            .Select(x => new FirmwareCard(x.Finding, x.Setting!, report.Vendor, OpenLink))
            .ToList();
        Cards.Clear();
        foreach (var card in cards)
        {
            Cards.Add(card);
        }

        var f2 = report.Facts;
        Facts.Clear();
        AddFact("Firmware.Fact.Board", Join(FirmwareCard.MakerName(report.Vendor) is { Length: > 0 } maker ? maker : f2.BoardManufacturer, f2.Board) ?? Join(f2.MachineManufacturer, f2.MachineModel));
        AddFact("Firmware.Fact.Bios", Join(f2.BiosVersion, f2.BiosDate is { Length: > 0 } d ? "(" + d + ")" : null));
        AddFact("Firmware.Fact.Boot", f2.BootMode is { Length: > 0 } boot ? boot + (f2.SecureBoot is { } sb ? " · " + Loc.Get(sb ? "System.SecureBootOn" : "System.SecureBootOff") : "") : null);
        AddFact("Firmware.Fact.Cpu", SystemInfoViewModel.Plain(f2.CpuName));

        Changes.Clear();
        if (_history is { Current: var current } history && (DateTimeOffset.Now - current.At).TotalDays <= 45)
        {
            foreach (var change in history.Changes)
            {
                Changes.Add(Loc.Format($"Firmware.Change.{change.Key}", change.Before, change.After));
            }
        }

        Status = report.ReviewCount == 0 ? Loc.Get("Firmware.StatusNone") : Loc.Plural("Firmware.StatusReview", report.ReviewCount);
        Raise(nameof(HasChanges));
        Raise(nameof(ShowEmpty));
        Raise(nameof(MenuNote));
        Raise(nameof(DockSummary));
    }

    private void AddFact(string labelKey, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            Facts.Add(new FirmwareFact(Loc.Get(labelKey), value));
        }
    }

    private static string? Join(string? a, string? b)
    {
        var parts = new[] { a, b }.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    private static int Rank(FirmwareStatus status) => status switch
    {
        FirmwareStatus.Review => 0,
        FirmwareStatus.Optional => 1,
        FirmwareStatus.Unknown => 2,
        FirmwareStatus.InPlace => 3,
        _ => 4,
    };

    // ---- restart into the firmware setup ----------------------------------------------------------------------------------------

    private void RestartToFirmware()
    {
        if (_services.Changing)
        {
            Dialogs.Busy();
            return;
        }

        var title = Loc.Get("Firmware.RestartTitle");
        if (!string.Equals(_report?.Facts.BootMode, "UEFI", StringComparison.OrdinalIgnoreCase))
        {
            // A PC that started in legacy mode has no firmware to restart into by this route: say what to press instead.
            ConfirmDialog.Tell(title, Loc.Get("Firmware.RestartNotUefi"));
            return;
        }

        if (!_services.Reader.IsElevated)
        {
            // Asking the firmware to open its setup next time needs administrator rights; Windows' own Settings page does it from a normal account.
            if (ConfirmDialog.Ask(title, Loc.Get("Firmware.RestartViaSettings"), confirm: Loc.Get("Firmware.OpenSettings")) && ShellLauncher.Open("ms-settings:recovery") is { } openError)
            {
                Result.Show(Tone.Warn, Loc.Get("Firmware.CantOpenLink"), technical: openError);
            }

            return;
        }

        if (!ConfirmDialog.Ask(title, Loc.Get("Firmware.RestartAsk"), note: Loc.Get("Firmware.RestartNote"), confirm: Loc.Get("Firmware.RestartNow")))
        {
            return;
        }

        if (ShellLauncher.RestartToFirmware() is { } error)
        {
            Result.Show(Tone.Danger, Loc.Get("Firmware.RestartFailed"), Loc.Get("Firmware.RestartFailedHint"), technical: error);
            return;
        }

        _restartScheduled = true;
        Result.Show(Tone.Warn, Loc.Get("Firmware.RestartScheduled"), Loc.Get("Firmware.RestartScheduledHint"));
        _services.Log("Restart into the firmware setup scheduled (shutdown /r /fw, ten seconds).");
        RaiseRestartState();
    }

    private void CancelScheduledRestart()
    {
        if (ShellLauncher.CancelRestart() is { } error)
        {
            Result.Show(Tone.Danger, Loc.Get("Firmware.RestartFailed"), technical: error);
            return;
        }

        _restartScheduled = false;
        Result.Show(Tone.Ok, Loc.Get("Firmware.RestartCancelled"));
        RaiseRestartState();
    }

    private void RaiseRestartState()
    {
        Raise(nameof(RestartScheduled));
        Restart.RaiseCanExecuteChanged();
        CancelRestart.RaiseCanExecuteChanged();
    }
}
