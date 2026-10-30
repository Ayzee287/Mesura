using System.Globalization;
using Mesura.Core.SystemInfo;

namespace Mesura.Core.Firmware;

/// <summary>Who made the board (or the whole machine): the BIOS menus are named and arranged by them.</summary>
public enum FirmwareVendor
{
    Unknown,
    Gigabyte,
    Asus,
    Msi,
    AsRock,
    Dell,
    Hp,
    Lenovo,
    Acer,
    Other,
}

public static class FirmwareVendors
{
    /// <summary>The vendor from the board's manufacturer, then the machine's, then the BIOS's own vendor string.</summary>
    public static FirmwareVendor From(string? boardManufacturer, string? machineManufacturer, string? biosVendor)
    {
        foreach (var text in new[] { boardManufacturer, machineManufacturer, biosVendor })
        {
            if (Classify(text) is { } vendor)
            {
                return vendor;
            }
        }

        return string.IsNullOrWhiteSpace(boardManufacturer) && string.IsNullOrWhiteSpace(machineManufacturer) ? FirmwareVendor.Unknown : FirmwareVendor.Other;
    }

    private static FirmwareVendor? Classify(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var t = text.ToLowerInvariant();
        return t switch
        {
            _ when t.Contains("gigabyte", StringComparison.Ordinal) || t.Contains("aorus", StringComparison.Ordinal) => FirmwareVendor.Gigabyte,
            _ when t.Contains("asustek", StringComparison.Ordinal) || t == "asus" || t.StartsWith("asus ", StringComparison.Ordinal) => FirmwareVendor.Asus,
            _ when t.Contains("micro-star", StringComparison.Ordinal) || t == "msi" || t.StartsWith("msi ", StringComparison.Ordinal) => FirmwareVendor.Msi,
            _ when t.Contains("asrock", StringComparison.Ordinal) => FirmwareVendor.AsRock,
            _ when t.Contains("dell", StringComparison.Ordinal) => FirmwareVendor.Dell,
            _ when t.Contains("hewlett", StringComparison.Ordinal) || t == "hp" || t.StartsWith("hp ", StringComparison.Ordinal) => FirmwareVendor.Hp,
            _ when t.Contains("lenovo", StringComparison.Ordinal) => FirmwareVendor.Lenovo,
            _ when t.Contains("acer", StringComparison.Ordinal) => FirmwareVendor.Acer,
            _ => null,
        };
    }

    /// <summary>The key the knowledge file uses for the vendor's own menu steps (<c>common</c> when it has none).</summary>
    public static string Key(FirmwareVendor vendor) => vendor switch
    {
        FirmwareVendor.Gigabyte => "gigabyte",
        FirmwareVendor.Asus => "asus",
        FirmwareVendor.Msi => "msi",
        FirmwareVendor.AsRock => "asrock",
        _ => "common",
    };

    /// <summary>A retail motherboard maker, whose boards' BIOS menus follow the layouts the knowledge file describes.</summary>
    public static bool IsBoardMaker(FirmwareVendor vendor) => vendor is FirmwareVendor.Gigabyte or FirmwareVendor.Asus or FirmwareVendor.Msi or FirmwareVendor.AsRock;
}

/// <summary>Extra readings the platform layer adds to the diagnostic report: what only a few Windows calls know.</summary>
public sealed record FirmwareExtras
{
    /// <summary>A Trusted Platform Module is present and enabled (from the device list, which needs no administrator rights).</summary>
    public bool? TpmPresent { get; init; }

    /// <summary>The TPM's specification version when Windows says (<c>2.0</c>).</summary>
    public string? TpmVersion { get; init; }

    /// <summary>Virtualization extensions are enabled in the firmware (<c>VirtualizationFirmwareEnabled</c>).</summary>
    public bool? Virtualization { get; init; }

    /// <summary>Windows is running on a hypervisor (Hyper-V, memory integrity, WSL 2): which means virtualization is on.</summary>
    public bool? HypervisorRunning { get; init; }

    /// <summary><c>GenuineIntel</c> or <c>AuthenticAMD</c>.</summary>
    public string? CpuVendor { get; init; }

    /// <summary>The microcode revision Windows reports for the first core (<c>Update Revision</c>), when it can be read.</summary>
    public uint? Microcode { get; init; }

    /// <summary>The largest memory window assigned to each display adapter's device, by adapter name: what Resizable BAR changes.</summary>
    public IReadOnlyList<GpuWindow> GpuWindows { get; init; } = [];
}

public sealed record GpuWindow(string? Name, ulong LargestBarBytes);

public sealed record FirmwareGpu(string? Name, string? Vendor, bool? Integrated, ulong? VideoMemoryBytes, ulong? LargestBarBytes)
{
    /// <summary>A card with its own memory: Resizable BAR is about these (an integrated GPU shares system memory).</summary>
    public bool IsDiscrete => Integrated == false && VideoMemoryBytes is >= 1UL << 30;
}

