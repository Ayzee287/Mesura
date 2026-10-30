using System.Globalization;
using System.Text.RegularExpressions;

namespace Mesura.Core.Firmware;

/// <summary>
/// Decides, for this PC, which BIOS or UEFI settings are worth a look (0.4, docs/firmware.md). It reads facts and nothing else: it changes nothing, and
/// no finding is a promise of speed. Each finding says what was seen (as keys the app words), whether it was read directly or inferred, and
/// what changing it could cost. What Windows cannot see (PBO and Curve Optimizer, the memory kit's rating when its part number is not
/// regular) is reported as such, never guessed; a firmware menu path is the knowledge file's business, not this class's.
/// </summary>
public static partial class FirmwareAnalyzer
{
    /// <summary>Intel's final fix for the Vmin Shift instability of 13th and 14th generation desktop processors shipped as microcode 0x12B (December 2024).</summary>
    public const uint IntelVminFixMicrocode = 0x12B;

    /// <summary>A BIOS older than this many months is worth a look at the maker's page (not a reason to update by itself).</summary>
    public const int BiosAgeWorthCheckingMonths = 18;

    public static FirmwareReport Analyze(FirmwareFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var findings = new List<FirmwareFinding>
        {
            MemoryProfile(facts),
            MemoryChannels(facts),
            ResizableBar(facts),
            BiosUpdate(facts),
            BootMode(facts),
            SecureBootAndTpm(facts),
            Virtualization(facts),
            CpuBoost(facts),
            PcieLink(facts),
        };
        return new FirmwareReport(facts, findings);
    }

    // ---- memory ---------------------------------------------------------------------------------------------------------------

    private static IReadOnlyList<Mesura.Core.SystemInfo.SystemMemoryModule> Modules(FirmwareFacts f) => f.Memory.Where(m => m.SizeBytes is > 0).ToList();

    private static bool Soldered(FirmwareFacts f)
    {
        var modules = Modules(f);
        return f.IsPortable || modules.Any(m => string.Equals(m.FormFactor, "Soldered", StringComparison.OrdinalIgnoreCase) || (m.Type?.StartsWith("LPDDR", StringComparison.OrdinalIgnoreCase) ?? false));
    }

