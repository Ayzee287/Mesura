using System.Text.Json;
using System.Text.Json.Serialization;
using Mesura.Core.Firmware;
using Mesura.Platform.Windows.Windows;

namespace Mesura.Cli.Setup;

/// <summary>
/// <c>mesura firmware [--json]</c>: what the BIOS &amp; Firmware page reads and concludes, as text or JSON, for a PC you cannot open the window on or
/// to compare the page with a machine you know (0.4.1). Read-only: it asks Windows and changes nothing. The wording is the findings' own keys and
/// arguments (the window words them; the command line shows them as they are).
/// </summary>
internal static class FirmwareCli
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static int Run(string[] args)
    {
        var json = args.Contains("--json", StringComparer.OrdinalIgnoreCase);
        if (args.Any(a => !a.Equals("--json", StringComparison.OrdinalIgnoreCase)))
        {
            Console.Error.WriteLine("Usage: mesura firmware [--json]");
            return 2;
        }

        var system = SystemInfoReader.Read("cli");
        var report = FirmwareAnalyzer.Analyze(FirmwareFacts.From(system, FirmwareReader.Read()));
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(report, Json));
            return 0;
        }

        var f = report.Facts;
        Console.WriteLine($"Firmware report, read {f.ReadAt:yyyy-MM-dd HH:mm} (Windows build {f.WindowsBuild}); nothing was changed.");
        Console.WriteLine($"  Machine : {f.MachineKind} {f.MachineManufacturer} {f.MachineModel}".TrimEnd());
        Console.WriteLine($"  Board   : {f.BoardManufacturer} {f.Board}  (vendor class: {report.Vendor})");
        Console.WriteLine($"  BIOS    : {f.BiosVendor} {f.BiosVersion} {f.BiosDate}");
        Console.WriteLine($"  Boot    : {f.BootMode}; Secure Boot {Show(f.SecureBoot)}");
        Console.WriteLine($"  CPU     : {f.CpuName}");
        Console.WriteLine($"  Memory  : {f.Memory.Count} module(s) in {f.MemorySlots?.ToString() ?? "?"} slot(s)");
        foreach (var m in f.Memory)
        {
            Console.WriteLine($"            {m.Manufacturer} {m.PartNumber}: {(m.SizeBytes ?? 0) / (1024 * 1024 * 1024)} GB, configured {m.ConfiguredMts?.ToString() ?? "?"} MT/s, rated max {m.MaxMts?.ToString() ?? "?"} MT/s, bank {m.Bank}");
        }

        foreach (var g in f.Gpus)
        {
            Console.WriteLine($"  GPU     : {g.Name} ({(g.IsDiscrete ? "discrete" : "integrated")}), memory {(g.VideoMemoryBytes ?? 0) / (1024 * 1024)} MB, largest BAR window {(g.LargestBarBytes ?? 0) / (1024 * 1024)} MB");
        }

        Console.WriteLine();
        Console.WriteLine("Findings (id: status, certainty, risk; what was seen):");
        foreach (var finding in report.Findings)
        {
            var seen = string.Join("; ", finding.Seen.Select(s => s.Args.Count == 0 ? s.Key : $"{s.Key}({string.Join(", ", s.Args)})"));
            Console.WriteLine($"  {finding.Id}: {finding.Status}, {finding.Certainty}, {finding.Risk}" + (seen.Length > 0 ? " — " + seen : ""));
        }

        return 0;
    }

    private static string Show(bool? value) => value is null ? "unknown" : value.Value ? "on" : "off";
}
