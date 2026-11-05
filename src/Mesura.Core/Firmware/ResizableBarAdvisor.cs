using System.Globalization;
using System.Text.RegularExpressions;

namespace Mesura.Core.Firmware;

/// <summary>How far a piece of hardware is documented to support Resizable BAR / SmartAccess Memory.</summary>
public enum BarSupport
{
    /// <summary>The vendor documents it (or it is a newer generation of a documented one).</summary>
    Supported,

    /// <summary>Depends on the motherboard's BIOS or on an unofficial extension.</summary>
    Likely,

    /// <summary>Older than anything documented.</summary>
    Unlikely,

    /// <summary>The vendor does not support it on this hardware.</summary>
    NotSupported,

    /// <summary>The name was not recognised: Mesura does not guess.</summary>
    Unknown,
}

/// <summary>One link of the chain Resizable BAR needs, with what is known about it and how.</summary>
public sealed record BarLayer(string Name, string State, string Detail);

/// <summary>What the driver itself reports about the feature (AMD: ADLX's SmartAccess Memory switch); <c>null</c> parts are unknown.</summary>
public sealed record DriverBarState(bool? Supported, bool? Enabled);

public enum BarVerdict
{
    /// <summary>The memory window is as large as the card's memory (and, on AMD, the driver's switch is not off).</summary>
    Enabled,

    /// <summary>The window is large but the AMD driver's own switch says off: the one part Mesura can change.</summary>
    DriverSwitchOff,

    /// <summary>The window is small: the BIOS has to enable it (Above 4G Decoding, then Re-Size BAR).</summary>
    NeedsBios,

    /// <summary>Windows boots in legacy (CSM) mode: it must be UEFI before anything else.</summary>
    NeedsUefi,

    /// <summary>The card or the processor is not documented to support it.</summary>
    NotSupported,

    /// <summary>No discrete graphics card.</summary>
    NotApplicable,

    /// <summary>Windows could not say.</summary>
    Unknown,
}

public sealed record BarAssessment(
    string Gpu,
    BarVerdict Verdict,
    IReadOnlyList<BarLayer> Layers,
    string Reason,
    IReadOnlyList<LocalizedText> Instruction,
    bool MesuraCanChangeIt);

/// <summary>
/// Resizable BAR (NVIDIA) / SmartAccess Memory (AMD) judged as the owner asked (G4, 2026-11-04): the layers are kept apart (processor, graphics card, motherboard, Above 4G Decoding, Re-Size BAR in the
/// BIOS, the driver's own state) and each says what is KNOWN and what is only inferred. The BIOS cannot be read from Windows, so those two layers are inferred from where Windows put the card's memory window
/// and how big it is, and are labelled so; a feature that is supported but off is explained with the BIOS steps for this board's maker; the BIOS is never touched; and "enabled" is said only when the window
/// really is as large as the card's memory.
/// <para>
/// Support tables, from what the vendors documented: NVIDIA's launch page (30 March 2021) lists GeForce RTX 30 series, AMD Zen 3 Ryzen 5000 on 400/500 boards and Intel 10th/11th generation; RTX 40 series and
/// newer ship with it (Tom's Hardware, secondhand: NVIDIA's page covers only RTX 30); AMD's launch pairing was Ryzen 5000 with Radeon RX 6000, later Ryzen 3000 on B550/X570. Newer generations of a documented
/// family are treated as supported. Anything else is "likely", "unlikely" or "unknown" and is said so.
/// </para>
/// </summary>
public static partial class ResizableBarAdvisor
{
    /// <summary>The window of about the card's memory is the sign: three quarters or more of it. Windows sees a 256 MB window without the feature.</summary>
    private const double LargeWindowShare = 0.75;

    private const ulong FourGiB = 1UL << 32;

    public static BarAssessment Assess(FirmwareFacts facts, DriverBarState? driver = null)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var gpu = facts.Gpus.Where(g => g.IsDiscrete).OrderByDescending(g => g.VideoMemoryBytes).FirstOrDefault();
        if (gpu is null)
        {
            return new("none", BarVerdict.NotApplicable, [], "This PC has no discrete graphics card; Resizable BAR is about a card with its own memory.", [], false);
        }

        var name = gpu.Name ?? "unknown graphics card";
        var layers = new List<BarLayer>();
        var cpu = CpuSupport(facts.CpuName);
        var card = GpuSupport(gpu);
        layers.Add(new("Processor", cpu.Support.ToString(), cpu.Detail));
        layers.Add(new("Graphics card", card.Support.ToString(), card.Detail));

        var vram = gpu.VideoMemoryBytes!.Value;
        var window = gpu.LargestBarBytes;
        var large = window is { } w && w >= (ulong)(vram * LargeWindowShare);
        var small = window is not null && !large;

