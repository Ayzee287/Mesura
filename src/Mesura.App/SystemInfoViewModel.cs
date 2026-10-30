using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Mesura.Core.SystemInfo;
using Mesura.Platform.Windows;
using Mesura.Platform.Windows.Windows;
using Microsoft.Win32;

namespace Mesura.App;

/// <summary>A label and its value on a System card; <see cref="Missing"/> values are shown as "unavailable", dimmed.</summary>
public sealed record InfoRow(string Label, string Value, bool Missing);

/// <summary>A measured share (memory in use, a volume's used space): a label, a caption and a fill from 0 to 1.</summary>
public sealed record InfoBar(string Label, string Caption, double Fraction, string Tone);

/// <summary>A heading with its rows and bars: one GPU, one drive, one display; a card with a single subject has one group without a heading.</summary>
public sealed record InfoGroup(string Heading, string Badge, IReadOnlyList<InfoRow> Rows, IReadOnlyList<InfoBar> Bars)
{
    public bool HasHeading => Heading.Length > 0;

    public bool HasBadge => Badge.Length > 0;

    public bool HasRows => Rows.Count > 0;

    public bool HasBars => Bars.Count > 0;
}

/// <summary>
/// A card of the System page: a glyph, a title and its groups. A card about one thing (the processor, the memory, the storage) leads with
/// its headline (the name or the total, as large as the card allows, wrapping instead of cutting a long hardware name) and one line under it.
/// </summary>
public sealed record InfoCard(string Key, string Glyph, string Title, IReadOnlyList<InfoGroup> Groups, string Headline = "", string Sub = "")
{
    public bool HasHeadline => Headline.Length > 0;

    public bool HasSub => Sub.Length > 0;
}

/// <summary>
/// The System page (0.3): an overview of this PC and its Windows configuration as cards (computer, Windows, processor, memory,
/// graphics, storage, displays, power). It reads, never changes: the diagnostic scan the product already has plus a few
/// registry values (SystemInfoReader), on a background thread, again on Refresh. A value that can't be read says
/// "unavailable"; nothing is guessed. The report can be copied or saved as text.
/// </summary>
public sealed class SystemInfoViewModel : Observable, IPageState
{
    private readonly Services _services;
    private SystemReport? _report;
    private bool _loading;
    private bool _loaded;
    private string _status = "";
    private string _failure = "";

    public SystemInfoViewModel(Services services)
    {
        _services = services;
        Refresh = new AsyncCommand(LoadAsync, () => !_loading, Fail);
        Copy = new RelayCommand(CopyReport, () => _report is not null);
        Save = new RelayCommand(SaveReport, () => _report is not null);
        Loc.Instance.LanguageChanged += Build;
    }

    public AsyncCommand Refresh { get; }

    public RelayCommand Copy { get; }

    public RelayCommand Save { get; }

    public Banner Result { get; } = new();

    public IReadOnlyList<InfoCard> Cards { get; private set; } = [];

    /// <summary>The cards split over the page's columns (one, two or three by the width the page has; see <see cref="SetColumns"/>).</summary>
    public IReadOnlyList<IReadOnlyList<InfoCard>> Columns { get; private set; } = [];

    public int ColumnCount { get; private set; } = 3;

    /// <summary>Re-deals the cards when the page gains or loses room for a column: each card goes under the shorter column, so no column ends in a gap.</summary>
    public void SetColumns(int count)
    {
        count = Math.Clamp(count, 1, 3);
        if (count == ColumnCount)
        {
            return;
        }

        ColumnCount = count;
        Raise(nameof(ColumnCount));
        Deal();
    }

