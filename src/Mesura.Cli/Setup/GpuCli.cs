using System.Text.Json;
using System.Text.Json.Serialization;
using Mesura.Core.Firmware;
using Mesura.Core.GpuControl;
using Mesura.Platform.Windows.Gpu;
using Mesura.Platform.Windows.Windows;
using Mesura.Setup.Windows;
using Mesura.Setup.Windows.Gpu;

namespace Mesura.Cli.Setup;

/// <summary>
/// <c>mesura gpu amd [--json]</c>: what AMD's driver reports through ADLX about the Radeon GPUs (0.6, read only; no setter is ever called). It runs ADLX in this process, so a crash inside AMD's
/// DLL ends this command and nothing else: a caller that must survive one (the window) starts this command as a child process. Exit 0 when ADLX answered, 3 when it is not available here.
/// </summary>
internal static class GpuCli
{
    public const string Usage = """
        mesura gpu amd [--json]
          mesura gpu amd set <antilag|sam|freesync> <on|off> [--gpu N] [--display N] [--apply]
          mesura gpu amd undo <change id> [--apply]
          mesura gpu amd changes
          mesura gpu nvidia [--json]
          mesura gpu vrr [--json]
          mesura gpu bar [--json]
        """;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static int Run(string[] args)
    {
        if (args.Length >= 1 && args[0].Equals("nvidia", StringComparison.OrdinalIgnoreCase))
        {
            return RunNvidia(args.Skip(1).ToArray());
        }

        if (args.Length >= 1 && args[0].Equals("bar", StringComparison.OrdinalIgnoreCase))
        {
            return RunBar(args.Skip(1).ToArray());
        }

        if (args.Length >= 1 && args[0].Equals("vrr", StringComparison.OrdinalIgnoreCase))
        {
            return RunVrr(args.Skip(1).ToArray());
        }

        if (args.Length >= 2 && args[0].Equals("amd", StringComparison.OrdinalIgnoreCase) && args[1] is "set" or "undo" or "changes")
        {
            return RunChange(args[1..]);
        }

        var json = args.Contains("--json", StringComparer.OrdinalIgnoreCase);
        if (args.Length == 0 || !args[0].Equals("amd", StringComparison.OrdinalIgnoreCase) || args.Skip(1).Any(a => !a.Equals("--json", StringComparison.OrdinalIgnoreCase)))
        {
            Console.Error.WriteLine($"Usage: {Usage}");
            return 2;
        }

        var report = AmdGpuReader.Read();
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(report, Json));
            return report.Availability == AmdAvailability.Available ? 0 : 3;
        }

        Console.WriteLine("ADLX report (read only; nothing was changed).");
        if (report.Availability != AmdAvailability.Available)
        {
            Console.WriteLine($"  ADLX is not available here: {report.Availability} ({report.Failure}). No AMD driver with ADLX, or it would not start.");
            return 3;
        }

        Console.WriteLine($"  Driver's ADLX {report.DllVersion}; asked for {report.AskedVersion}; {report.Gpus.Count} GPU(s).");
        if (report.Failure is { } failure)
        {
            Console.WriteLine($"  Problem: {failure}");
        }

        foreach (var gpu in report.Gpus)
        {
            Console.WriteLine();
            Console.WriteLine($"GPU {gpu.Index}: {gpu.Name ?? "unknown"}  (vendor {gpu.VendorId ?? "?"}, device {gpu.DeviceId ?? "?"}, revision {gpu.RevisionId ?? "?"}), {gpu.VramMb?.ToString() ?? "?"} MB {gpu.VramType}, BIOS {gpu.BiosVersion ?? "?"}");
            Console.WriteLine($"  Anti-Lag {Show(gpu.AntiLag)}{(gpu.AntiLagLevel is { } level ? $", level {level}" : "")}; Chill {Show(gpu.Chill)}; Boost {Show(gpu.Boost)}; Image Sharpening {Show(gpu.ImageSharpening)}; Enhanced Sync {Show(gpu.EnhancedSync)}");
            Console.WriteLine($"  Frame rate target {Show(gpu.FrameRateTarget)}; SmartAccess Memory {Show(gpu.SmartAccessMemory)}");
            Console.WriteLine($"  V-Sync mode: {gpu.VerticalRefreshMode ?? "unknown"} (the setting's own flag: {Show(gpu.VerticalRefresh)}; the mode is what counts); Radeon Super Resolution {Show(gpu.RadeonSuperResolution)}{(gpu.SuperResolutionSharpness is { } sharp ? $", sharpness {sharp}" : "")}");
            if (gpu.Tessellation is { Supported: true } tess)
            {
                Console.WriteLine($"  Tessellation: {tess.Mode ?? "?"}, level {tess.Level?.ToString() ?? "?"}");
            }
            if (gpu.Tuning is { } t)
            {
                Console.WriteLine($"  Tuning exists: auto {Flag(t.Auto)}, preset {Flag(t.Preset)}, manual GFX {Flag(t.ManualGfx)}, VRAM {Flag(t.ManualVram)}, fan {Flag(t.ManualFan)}, power {Flag(t.ManualPower)}; at factory {Flag(t.AtFactory)}");
            }

            if (gpu.Metrics is { } m)
            {
                Console.WriteLine($"  Now: {m.ClockMhz?.ToString() ?? "?"} MHz, memory {m.MemoryClockMhz?.ToString() ?? "?"} MHz, {m.TemperatureC?.ToString("0.#") ?? "?"} C, {m.PowerW?.ToString("0.#") ?? "?"} W, fan {m.FanRpm?.ToString() ?? "?"} rpm, usage {m.UsagePercent?.ToString("0.#") ?? "?"} %");
            }

            foreach (var problem in gpu.Problems)
            {
                Console.WriteLine($"  (unknown) {problem}");
            }
        }

        if (report.HybridGraphics is { } hybrid)
        {
            Console.WriteLine();
            Console.WriteLine($"Hybrid graphics: {hybrid}");
        }

        foreach (var d in report.Displays)
        {
            Console.WriteLine();
            Console.WriteLine($"Display {d.Index}: {d.Name ?? "unknown"}  ({d.Type ?? "?"}, {d.Connector ?? "?"}), {d.NativeWidth?.ToString() ?? "?"}x{d.NativeHeight?.ToString() ?? "?"} at {d.RefreshHz?.ToString("0.##") ?? "?"} Hz, {d.ScanType ?? "?"}");
            Console.WriteLine($"  FreeSync {Show(d.FreeSync)}; Virtual Super Resolution {Show(d.VirtualSuperResolution)}; GPU scaling {Show(d.GpuScaling)}; integer scaling {Show(d.IntegerScaling)}");
            Console.WriteLine($"  Scaling mode {d.ScalingMode ?? "unknown"}; colour depth {d.ColorDepth ?? "unknown"}; pixel format {d.PixelFormat ?? "unknown"}");
            foreach (var problem in d.Problems)
            {
                Console.WriteLine($"  (unknown) {problem}");
            }
        }

        return 0;
    }

    /// <summary>
    /// <c>set</c>, <c>undo</c> and <c>changes</c> (0.6): a GPU switch is previewed unless <c>--apply</c> is given; nothing is written without it, and every written change is recorded first and read again by a new ADLX session.
    /// </summary>
    private static int RunChange(string[] args)
    {
        var apply = args.Contains("--apply", StringComparer.OrdinalIgnoreCase);
        var store = new SetupStore();
        var changer = new AmdGpuChanger(store);
        switch (args[0])
        {
            case "changes":
                var ids = store.ListGpuChanges();
                Console.WriteLine(ids.Count == 0 ? $"No GPU changes recorded yet ({store.GpuFolder})." : string.Join(Environment.NewLine, ids.Select(id =>
                    GpuChangeRecord.FromJson(store.ReadGpuChange(id) ?? string.Empty) is { } r ? $"{id}  {r.Status,-16}  {r.Feature} {(r.Requested ? "on" : "off")}  {r.GpuName}{(r.DisplayName is { } d ? $" / {d}" : string.Empty)}" : $"{id}  (unreadable)")));
                return 0;
            case "undo" when args.Length >= 2:
                return Print(changer.Revert(args[1], apply), apply, undo: true);
            case "set" when args.Length >= 3 && Feature(args[1]) is { } feature && args[2] is "on" or "off":
                var gpu = Number(args, "--gpu") ?? 0;
                var display = Number(args, "--display");
                if (feature == GpuFeature.FreeSync && display is null)
                {
                    Console.Error.WriteLine("FreeSync is a display setting: give --display N (the numbers are those of 'mesura gpu amd').");
                    return 2;
                }

                var change = new AmdChange(feature, gpu, display, args[2] == "on");
                return Print(apply ? changer.Apply(change) : changer.Preview(change), apply, undo: false);
            default:
                Console.Error.WriteLine($"Usage: {Usage}");
                return 2;
        }
    }

    /// <summary><c>mesura gpu nvidia</c>: what NVIDIA's driver says through NVAPI (read only), like <c>mesura gpu amd</c>; exit 3 when there is no NVIDIA driver.</summary>
    private static int RunNvidia(string[] rest)
    {
        if (rest.Any(a => !a.Equals("--json", StringComparison.OrdinalIgnoreCase)))
        {
            Console.Error.WriteLine($"Usage: {Usage}");
            return 2;
        }

        var report = NvidiaGpuReader.Read();
        if (rest.Length > 0)
        {
            Console.WriteLine(JsonSerializer.Serialize(report, Json));
            return report.Availability == NvidiaAvailability.Available ? 0 : 3;
        }

        Console.WriteLine("NVAPI report (read only; nothing was changed).");
        if (report.Availability != NvidiaAvailability.Available)
        {
            Console.WriteLine($"  NVAPI is not available here: {report.Availability} ({report.Failure}). No NVIDIA driver, or it would not start.");
            return 3;
        }

        Console.WriteLine($"  Driver {report.DriverVersion} ({report.Branch}); {report.Gpus.Count} GPU(s).");
        foreach (var gpu in report.Gpus)
        {
            Console.WriteLine($"GPU {gpu.Index}: {gpu.Name ?? "unknown"}");
            foreach (var d in gpu.Displays)
            {
                Console.WriteLine($"  Display {d.Index} ({d.GdiName ?? "no Windows output matched"}): variable refresh {Three(d.VrrPossible, "possible", "not possible")}, {Three(d.VrrEnabled, "on", "off")}{(d.VrrRequested is true ? ", requested" : string.Empty)}{(d.Problem is { } p ? $" ({p})" : string.Empty)}");
            }
        }

        foreach (var profile in report.Profiles)
        {
            Console.WriteLine($"Profile '{profile.Kind}'{(profile.SameAsBase is true ? ": the same as the Base Profile" : string.Empty)}{(profile.Problem is { } pp ? $": {pp}" : string.Empty)}");
            foreach (var s in profile.Settings)
            {
                Console.WriteLine($"  {s.Label,-34} {(s.Set is true ? $"set: 0x{s.Value:X8} ({s.Source})" : s.Set is false ? "not set (the driver's default applies)" : $"unknown ({s.Status})")}");
            }
        }

        return 0;
    }

    /// <summary><c>mesura gpu vrr</c>: every display a driver reports, whether variable refresh is on, can be turned on or is not possible, and why (read only).</summary>
    private static int RunVrr(string[] rest)
    {
        if (rest.Any(a => !a.Equals("--json", StringComparison.OrdinalIgnoreCase)))
        {
            Console.Error.WriteLine($"Usage: {Usage}");
            return 2;
        }

        var facts = VrrFactsReader.Read(out var notes);
        var assessment = DisplayVrr.Assess(facts);
        if (rest.Length > 0)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { assessment, notes }, Json));
            return 0;
        }

        Console.WriteLine("Variable refresh (read only; nothing was changed).");
        Console.WriteLine(assessment.Summary);
        foreach (var v in assessment.Displays)
        {
            var f = v.Fact;
            Console.WriteLine();
            Console.WriteLine($"{f.Vendor} display {f.Index}: {f.Name}{(f.RefreshHz is { } hz ? $", {hz:0.##} Hz" : string.Empty)}: {v.State}");
            Console.WriteLine($"  {v.Explanation}");
            if (v.Action is { } action)
            {
                Console.WriteLine($"  What to do: {action}");
            }
        }

        foreach (var note in notes)
        {
            Console.WriteLine($"(note) {note}");
        }

        return 0;
    }

    /// <summary>
    /// <c>mesura gpu bar</c>: Resizable BAR / SmartAccess Memory for this PC, layer by layer (processor, card, board, Above 4G Decoding, Re-Size BAR in the BIOS, the driver), what is read and what is inferred,
    /// and, when it is off, why and the steps for this board's maker. Read only; the BIOS is never touched.
    /// </summary>
    private static int RunBar(string[] rest)
    {
        if (rest.Any(a => !a.Equals("--json", StringComparison.OrdinalIgnoreCase)))
        {
            Console.Error.WriteLine($"Usage: {Usage}");
            return 2;
        }

        var facts = FirmwareFacts.From(SystemInfoReader.Read("cli"), FirmwareReader.Read());
        DriverBarState? driver = null;
        if (AmdGpuReader.Read() is { Availability: AmdAvailability.Available } amd && amd.Gpus.Count > 0)
        {
            driver = new(amd.Gpus[0].SmartAccessMemory.Supported, amd.Gpus[0].SmartAccessMemory.Enabled);
        }

        var assessment = ResizableBarAdvisor.Assess(facts, driver);
        if (rest.Length > 0)
        {
            Console.WriteLine(JsonSerializer.Serialize(assessment, Json));
            return 0;
        }

        Console.WriteLine("Resizable BAR / SmartAccess Memory (read only; nothing was changed).");
        Console.WriteLine($"  Card: {assessment.Gpu}");
        foreach (var layer in assessment.Layers)
        {
            Console.WriteLine($"  {layer.Name,-24} {layer.State,-22} {layer.Detail}");
        }

        Console.WriteLine();
        Console.WriteLine($"{assessment.Verdict}: {assessment.Reason}");
        if (assessment.MesuraCanChangeIt)
        {
            Console.WriteLine("  Mesura can turn the driver's switch on: mesura gpu amd set sam on (preview; add --apply to write it).");
        }

        foreach (var step in assessment.Instruction)
        {
            Console.WriteLine($"  - {step.En}");
        }

        return 0;
    }

    private static string Three(bool? value, string yes, string no) => value is null ? "unknown" : value.Value ? yes : no;

    private static GpuFeature? Feature(string word) => word switch { "antilag" => GpuFeature.AntiLag, "sam" => GpuFeature.SmartAccessMemory, "freesync" => GpuFeature.FreeSync, _ => null };

    private static int? Number(string[] args, string name)
    {
        var at = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return at >= 0 && at + 1 < args.Length && int.TryParse(args[at + 1], out var n) ? n : null;
    }

    private static int Print(AmdChangeOutcome outcome, bool apply, bool undo)
    {
        if (outcome.Preview is { } p)
        {
            Console.WriteLine($"{p.Gpu}{(p.Display is { } d ? $" / {d}" : string.Empty)}: {p.Feature} is {Show(p.Current)}; asked: {(p.Requested ? "on" : "off")}.");
            foreach (var effect in p.SideEffects)
            {
                Console.WriteLine($"  Beside it: {effect}");
            }
        }

        Console.WriteLine(outcome.Message);
        if (outcome.Record is { } r)
        {
            Console.WriteLine($"Change {r.Id}: {r.Status}.{(undo ? string.Empty : $" Undo with: mesura gpu amd undo {r.Id} --apply")}");
        }

        if (!apply && outcome.Status == GpuChangeStatus.NotWritten && outcome.Preview?.CanApply == true)
        {
            Console.WriteLine("Preview only: nothing was changed. Add --apply to write it.");
        }

        return outcome.Status is GpuChangeStatus.Failed or GpuChangeStatus.NotConfirmed or GpuChangeStatus.RevertIncomplete ? 1 : 0;
    }

    private static string Show(GpuFeatureState state) =>
        state.Supported is false ? "not supported" : state.Enabled is null ? "unknown" : state.Enabled.Value ? $"on{(state.Level is { } l ? $" (level {l})" : string.Empty)}" : $"off{(state.Level is { } l2 ? $" (level {l2})" : string.Empty)}";

    private static string Flag(bool? value) => value is null ? "unknown" : value.Value ? "yes" : "no";

    private static string Show(AmdFeature feature) =>
        feature.Supported is null ? $"unknown{(feature.Problem is { } p ? $" ({p})" : "")}" : !feature.Supported.Value ? "not supported" : feature.Enabled is null ? "supported, state unknown" : feature.Enabled.Value ? "on" : "off";
}
