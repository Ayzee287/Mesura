using Mesura.Core.GpuControl;
using Mesura.Core.Raw;
using Mesura.Platform.Windows.Collectors;

namespace Mesura.Platform.Windows.Gpu;

/// <summary>
/// Gathers, for every display a graphics driver reports on, what the driver says about variable refresh (<see cref="VrrDisplayFact"/>): NVIDIA through NVAPI (tied to the Windows output of the same name, so the
/// refresh rate Windows shows is attached), AMD through ADLX. Read only. It calls the vendors' DLLs in this process, so, like <c>mesura gpu amd</c>, it belongs to the command line, not to the window.
/// </summary>
public static class VrrFactsReader
{
    public static IReadOnlyList<VrrDisplayFact> Read(out IReadOnlyList<string> notes)
    {
        var noted = new List<string>();
        var facts = new List<VrrDisplayFact>();

        Dictionary<string, DisplayOutput> windows = [];
        try
        {
            foreach (var output in DisplayCollector.Collect().Outputs)
            {
                if (output.SourceName.IsKnown)
                {
                    windows[output.SourceName.Value!] = output;
                }
            }
        }
        catch (Exception e) when (e is SourceUnavailableException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            noted.Add($"Windows' display list could not be read ({e.Message}); refresh rates of NVIDIA displays are unknown.");
        }

        var nvidia = NvidiaGpuReader.Read();
        if (nvidia.Availability == NvidiaAvailability.Available)
        {
            foreach (var gpu in nvidia.Gpus)
            {
                foreach (var display in gpu.Displays.Where(d => d.Connected is not false))
                {
                    windows.TryGetValue(display.GdiName ?? string.Empty, out var output);
                    var name = output is { MonitorName.IsKnown: true } ? output.MonitorName.Value! : display.GdiName is { } g ? $"NVIDIA display {g}" : $"NVIDIA display {display.Index}";
                    facts.Add(new("NVIDIA", display.Index, name, display.GdiName, Hz(output), display.VrrPossible, display.VrrEnabled, display.Problem));
                }
            }
        }
        else
        {
            noted.Add($"NVIDIA: {nvidia.Availability}{(nvidia.Failure is { } f ? $" ({f})" : string.Empty)}.");
        }

        var amd = AmdGpuReader.Read();
        if (amd.Availability == AmdAvailability.Available)
        {
            foreach (var display in amd.Displays.Where(d => d.Index >= 0))
            {
                facts.Add(new("AMD", display.Index, display.Name ?? $"AMD display {display.Index}", null, display.RefreshHz, display.FreeSync.Supported, display.FreeSync.Enabled,
                    display.FreeSync.Problem ?? (display.FreeSync.Supported is null ? string.Join("; ", display.Problems) : null)));
            }
        }
        else
        {
            noted.Add($"AMD: {amd.Availability}{(amd.Failure is { } f2 ? $" ({f2})" : string.Empty)}.");
        }

        notes = noted;
        return facts;
    }

    private static double? Hz(DisplayOutput? output) =>
        output is { SignalRefresh: { IsKnown: true, Value: { Denominator: > 0 } rate } } ? (double)rate.Numerator / rate.Denominator : null;
}