    private void Deal()
    {
        var columns = Enumerable.Range(0, ColumnCount).Select(_ => new List<InfoCard>()).ToList();
        var heights = new double[ColumnCount];
        foreach (var card in Cards)
        {
            var height = 3 + card.Groups.Sum(g => (g.HasHeading ? 1.4 : 0) + g.Rows.Count + g.Bars.Count * 1.5) + (card.HasHeadline ? 1.5 : 0);
            var shortest = Array.IndexOf(heights, heights.Min());
            columns[shortest].Add(card);
            heights[shortest] += height;
        }

        Columns = columns.Where(c => c.Count > 0).Select(c => (IReadOnlyList<InfoCard>)c).ToList();
        Raise(nameof(Columns));
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

    /// <summary>One line under the title: the model and Windows, once known.</summary>
    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    /// <summary>Set when the whole read failed; the cards that did load stay.</summary>
    public string Failure
    {
        get => _failure;
        private set => Set(ref _failure, value);
    }

    public bool ShowEmpty => !_loading && Cards.Count == 0;

    /// <summary>The first visit reads the PC; coming back keeps what is shown (Refresh reads again).</summary>
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
        Failure = "";
        Result.Hide();
        try
        {
            var version = Branding.ShortVersion;
            var report = await Task.Run(() => SystemInfoReader.Read(version));
            _report = report;
            _loaded = true;
            Build();
            _services.Log($"System page read: {report.Processors.Count} processors, {report.Gpus.Count} graphics adapters, {report.Disks.Count} drives, {report.Displays.Count} displays.");
        }
        finally
        {
            Loading = false;
            Copy.RaiseCanExecuteChanged();
            Save.RaiseCanExecuteChanged();
        }
    }

    private void Fail(Exception error)
    {
        // The reason goes to the session log; the page says what it means.
        _services.Log("The System page couldn't read this PC: " + error.Message);
        Failure = Loc.Get("System.Failed");
        Result.Show(Tone.Warn, Loc.Get("System.Failed"), Loc.Get("System.FailedHint"), technical: error.Message);
        Loading = false;
    }

    private void CopyReport()
    {
        if (_report is null)
        {
            return;
        }

        try
        {
            Clipboard.SetText(SystemReportText.Render(_report, Branding.ShortVersion));
            Result.Show(Tone.Ok, Loc.Get("System.Copied"));
        }
        catch (System.Runtime.InteropServices.ExternalException e)
        {
            // Another program holds the clipboard for a moment.
            Result.Show(Tone.Warn, Loc.Get("System.CopyFailed"), technical: e.Message);
        }
    }

    private void SaveReport()
    {
        if (_report is null)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = Loc.Get("System.SaveTitle"),
            Filter = Loc.Get("System.SaveFilter"),
            FileName = "mesura-system-report-" + _report.GeneratedAt.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture) + ".md",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            LocalStore.WriteUserExport(dialog.FileName, new UTF8Encoding(false).GetBytes(SystemReportText.Render(_report, Branding.ShortVersion)));
            _services.Log("System report saved: " + dialog.FileName);
            Result.Show(Tone.Ok, Loc.Format("System.Saved", Path.GetFileName(dialog.FileName)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Result.Show(Tone.Danger, Loc.Get("System.SaveFailed"), Loc.Get("System.SaveFailedHint"), technical: e.Message);
        }
    }

    // ---- building the cards from the report -------------------------------------------------------------------------

    private static string Missing => Loc.Get("System.Unavailable");

    private static InfoRow Row(string labelKey, string? value) => new(Loc.Get(labelKey), string.IsNullOrWhiteSpace(value) ? Missing : value, string.IsNullOrWhiteSpace(value));

    private static string? Join(string separator, params string?[] parts)
    {
        var present = parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        return present.Count == 0 ? null : string.Join(separator, present);
    }

    private static string Bytes(ulong? bytes, bool decimalUnits = false) =>
        bytes is null ? "" : SystemReportText.Bytes(bytes, decimalUnits, Loc.Get("System.UnitMB"), Loc.Get("System.UnitGB"), Loc.Get("System.UnitTB"));

    /// <summary>A word the report builder produces in English ("Laptop", "Soldered"), in the language of the window; unknown words stay as they are.</summary>
    private const string WordPrefix = "System.Word.";

    private static string? Tr(string? word)
    {
        if (string.IsNullOrEmpty(word))
        {
            return word;
        }

        var translated = Loc.Get(WordPrefix + word);
        return translated == WordPrefix + word ? word : translated;
    }