        // The board: its BIOS options cannot be read from Windows; a large window proves the chain works.
        layers.Add(new("Motherboard BIOS", large ? "Evidently supported" : "Unknown", large ? "The memory window is large, so the board and its BIOS already do their part." : "Windows cannot read what the BIOS offers; the board needs a BIOS with Above 4G Decoding and Re-Size BAR options."));

        // Above 4G Decoding: provable only when the window sits at or above 4 GiB.
        var above = gpu.LargestBarStart is { } start && start >= FourGiB
            ? new BarLayer("Above 4G Decoding", "On (inferred)", $"The card's largest window starts at {start / (1UL << 20)} MB, above 4 GiB: the firmware places it there only with Above 4G Decoding on.")
            : new BarLayer("Above 4G Decoding", "Unknown", "A 256 MB window fits below 4 GiB with or without it, so Windows cannot tell; the BIOS shows the real setting.");
        layers.Add(above);

        layers.Add(new("Re-Size BAR in the BIOS", large ? "On (inferred)" : small ? "Probably off" : "Unknown",
            large ? $"The window is {Mb(window!.Value)} of the card's {Mb(vram)}." : small ? $"The window is only {Mb(window!.Value)} of the card's {Mb(vram)}: Re-Size BAR (or Above 4G Decoding, which it needs) is off in the BIOS, or the card's firmware lacks it. Windows cannot read the BIOS to say which." : "Windows did not report the window."));

