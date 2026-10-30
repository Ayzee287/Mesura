using System.Text.Json;
using System.Globalization;

namespace Mesura.Core.Firmware;

/// <summary>
/// The few firmware-level readings that can change when someone changes a BIOS setting, kept between visits (<c>Firmware\last-seen.json</c>)
/// so that after a restart the page can say what changed ("memory now runs at 6000 MT/s, was 4800") instead of asking to be trusted. It
/// holds values, never identifiers: no serial numbers, no names of this PC or its owner.
/// </summary>
public sealed record FirmwareSnapshot(
    DateTimeOffset At,
    string? BiosVersion,
    string? BootMode,
    bool? SecureBoot,
    bool? Tpm,
    bool? Virtualization,
    int? MemoryMts,
    int? MemoryModules,
    long? LargestBarMb,
    uint? Microcode)
{
    public static FirmwareSnapshot Of(FirmwareFacts f)
    {
        var modules = f.Memory.Where(m => m.SizeBytes is > 0).ToList();
        var speeds = modules.Select(m => m.ConfiguredMts ?? m.SpeedMts).Where(s => s is > 0).Select(s => s!.Value).ToList();
        var discrete = f.Gpus.Where(g => g.IsDiscrete).OrderByDescending(g => g.VideoMemoryBytes).FirstOrDefault();
        return new FirmwareSnapshot(
            f.ReadAt,
            f.BiosVersion,
            f.BootMode,
            f.SecureBoot,
            f.Extras.TpmPresent,
            f.Extras.HypervisorRunning == true ? true : f.Extras.Virtualization,
            speeds.Count > 0 ? speeds.Min() : null,
            modules.Count > 0 ? modules.Count : null,
            discrete?.LargestBarBytes is { } bar ? (long)(bar >> 20) : null,
            f.Extras.Microcode);
    }

    /// <summary>What differs between an earlier reading and this one, as keys the app words; nothing when nothing changed or a value wasn't read both times.</summary>
    public static IReadOnlyList<FirmwareChange> Diff(FirmwareSnapshot? before, FirmwareSnapshot now)
    {
        ArgumentNullException.ThrowIfNull(now);
        if (before is null)
        {
            return [];
        }

        var changes = new List<FirmwareChange>();
        void Text(string key, string? a, string? b)
        {
            if (!string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && a != b)
            {
                changes.Add(new FirmwareChange(key, a, b));
            }
        }

        void Flag(string key, bool? a, bool? b)
        {
            if (a is { } x && b is { } y && x != y)
            {
                changes.Add(new FirmwareChange(key, x ? "on" : "off", y ? "on" : "off"));
            }
        }

        void Number<T>(string key, T? a, T? b) where T : struct, IFormattable
        {
            if (a is { } x && b is { } y && !x.Equals(y))
            {
                changes.Add(new FirmwareChange(key, x.ToString(null, CultureInfo.InvariantCulture), y.ToString(null, CultureInfo.InvariantCulture)));
            }
        }

        Text("bios.version", before.BiosVersion, now.BiosVersion);
        Text("boot.mode", before.BootMode, now.BootMode);
        Flag("secure-boot", before.SecureBoot, now.SecureBoot);
        Flag("tpm", before.Tpm, now.Tpm);
        Flag("virtualization", before.Virtualization, now.Virtualization);
        Number("memory.speed", before.MemoryMts, now.MemoryMts);
        Number("memory.modules", before.MemoryModules, now.MemoryModules);
        Number("rebar.window", before.LargestBarMb, now.LargestBarMb);
        if (before.Microcode is { } m1 && now.Microcode is { } m2 && m1 != m2)
        {
            changes.Add(new FirmwareChange("microcode", "0x" + m1.ToString("X", CultureInfo.InvariantCulture), "0x" + m2.ToString("X", CultureInfo.InvariantCulture)));
        }

        return changes;
    }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    /// <summary>Reads a snapshot back; <c>null</c> for anything that isn't one (a damaged file is a missing one, never an error).</summary>
    public static FirmwareSnapshot? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<FirmwareSnapshot>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>One thing that differs from the last visit: a key, the earlier value and the new one.</summary>
public sealed record FirmwareChange(string Key, string Before, string After);

/// <summary>
/// The readings of the last two different visits. A visit that sees the same values leaves them as they were, so a change a restart made
/// stays on the page until the next real change, instead of vanishing the second time the page opens.
/// </summary>
public sealed record FirmwareHistory(FirmwareSnapshot? Previous, FirmwareSnapshot Current)
{
    public static FirmwareHistory Advance(FirmwareHistory? old, FirmwareSnapshot now)
    {
        ArgumentNullException.ThrowIfNull(now);
        if (old is null)
        {
            return new FirmwareHistory(null, now);
        }

        return FirmwareSnapshot.Diff(old.Current, now).Count == 0 ? old : new FirmwareHistory(old.Current, now);
    }

    /// <summary>What differs between the last two different readings.</summary>
    public IReadOnlyList<FirmwareChange> Changes => FirmwareSnapshot.Diff(Previous, Current);

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static FirmwareHistory? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<FirmwareHistory>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
