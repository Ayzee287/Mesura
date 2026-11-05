using System.Text.Json;
using System.Text.Json.Serialization;
using Mesura.Core.Hardware;
using Mesura.Platform.Windows.Windows;

namespace Mesura.Cli.Setup;

/// <summary>
/// <c>mesura hardware [--json]</c>: the hardware profile Mesura decides optimisations from (0.6), as text or JSON, for a PC you cannot open the window on or to
/// check what Mesura believes about a machine you know. Read-only: it asks Windows and changes nothing. It holds model names and PCI ids, no serial
/// numbers, addresses or user names.
/// </summary>
internal static class HardwareCli
{
    public const string Usage = "mesura hardware [--json]";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static int Run(string[] args)
    {
        var json = args.Contains("--json", StringComparer.OrdinalIgnoreCase);
        if (args.Any(a => !a.Equals("--json", StringComparison.OrdinalIgnoreCase)))
        {
            Console.Error.WriteLine($"Usage: {Usage}");
            return 2;
        }

        var profile = HardwareProfileReader.Read(new SystemStateReader(), "cli");
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(profile, Json));
            return 0;
        }

        Console.WriteLine("Hardware profile; nothing was changed. 'unknown' means Windows didn't say, never a guess.");
        var w = profile.Windows;
        Console.WriteLine($"  Windows  : build {w.Build}{(w.IsWindows11 ? " (Windows 11)" : "")}, edition {w.Edition ?? "unknown"}, machine policies {Show(w.PolicyEdition, "honoured", "ignored (Home)")}");
        Console.WriteLine($"  Form     : {profile.Form}; cooling {profile.Cooling} (from the case, not a measurement)");
        var p = profile.Power;
        Console.WriteLine($"  Power    : battery {Show(p.HasBattery, "yes", "no")}, Modern Standby {Show(p.ModernStandby, "yes", "no")}, on mains {Show(p.OnMains, "yes", "no")}");
        Console.WriteLine(profile.Cpu is { } cpu
            ? $"  CPU      : {cpu.Name ?? "unknown"} ({cpu.Maker}), {cpu.Cores?.ToString() ?? "?"} cores / {cpu.Threads?.ToString() ?? "?"} threads, hybrid {Show(cpu.Hybrid, "yes", "no")}"
            : "  CPU      : unknown");
        Console.WriteLine($"  Memory   : {(profile.MemoryGb is { } gb ? gb + " GB" : "unknown")}");
        foreach (var gpu in profile.Gpus)
        {
            Console.WriteLine($"  GPU      : {gpu.Name} ({gpu.Maker}, {(gpu.Integrated is { } integrated ? integrated ? "integrated" : "discrete" : "integrated flag unknown")}), driver {gpu.DriverVersion ?? "unknown"}");
        }

        if (profile.Gpus.Count == 0)
        {
            Console.WriteLine("  GPU      : none read");
        }

        foreach (var disk in profile.Disks)
        {
            Console.WriteLine($"  Disk     : {disk.Model ?? "unknown model"} ({disk.Class}, {(disk.SizeBytes is { } size ? size / (1024UL * 1024 * 1024) + " GB" : "size unknown")}){(disk.HoldsWindows ? " <- Windows" : "")}");
        }

        foreach (var adapter in profile.Network)
        {
            Console.WriteLine($"  Network  : {adapter.Name} ({adapter.Kind}{(adapter.Maker is { } maker ? ", " + maker : "")}), {(adapter.Up ? "up" : "down")}");
        }

        if (profile.Network.Count == 0)
        {
            Console.WriteLine("  Network  : no physical Ethernet or Wi-Fi adapter read");
        }

        return 0;
    }

    private static string Show(bool? value, string yes, string no) => value is null ? "unknown" : value.Value ? yes : no;
}