    private void Build()
    {
        if (_report is not { } r)
        {
            return;
        }

        var cards = new List<InfoCard>();
        var machine = r.Machine;
        Status = Join(" · ", Join(" ", machine.Manufacturer, machine.Model), Join(" ", r.Os.Name, r.Os.Version)) ?? "";

        cards.Add(new InfoCard("computer", Glyph("IconSystem"), Loc.Get("System.Computer"),
        [
            new InfoGroup("", "", [
                Row("System.Type", Tr(machine.Kind)),
                Row("System.Board", Join(" ", machine.BoardManufacturer, machine.Board)),
                Row("System.Bios", Join(" ", r.Firmware.Vendor, r.Firmware.Version)),
                Row("System.BiosDate", r.Firmware.ReleaseDate),
                Row("System.BootMode", Join(" · ", Tr(r.Firmware.BootMode), r.Firmware.SecureBoot is { } sb ? Loc.Get(sb ? "System.SecureBootOn" : "System.SecureBootOff") : null)),
            ], []),
        ]));

        var os = r.Os;
        cards.Add(new InfoCard("windows", Glyph("IconWindows"), Loc.Get("System.Windows"),
        [
            new InfoGroup("", "", [
                Row("System.Edition", os.Edition),
                Row("System.Build", os.Build is { } b ? b.ToString(CultureInfo.InvariantCulture) + (os.Revision is { } rev ? "." + rev.ToString(CultureInfo.InvariantCulture) : "") : null),
                Row("System.Architecture", os.Architecture),
                Row("System.InstalledOn", os.InstalledOn?.ToString("d", CultureInfo.CurrentCulture)),
                Row("System.Uptime", os.Uptime is { } up ? Uptime(up) : null),
            ], []),
        ]));

        var cpuGroups = r.Processors.Select(cpu => new InfoGroup(r.Processors.Count > 1 ? Plain(cpu.Name) ?? "" : "", "", [
            Row("System.Processor", Plain(cpu.Name)),
            Row("System.CoresThreads", cpu.Cores is { } c ? Loc.Format("System.CoresThreadsValue", c, cpu.Threads?.ToString(CultureInfo.InvariantCulture) ?? "?") : null),
            Row("System.MaxClock", cpu.MaxMhz is { } mhz ? SystemReportText.Ghz(mhz) : null),
        ], [])).ToList();
        if (cpuGroups.Count == 0)
        {
            cpuGroups.Add(new InfoGroup("", "", [Row("System.Processor", null)], []));
        }

        if (r.Processors.Count == 1 && Plain(r.Processors[0].Name) is { } cpuName)
        {
            // One processor: its name leads the card instead of repeating as the first row.
            cards.Add(new InfoCard("processor", Glyph("IconBoard"), Loc.Get("System.ProcessorCard"), [new InfoGroup("", "", cpuGroups[0].Rows.Skip(1).ToList(), [])], cpuName));
        }
        else
        {
            cards.Add(new InfoCard("processor", Glyph("IconBoard"), Loc.Get("System.ProcessorCard"), cpuGroups));
        }

        var memory = r.Memory;
        var memoryRows = new List<InfoRow>();
        var firstModule = memory.Modules.FirstOrDefault();
        var memorySub = Join(" · ", Join(" ", string.IsNullOrEmpty(firstModule?.Type) ? null : firstModule!.Type, firstModule?.SpeedMts is { } fs ? fs.ToString(CultureInfo.InvariantCulture) + " MT/s" : null),
            memory.Modules.Count > 0 ? Loc.Plural("System.ModuleCount", memory.Modules.Count) : null) ?? "";
        var memoryBars = new List<InfoBar>();
        if (memory.UsedBytes is { } used && memory.TotalBytes is { } total && total > 0)
        {
            var fraction = Math.Min(1, (double)used / total);
            memoryBars.Add(new InfoBar(Loc.Get("System.InUse"), Loc.Format("System.UsedOf", Bytes(used), Bytes(total)), fraction, fraction > 0.9 ? Tone.Danger : fraction > 0.75 ? Tone.Warn : Tone.Accent));
        }

        var memoryGroups = new List<InfoGroup> { new("", "", memoryRows, memoryBars) };
        foreach (var module in memory.Modules)
        {
            memoryGroups.Add(new InfoGroup(
                Join(" · ", module.Slot, Bytes(module.SizeBytes)) ?? "",
                "",
                [Row("System.ModuleKind", Join(" ", module.SpeedMts is { } s ? s.ToString(CultureInfo.InvariantCulture) + " MT/s" : null, string.IsNullOrEmpty(module.FormFactor) ? null : Tr(module.FormFactor))),
                 Row("System.Maker", module.Manufacturer)],
                []));
        }

        cards.Add(new InfoCard("memory", Glyph("IconMemory"), Loc.Get("System.Memory"), memoryGroups, memory.TotalBytes is null ? Missing : Bytes(memory.TotalBytes), memorySub));

        var gpuGroups = r.Gpus.Select(gpu => new InfoGroup(Plain(gpu.Name) ?? Missing,
            gpu.Integrated switch { true => Loc.Get("System.Integrated"), false => Loc.Get("System.Discrete"), _ => "" },
            [Row("System.Driver", gpu.DriverVersion), Row("System.VideoMemory", gpu.MemoryBytes is { } vram ? Bytes(vram) : null)], [])).ToList();
        if (gpuGroups.Count == 0)
        {
            gpuGroups.Add(new InfoGroup("", "", [Row("System.Graphics", null)], []));
        }

        if (r.Gpus.Count == 1 && Plain(r.Gpus[0].Name) is { } gpuName)
        {
            cards.Add(new InfoCard("graphics", Glyph("IconImage"), Loc.Get("System.GraphicsCard"), [new InfoGroup("", "", gpuGroups[0].Rows, [])], gpuName,
                r.Gpus[0].Integrated switch { true => Loc.Get("System.Integrated"), false => Loc.Get("System.Discrete"), _ => "" }));
        }
        else
        {
            cards.Add(new InfoCard("graphics", Glyph("IconImage"), Loc.Get("System.GraphicsCard"), gpuGroups));
        }

        var storageGroups = new List<InfoGroup>();
        foreach (var disk in r.Disks)
        {
            storageGroups.Add(new InfoGroup(disk.Model ?? Missing, Join(" ", disk.Media, disk.Bus) ?? "",
                [Row("System.Capacity", disk.SizeBytes is { } size ? Bytes(size, decimalUnits: true) : null), Row("System.Health", disk.Health is { } h ? HealthText(h) : null)], []));
        }

        var volumeBars = r.Volumes.Where(v => v.SizeBytes is > 0 && v.UsedBytes is not null).Select(v =>
        {
            var fraction = Math.Min(1, (double)v.UsedBytes!.Value / v.SizeBytes!.Value);
            return new InfoBar(Join(" ", v.Letter + ":", v.FileSystem) ?? "", Loc.Format("System.FreeOf", Bytes(v.FreeBytes), Bytes(v.SizeBytes)), fraction, fraction > 0.92 ? Tone.Danger : fraction > 0.8 ? Tone.Warn : Tone.Accent);
        }).ToList();
        if (volumeBars.Count > 0)
        {
            storageGroups.Add(new InfoGroup(Loc.Get("System.Volumes"), "", [], volumeBars));
        }

        if (storageGroups.Count == 0)
        {
            storageGroups.Add(new InfoGroup("", "", [Row("System.Drives", null)], []));
        }

        var totalDisk = r.Disks.Where(d => d.SizeBytes is not null).Sum(d => (long)d.SizeBytes!.Value);
        cards.Add(new InfoCard("storage", Glyph("IconStorage"), Loc.Get("System.Storage"), storageGroups, totalDisk > 0 ? Bytes((ulong)totalDisk, decimalUnits: true) : "",
            r.Disks.Count > 0 ? Loc.Format("System.DriveCount", r.Disks.Count) : ""));

        var displayGroups = r.Displays.Select(display => new InfoGroup(display.Name ?? Loc.Get("System.Display"), Tr(display.Connection) ?? "",
        [
            Row("System.Resolution", display.Width is { } w && display.Height is { } h ? $"{w} × {h}" : null),
            Row("System.RefreshRate", display.RefreshHz is { } hz ? Hz(hz) + (display.MaxRefreshHz is { } max && max > hz + 0.5 ? " · " + Loc.Format("System.UpTo", Hz(max)) : "") : null),
        ], [])).ToList();
        if (displayGroups.Count == 0)
        {
            displayGroups.Add(new InfoGroup("", "", [Row("System.Display", null)], []));
        }

        cards.Add(new InfoCard("displays", Glyph("IconMonitor"), Loc.Get("System.Displays"), displayGroups));

        var power = r.Power;
        cards.Add(new InfoCard("power", Glyph("IconBattery"), Loc.Get("System.Power"),
        [
            new InfoGroup("", "", [
                Row("System.PowerSource", power.OnAc switch { true => Loc.Get("System.PluggedIn"), false => Loc.Get("System.OnBattery"), _ => null }),
                Row("System.Battery", power.HasBattery switch { true => Loc.Get("System.Present"), false => Loc.Get("System.None"), _ => null }),
                Row("System.PowerPlan", power.PlanName is { } plan ? PlanName(plan) : null),
                Row("System.ModernStandby", power.ModernStandby switch { true => Loc.Get("System.Supported"), false => Loc.Get("System.NotSupported"), _ => null }),
            ], []),
        ]));

        Cards = cards;
        Deal();
        Raise(nameof(Cards));
        Raise(nameof(ShowEmpty));
    }