        var isAmd = string.Equals(gpu.Vendor, "AMD", StringComparison.OrdinalIgnoreCase) || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase);
        layers.Add(driver is null || !isAmd
            ? new("Driver", isAmd ? "Not read" : "Per game", isAmd ? "The driver's SmartAccess Memory switch was not read." : "NVIDIA's driver enables Resizable BAR per game from its own list; the window above is what Windows gave the card.")
            : new BarLayer("Driver", driver.Enabled is { } e ? (e ? "On" : "Off") : "Unknown", driver.Supported is false ? "The driver says SmartAccess Memory is not supported." : "The driver's own SmartAccess Memory switch (read through ADLX)."));

        var steps = FirmwareKnowledge.Default.Get("resizable-bar")?.StepsFor(facts.Vendor) ?? [];
        if (string.Equals(facts.BootMode, "Legacy BIOS", StringComparison.OrdinalIgnoreCase))
        {
            return new(name, BarVerdict.NeedsUefi, layers, "Windows boots in legacy (CSM) mode. Resizable BAR needs UEFI boot; changing that on an installed Windows can make it unable to start, so check the boot mode item first.", steps, false);
        }

        if (card.Support == BarSupport.NotSupported)
        {
            return new(name, BarVerdict.NotSupported, layers, $"{card.Detail} Mesura does not enable it by other means (patched firmware can brick a motherboard).", [], false);
        }

        if (cpu.Support is BarSupport.NotSupported or BarSupport.Unlikely && !large)
        {
            return new(name, BarVerdict.NotSupported, layers, $"The processor is probably too old: {cpu.Detail}", [], false);
        }

        if (large)
        {
            if (isAmd && driver?.Enabled is false)
            {
                return new(name, BarVerdict.DriverSwitchOff, layers, "The memory window is large, but the AMD driver's SmartAccess Memory switch is off. Turning it on is the one part Mesura can do (a preview first; it never touches the BIOS).", [], driver.Supported is true);
            }

            return new(name, BarVerdict.Enabled, layers, $"On: the memory window ({Mb(window!.Value)}) is about the size of the card's memory ({Mb(vram)}).", [], false);
        }

        if (small)
        {
            return new(name, BarVerdict.NeedsBios, layers,
                $"Supported hardware, but off: the memory window is only {Mb(window!.Value)} of {Mb(vram)}. It is enabled in the BIOS (Above 4G Decoding, then Re-Size BAR); Windows cannot read those settings, so Mesura cannot say which is off, and it never changes the BIOS.", steps, false);
        }

        return new(name, BarVerdict.Unknown, layers, "Windows did not report the card's memory window, so Mesura cannot say whether the feature is on.", steps, false);
    }

    private static string Mb(ulong bytes) => $"{(bytes >> 20).ToString(CultureInfo.InvariantCulture)} MB";

    // ---- processor ---------------------------------------------------------------------------------------------------------------------------------------------

    internal static (BarSupport Support, string Detail) CpuSupport(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return (BarSupport.Unknown, "The processor's name was not read.");
        }

        if (RyzenRegex().Match(name) is { Success: true } r)
        {
            var model = r.Groups[2].Value;
            var series = int.Parse(model[..1], CultureInfo.InvariantCulture);
            var apu = r.Groups[3].Value.StartsWith('G');
            return series switch
            {
                >= 7 => (BarSupport.Supported, $"AMD Ryzen {series}000 series or newer: later than the Zen 3 (Ryzen 5000) generation NVIDIA and AMD documented."),
                5 when apu => (BarSupport.Likely, "A Ryzen 5000 APU: the Zen 3 generation is documented, APUs depend on the board's BIOS."),
                5 or 6 => (BarSupport.Supported, "AMD Zen 3 (Ryzen 5000 series) or newer: documented by NVIDIA (400 series boards with Zen 3 BIOS support, and 500 series) and AMD."),
                3 when apu && (model.StartsWith("32", StringComparison.Ordinal) || model.StartsWith("34", StringComparison.Ordinal)) => (BarSupport.Unlikely, "Ryzen 3200G / 3400G were excluded when AMD extended SmartAccess Memory to Ryzen 3000."),
                3 => (BarSupport.Likely, "Ryzen 3000: AMD extended SmartAccess Memory to it on B550 and X570 boards in 2021; depends on the board's BIOS."),
                _ => (BarSupport.Unlikely, "Older than Ryzen 3000: not in AMD's or NVIDIA's documented lists."),
            };
        }

        if (UltraRegex().IsMatch(name))
        {
            return (BarSupport.Supported, "Intel Core Ultra: newer than the 10th and 11th generation NVIDIA documented.");
        }

        if (IntelRegex().Match(name) is { Success: true } i)
        {
            var number = i.Groups[1].Value;
            var generation = number.Length >= 5 ? int.Parse(number[..2], CultureInfo.InvariantCulture)
                : number.StartsWith('1') ? int.Parse(number[..2], CultureInfo.InvariantCulture) : int.Parse(number[..1], CultureInfo.InvariantCulture);
            return generation switch
            {
                >= 10 => (BarSupport.Supported, $"Intel Core, generation {generation}: NVIDIA documented the 10th and 11th generation; later ones are newer."),
                8 or 9 => (BarSupport.Likely, $"Intel Core, generation {generation}: older than the documented 10th generation; some boards' BIOS offer it."),
                _ => (BarSupport.Unlikely, $"Intel Core, generation {generation}: older than anything documented."),
            };
        }

        return (BarSupport.Unknown, $"Mesura does not recognise the processor '{name}'; it does not guess.");
    }

    // ---- graphics card -----------------------------------------------------------------------------------------------------------------------------------------

    internal static (BarSupport Support, string Detail) GpuSupport(FirmwareGpu gpu)
    {
        var name = gpu.Name ?? string.Empty;
        if (NvidiaRegex().Match(name) is { Success: true } n)
        {
            var series = int.Parse(n.Groups[2].Value, CultureInfo.InvariantCulture);
            return series switch
            {
                >= 30 => (BarSupport.Supported, series == 30
                    ? "GeForce RTX 30 series: NVIDIA's launch page (2021) lists it; some early cards need their maker's VBIOS update. RTX 40 and 50 series ship with it."
                    : $"GeForce RTX {series} series: newer than the RTX 30 series NVIDIA documented; ships with the feature."),
                _ => (BarSupport.NotSupported, $"GeForce {n.Groups[1].Value} {series} series: NVIDIA's documentation covers the RTX 30 series and newer only."),
            };
        }

        if (AmdRegex().Match(name) is { Success: true } a)
        {
            var series = int.Parse(a.Groups[1].Value, CultureInfo.InvariantCulture);
            return series switch
            {
                >= 6 => (BarSupport.Supported, $"Radeon RX {series}000 series: AMD's SmartAccess Memory launched with RX 6000; later series are newer."),
                5 => (BarSupport.Unknown, "Radeon RX 5000: not in the lists Mesura could read."),
                _ => (BarSupport.Unlikely, $"Radeon RX {series}000 series: older than RX 6000."),
            };
        }

        if (name.Contains("Arc", StringComparison.OrdinalIgnoreCase) && name.Contains("Intel", StringComparison.OrdinalIgnoreCase))
        {
            return (BarSupport.Supported, "Intel Arc: designed to need Resizable BAR.");
        }

        return (BarSupport.Unknown, $"Mesura does not recognise the card '{name}'; it does not guess.");
    }

    [GeneratedRegex(@"Ryzen\s+(?:AI\s+)?(?:Threadripper\s+)?(?:PRO\s+)?(\d)\s+(?:PRO\s+)?(\d{4,5})([A-Z0-9]*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RyzenRegex();

    [GeneratedRegex(@"\bi[3579]-(\d{4,5})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IntelRegex();

    [GeneratedRegex(@"\bUltra\s+[3579]\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UltraRegex();

    [GeneratedRegex(@"\b(RTX|GTX)\s*(?:TITAN\s*)?(\d{2})\d{2}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NvidiaRegex();

    [GeneratedRegex(@"\bRX\s*(\d)\d{3}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AmdRegex();
}