    private static FirmwareFinding MemoryProfile(FirmwareFacts f)
    {
        const string id = "memory-profile";
        var modules = Modules(f);
        if (modules.Count == 0)
        {
            return new(id, FirmwareStatus.Unknown, FirmwareCertainty.Confirmed, FirmwareRisk.Safe, [FirmwareObservation.Of("unreadable")]);
        }

        if (Soldered(f))
        {
            return new(id, FirmwareStatus.NotApplicable, FirmwareCertainty.Confirmed, FirmwareRisk.Safe, [FirmwareObservation.Of("memory.soldered")]);
        }

        var speeds = modules.Select(m => m.ConfiguredMts ?? m.SpeedMts).Where(s => s is > 0).Select(s => s!.Value).ToList();
        if (speeds.Count == 0)
        {
            return new(id, FirmwareStatus.Unknown, FirmwareCertainty.Confirmed, FirmwareRisk.Safe, [FirmwareObservation.Of("unreadable")]);
        }

        var running = speeds.Min();
        var type = modules.Select(m => m.Type).FirstOrDefault(t => !string.IsNullOrEmpty(t)) ?? "";
        var rated = modules.Select(m => MemoryRatings.RatedMts(m.PartNumber)).Where(r => r is not null).Select(r => r!.Value).DefaultIfEmpty(0).Max();
        var part = modules.Select(m => m.PartNumber).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)) ?? "";
        var speed = running.ToString(CultureInfo.InvariantCulture);

        if (rated > 0)
        {
            return running + 50 >= rated
                ? new(id, FirmwareStatus.InPlace, FirmwareCertainty.Likely, FirmwareRisk.Safe, [FirmwareObservation.Of("memory.at-rating", speed, rated.ToString(CultureInfo.InvariantCulture), type)])
                : new(id, FirmwareStatus.Review, FirmwareCertainty.Likely, FirmwareRisk.Safe, [FirmwareObservation.Of("memory.below-rating", speed, rated.ToString(CultureInfo.InvariantCulture), part, type)]);
        }

        var top = MemoryRatings.JedecTop(type);
        if (top is { } jedec && running > jedec)
        {
            return new(id, FirmwareStatus.InPlace, FirmwareCertainty.Likely, FirmwareRisk.Safe, [FirmwareObservation.Of("memory.above-base", speed, type)]);
        }

        // At or under the base speed of the type: right if the kit is a plain one, a missed profile if it is a gaming kit. Windows can't say which.
        return new(id, FirmwareStatus.Unknown, FirmwareCertainty.Likely, FirmwareRisk.Safe, [FirmwareObservation.Of("memory.base-speed", speed, type, part)]);
    }

    private static FirmwareFinding MemoryChannels(FirmwareFacts f)
    {
        const string id = "memory-channels";
        var modules = Modules(f);
        if (modules.Count == 0)
        {
            return new(id, FirmwareStatus.Unknown, FirmwareCertainty.Confirmed, FirmwareRisk.Safe, [FirmwareObservation.Of("unreadable")]);
        }

        if (Soldered(f))
        {
            return new(id, FirmwareStatus.NotApplicable, FirmwareCertainty.Confirmed, FirmwareRisk.Safe, [FirmwareObservation.Of("memory.soldered")]);
        }

        var count = modules.Count.ToString(CultureInfo.InvariantCulture);
        if (modules.Count == 1 && (f.MemorySlots ?? 2) >= 2)
        {
            return new(id, FirmwareStatus.Review, FirmwareCertainty.Confirmed, FirmwareRisk.Safe,
                [FirmwareObservation.Of("memory.single", count, (f.MemorySlots ?? 0).ToString(CultureInfo.InvariantCulture))]);
        }

        if (modules.Count == 2)
        {
            var banks = modules.Select(m => m.Bank).Where(b => !string.IsNullOrWhiteSpace(b)).Select(b => b!.Trim().ToUpperInvariant()).ToList();
            if (banks.Count == 2 && banks[0] == banks[1] && (f.MemorySlots ?? 0) >= 4)
            {
                return new(id, FirmwareStatus.Review, FirmwareCertainty.Likely, FirmwareRisk.Safe, [FirmwareObservation.Of("memory.same-channel", banks[0])]);
            }

            return new(id, FirmwareStatus.InPlace, FirmwareCertainty.Likely, FirmwareRisk.Safe, [FirmwareObservation.Of("memory.dual")]);
        }

        return new(id, FirmwareStatus.InPlace, FirmwareCertainty.Likely, FirmwareRisk.Safe, [FirmwareObservation.Of(modules.Count >= 4 ? "memory.crowded" : "memory.dual", count)]);
    }

    // ---- graphics ---------------------------------------------------------------------------------------------------------------

    private static FirmwareFinding ResizableBar(FirmwareFacts f)
    {
        const string id = "resizable-bar";
        var gpu = f.Gpus.Where(g => g.IsDiscrete).OrderByDescending(g => g.VideoMemoryBytes).FirstOrDefault();
        if (gpu is null)
        {
            return new(id, FirmwareStatus.NotApplicable, FirmwareCertainty.Confirmed, FirmwareRisk.Safe, [FirmwareObservation.Of("rebar.no-card")]);
        }

        var name = gpu.Name ?? "";
        var vram = (gpu.VideoMemoryBytes!.Value >> 20).ToString(CultureInfo.InvariantCulture);
        if (string.Equals(f.BootMode, "Legacy BIOS", StringComparison.OrdinalIgnoreCase))
        {
            return new(id, FirmwareStatus.Review, FirmwareCertainty.Confirmed, FirmwareRisk.Careful, [FirmwareObservation.Of("rebar.needs-uefi", name)]);
        }

        if (gpu.LargestBarBytes is not { } bar)
        {
            return new(id, FirmwareStatus.Unknown, FirmwareCertainty.Likely, FirmwareRisk.Safe, [FirmwareObservation.Of("rebar.unreadable", name, vram)]);
        }

        var barMb = (bar >> 20).ToString(CultureInfo.InvariantCulture);

        // With Resizable BAR the card's memory window is as large as its memory (or close: a card with 12 GB may expose 16 GB); without it Windows
        // sees the 256 MB window. A window of about the size of the card's memory is the sign; one of 256 MB on a bigger card is the other.
        return bar >= gpu.VideoMemoryBytes.Value / 4 * 3
            ? new(id, FirmwareStatus.InPlace, FirmwareCertainty.Likely, FirmwareRisk.Safe, [FirmwareObservation.Of("rebar.on", name, barMb, vram)])
            : new(id, FirmwareStatus.Review, FirmwareCertainty.Likely, FirmwareRisk.Safe, [FirmwareObservation.Of("rebar.off", name, barMb, vram)]);
    }

    private static FirmwareFinding PcieLink(FirmwareFacts f)
    {
        const string id = "pcie-link";
        var gpu = f.Gpus.FirstOrDefault(g => g.IsDiscrete);
        return gpu is null
            ? new(id, FirmwareStatus.NotApplicable, FirmwareCertainty.Confirmed, FirmwareRisk.Safe, [FirmwareObservation.Of("rebar.no-card")])
            : new(id, FirmwareStatus.Optional, FirmwareCertainty.Likely, FirmwareRisk.Safe, [FirmwareObservation.Of("pcie.check", gpu.Name ?? "")]);
    }

    // ---- the firmware itself ----------------------------------------------------------------------------------------------------

    private static FirmwareFinding BiosUpdate(FirmwareFacts f)
    {
        const string id = "bios-update";
        var version = f.BiosVersion ?? "";
        var date = f.BiosReleased;
        var seen = new List<FirmwareObservation>();
        if (version.Length > 0)
        {
            seen.Add(FirmwareObservation.Of("bios.version", version, f.BiosDate ?? ""));
        }

        var affected = f.IsIntel && IntelDesktop13Or14(f.CpuName);
        if (affected)
        {
            if (f.Extras.Microcode is { } micro)
            {
                var hex = "0x" + micro.ToString("X", CultureInfo.InvariantCulture);
                if (micro < IntelVminFixMicrocode)
                {
                    seen.Add(FirmwareObservation.Of("bios.intel-microcode-old", hex, f.CpuName ?? ""));
                    return new(id, FirmwareStatus.Review, FirmwareCertainty.Likely, FirmwareRisk.Careful, seen);
                }

                seen.Add(FirmwareObservation.Of("bios.intel-microcode-ok", hex));
            }
            else
            {
                seen.Add(FirmwareObservation.Of("bios.intel-microcode-unknown", f.CpuName ?? ""));
                return new(id, FirmwareStatus.Unknown, FirmwareCertainty.Likely, FirmwareRisk.Careful, seen);
            }
        }

        if (date is null)
        {
            return new(id, FirmwareStatus.Unknown, FirmwareCertainty.Confirmed, FirmwareRisk.Careful, seen.Count > 0 ? seen : [FirmwareObservation.Of("unreadable")]);
        }

        var months = (int)((f.ReadAt.Year - date.Value.Year) * 12 + f.ReadAt.Month - date.Value.Month);
        seen.Add(FirmwareObservation.Of("bios.age", months.ToString(CultureInfo.InvariantCulture)));
        return months >= BiosAgeWorthCheckingMonths
            ? new(id, FirmwareStatus.Optional, FirmwareCertainty.Confirmed, FirmwareRisk.Careful, seen)
            : new(id, FirmwareStatus.InPlace, FirmwareCertainty.Confirmed, FirmwareRisk.Careful, seen);
    }

    /// <summary>The desktop 13th and 14th generation Core i9, i7 and K-series i5 that Intel's instability guidance covers; mobile parts (H, HX, U, P) are not.</summary>
    public static bool IntelDesktop13Or14(string? cpuName) => !string.IsNullOrWhiteSpace(cpuName) && IntelAffected().IsMatch(cpuName);

    [GeneratedRegex(@"\b(i9-1[34]9\d{2}|i7-1[34]7\d{2}|i5-1[34]6\d{2})(K|KF|KS|F)?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IntelAffected();

    private static FirmwareFinding BootMode(FirmwareFacts f)
    {
        const string id = "boot-mode";
        return f.BootMode switch
        {
            null or "" => new(id, FirmwareStatus.Unknown, FirmwareCertainty.Confirmed, FirmwareRisk.Careful, [FirmwareObservation.Of("unreadable")]),
            var m when string.Equals(m, "UEFI", StringComparison.OrdinalIgnoreCase) => new(id, FirmwareStatus.InPlace, FirmwareCertainty.Confirmed, FirmwareRisk.Careful, [FirmwareObservation.Of("boot.uefi")]),
            _ => new(id, FirmwareStatus.Review, FirmwareCertainty.Confirmed, FirmwareRisk.Careful, [FirmwareObservation.Of("boot.legacy")]),
        };
    }

    private static FirmwareFinding SecureBootAndTpm(FirmwareFacts f)
    {
        const string id = "secure-boot-tpm";
        var seen = new List<FirmwareObservation>();
        var legacy = string.Equals(f.BootMode, "Legacy BIOS", StringComparison.OrdinalIgnoreCase);
        seen.Add(FirmwareObservation.Of(f.SecureBoot switch { true => "sb.on", false => legacy ? "sb.legacy" : "sb.off", _ => "sb.unknown" }));
        seen.Add(FirmwareObservation.Of(f.Extras.TpmPresent switch { true => "tpm.on", false => "tpm.off", _ => "tpm.unknown" }, f.Extras.TpmVersion ?? ""));
        if (f.SecureBoot == true && f.Extras.TpmPresent == true)
        {
            return new(id, FirmwareStatus.InPlace, FirmwareCertainty.Confirmed, FirmwareRisk.Careful, seen);
        }

        if (f.SecureBoot is null && f.Extras.TpmPresent is null)
        {
            return new(id, FirmwareStatus.Unknown, FirmwareCertainty.Confirmed, FirmwareRisk.Careful, seen);
        }

        return new(id, FirmwareStatus.Optional, FirmwareCertainty.Confirmed, FirmwareRisk.Careful, seen);
    }

    private static FirmwareFinding Virtualization(FirmwareFacts f)
    {
        const string id = "virtualization";
        if (f.Extras.HypervisorRunning == true)
        {
            return new(id, FirmwareStatus.InPlace, FirmwareCertainty.Confirmed, FirmwareRisk.Safe, [FirmwareObservation.Of("virt.hypervisor")]);
        }

        return f.Extras.Virtualization switch
        {
            true => new(id, FirmwareStatus.InPlace, FirmwareCertainty.Confirmed, FirmwareRisk.Safe, [FirmwareObservation.Of("virt.on")]),
            false => new(id, FirmwareStatus.Optional, FirmwareCertainty.Confirmed, FirmwareRisk.Safe, [FirmwareObservation.Of("virt.off")]),
            _ => new(id, FirmwareStatus.Unknown, FirmwareCertainty.Confirmed, FirmwareRisk.Safe, [FirmwareObservation.Of("unreadable")]),
        };
    }

    // ---- the processor ----------------------------------------------------------------------------------------------------------

    private static FirmwareFinding CpuBoost(FirmwareFacts f)
    {
        const string id = "cpu-boost";
        if (f.IsPortable)
        {
            return new(id, FirmwareStatus.NotApplicable, FirmwareCertainty.Confirmed, FirmwareRisk.Experimental, [FirmwareObservation.Of("boost.portable")]);
        }

        if (f.IsAmd)
        {
            return new(id, FirmwareStatus.Optional, FirmwareCertainty.Confirmed, FirmwareRisk.Experimental, [FirmwareObservation.Of("boost.amd", f.CpuName ?? "")]);
        }

        if (f.IsIntel)
        {
            return new(id, FirmwareStatus.Optional, FirmwareCertainty.Confirmed, FirmwareRisk.Experimental, [FirmwareObservation.Of("boost.intel", f.CpuName ?? "")]);
        }

        return new(id, FirmwareStatus.Unknown, FirmwareCertainty.Confirmed, FirmwareRisk.Experimental, [FirmwareObservation.Of("unreadable")]);
    }
}