/// <summary>Everything the analysis looks at, in one place: the diagnostic report and the extra readings, with no operating system behind it.</summary>
public sealed record FirmwareFacts(
    DateTimeOffset ReadAt,
    int WindowsBuild,
    string? MachineKind,
    string? MachineManufacturer,
    string? MachineModel,
    string? BoardManufacturer,
    string? Board,
    string? BiosVendor,
    string? BiosVersion,
    string? BiosDate,
    string? BootMode,
    bool? SecureBoot,
    FirmwareExtras Extras,
    string? CpuName,
    IReadOnlyList<SystemMemoryModule> Memory,
    int? MemorySlots,
    IReadOnlyList<FirmwareGpu> Gpus)
{
    public FirmwareVendor Vendor => FirmwareVendors.From(BoardManufacturer, MachineManufacturer, BiosVendor);

    /// <summary>A laptop, notebook or tablet: BIOS options for memory, boost and PCIe are the maker's choice, not the owner's.</summary>
    public bool IsPortable => MachineKind is { Length: > 0 } kind
        && (kind.Contains("laptop", StringComparison.OrdinalIgnoreCase) || kind.Contains("notebook", StringComparison.OrdinalIgnoreCase)
            || kind.Contains("tablet", StringComparison.OrdinalIgnoreCase) || kind.Contains("portable", StringComparison.OrdinalIgnoreCase)
            || kind.Contains("convertible", StringComparison.OrdinalIgnoreCase) || kind.Contains("detachable", StringComparison.OrdinalIgnoreCase));

    public string? CpuVendor => Extras.CpuVendor is { Length: > 0 } v ? v
        : CpuName is { } n && n.Contains("AMD", StringComparison.OrdinalIgnoreCase) ? "AuthenticAMD"
        : CpuName is { } n2 && n2.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? "GenuineIntel" : null;

    public bool IsAmd => string.Equals(CpuVendor, "AuthenticAMD", StringComparison.OrdinalIgnoreCase);

    public bool IsIntel => string.Equals(CpuVendor, "GenuineIntel", StringComparison.OrdinalIgnoreCase);

    /// <summary>Builds the facts from the diagnostic report and the platform's extras.</summary>
    public static FirmwareFacts From(SystemReport report, FirmwareExtras extras)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(extras);
        var windows = extras.GpuWindows;
        var gpus = report.Gpus.Select(gpu =>
        {
            var window = windows.FirstOrDefault(w => SameAdapter(w.Name, gpu.Name));
            return new FirmwareGpu(gpu.Name, gpu.Vendor, gpu.Integrated, gpu.MemoryBytes, window?.LargestBarBytes);
        }).ToList();
        return new FirmwareFacts(
            report.GeneratedAt, report.Os.Build ?? 0, report.Machine.Kind, report.Machine.Manufacturer, report.Machine.Model, report.Machine.BoardManufacturer, report.Machine.Board,
            report.Firmware.Vendor, report.Firmware.Version, report.Firmware.ReleaseDate, report.Firmware.BootMode, report.Firmware.SecureBoot, extras,
            report.Processors.FirstOrDefault()?.Name, report.Memory.Modules, report.Memory.SlotCount, gpus);
    }

    private static bool SameAdapter(string? a, string? b)
    {
        static string Norm(string? s) => new string((s ?? "").Replace("(R)", "", StringComparison.OrdinalIgnoreCase).Replace("(TM)", "", StringComparison.OrdinalIgnoreCase)
            .Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        var (x, y) = (Norm(a), Norm(b));
        return x.Length > 0 && x == y;
    }

    /// <summary>The BIOS release date, when the firmware's text can be read as one (it comes as <c>MM/DD/YYYY</c> or <c>YYYYMMDD</c>).</summary>
    public DateTime? BiosReleased
    {
        get
        {
            if (string.IsNullOrWhiteSpace(BiosDate))
            {
                return null;
            }

            string[] formats = ["MM/dd/yyyy", "M/d/yyyy", "yyyyMMdd", "yyyy-MM-dd", "dd.MM.yyyy"];
            return DateTime.TryParseExact(BiosDate.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
        }
    }
}

/// <summary>How a setting stands on this PC. "Review" is the only status that asks for something; the others say it is fine, optional, unknowable from Windows or irrelevant here.</summary>
public enum FirmwareStatus
{
    /// <summary>Looks right: nothing to change.</summary>
    InPlace,

    /// <summary>Worth changing in the firmware, for the reason the finding gives.</summary>
    Review,

    /// <summary>Off, and only needed for a particular use (virtual machines, a game's anti-cheat); the finding says which.</summary>
    Optional,

    /// <summary>Windows can't tell; the finding says how to look.</summary>
    Unknown,

    /// <summary>Doesn't apply to this PC (a laptop's soldered memory, no discrete graphics card).</summary>
    NotApplicable,
}

/// <summary>Whether the reading was taken straight from Windows (<see cref="Confirmed"/>) or inferred from it (<see cref="Likely"/>, with the reasoning in the finding).</summary>
public enum FirmwareCertainty
{
    Confirmed,
    Likely,
}

/// <summary>What changing the setting can cost. Never a promise of speed.</summary>
public enum FirmwareRisk
{
    /// <summary>A switch with a documented default; undone by switching it back, or clearing the CMOS in the worst case.</summary>
    Safe,

    /// <summary>Can leave the PC unable to start Windows until it is undone; the finding says what to check first.</summary>
    Careful,

    /// <summary>Changes electrical or timing behaviour: can make the PC unstable. Test after every change; undo at the first crash.</summary>
    Experimental,
}

/// <summary>One thing the page says it saw: a key the app localizes and its arguments (a speed, a size, a name). No sentence is built in Core.</summary>
public sealed record FirmwareObservation(string Key, IReadOnlyList<string> Args)
{
    public static FirmwareObservation Of(string key, params string[] args) => new(key, args);
}

/// <summary>A setting's finding: its id (the knowledge file's), its status, how it was read, what it could cost and what was seen.</summary>
public sealed record FirmwareFinding(string Id, FirmwareStatus Status, FirmwareCertainty Certainty, FirmwareRisk Risk, IReadOnlyList<FirmwareObservation> Seen);

public sealed record FirmwareReport(FirmwareFacts Facts, IReadOnlyList<FirmwareFinding> Findings)
{
    public FirmwareVendor Vendor => Facts.Vendor;

    public int ReviewCount => Findings.Count(f => f.Status == FirmwareStatus.Review);

    public FirmwareFinding? Find(string id) => Findings.FirstOrDefault(f => f.Id == id);
}