    /// <summary>"11th Gen Intel(R) Core(TM) i7-1165G7 @ 2.80GHz" as "Intel Core i7-1165G7": the name without trademark marks, generation prefix and clock.</summary>
    internal static string ShortProcessor(string name)
    {
        var text = System.Text.RegularExpressions.Regex.Replace(name, @"\((R|TM)\)|\bCPU\b|\bProcessor\b|@\s*[\d.]+\s*GHz|^\d+(st|nd|rd|th)\s+Gen\s+", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s{2,}", " ").Trim();
        return text.Length == 0 ? name : text;
    }

    /// <summary>A device name without its trademark marks ("Intel(R) Iris(R) Xe Graphics" as "Intel Iris Xe Graphics").</summary>
    internal static string? Plain(string? name) => name is null ? null : System.Text.RegularExpressions.Regex.Replace(name.Replace("(R)", "", StringComparison.OrdinalIgnoreCase).Replace("(TM)", "", StringComparison.OrdinalIgnoreCase), @"\s{2,}", " ").Trim();

    private static string Glyph(string key) => Application.Current?.TryFindResource(key) as string ?? "";

    private static string Hz(double hz) => hz.ToString("0.##", CultureInfo.CurrentCulture) + " " + Loc.Get("System.Hz");

    private static string Uptime(TimeSpan span) =>
        span.TotalDays >= 1 ? Loc.Format("System.UptimeDays", (int)span.TotalDays, span.Hours)
        : span.TotalHours >= 1 ? Loc.Format("System.UptimeHours", (int)span.TotalHours, span.Minutes)
        : Loc.Format("System.UptimeMinutes", Math.Max(1, span.Minutes));

    private static string HealthText(string health) => health switch
    {
        "Healthy" => Loc.Get("System.Healthy"),
        "Warning" => Loc.Get("System.HealthWarning"),
        "Unhealthy" => Loc.Get("System.Unhealthy"),
        _ => health,
    };

    private static string PlanName(string plan) => plan switch
    {
        "Balanced" => Loc.Get("System.PlanBalanced"),
        "High performance" => Loc.Get("System.PlanHigh"),
        "Power saver" => Loc.Get("System.PlanSaver"),
        "Ultimate Performance" => Loc.Get("System.PlanUltimate"),
        "Custom" => Loc.Get("System.PlanCustom"),
        _ => plan,
    };
}
