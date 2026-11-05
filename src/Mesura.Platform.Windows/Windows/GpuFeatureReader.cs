using System.Management;
using Mesura.Core.Windows;
using Mesura.Platform.Windows.Gpu;

namespace Mesura.Platform.Windows.Windows;

/// <summary>
/// Answers the engine's question "what does the GPU driver say about this switch" (0.6: AMD Anti-Lag). ADLX is asked only when Windows lists an AMD display adapter, so an NVIDIA or Intel PC (even one
/// with AMD's software installed) never loads the AMD driver library from here. The reading is the read-only <see cref="AmdGpuReader"/>, run in the command line (<see cref="AmdCommandLine"/>) when the
/// caller is the window, because a crash inside AMD's library cannot be caught by managed code; each reading keeps one ADLX session and unloads the library after it (docs/adlx-integration.md, lifecycle rule).
/// </summary>
internal static class GpuFeatureReader
{
    private const string AmdPciVendor = "VEN_1002";

    public static GpuFeatureReading? Read(string vendor, string feature, ISystemSettingSource? source = null, Func<bool?>? adapterPresent = null)
    {
        if (!vendor.Equals(GpuSetting.Amd, StringComparison.OrdinalIgnoreCase) || !feature.Equals(GpuSetting.AntiLag, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var present = (adapterPresent ?? AmdAdapterPresent)();
        if (present is null)
        {
            return GpuFeatureReading.None(GpuAvailability.Unreadable, "Windows' list of graphics adapters couldn't be read.");
        }

        if (present == false)
        {
            return GpuFeatureReading.None(GpuAvailability.NoAdapter);
        }

        // The window never calls AMD's library in its own process (an access violation there ends the process): it asks the command line. The command line calls it directly.
        if (AmdCommandLine.IsCommandLine)
        {
            return FromReport(AmdGpuReader.Read());
        }

        return source?.ReadGpuFeature(vendor, feature)
            ?? GpuFeatureReading.None(GpuAvailability.Unreadable, "The window has no way to ask the command line that reads the AMD driver.");
    }

    /// <summary>The reading for Anti-Lag from an ADLX report (separate so the choice of GPU is testable).</summary>
    internal static GpuFeatureReading FromReport(AmdAdlxReport report)
    {
        switch (report.Availability)
        {
            case AmdAvailability.NoDriver:
                return GpuFeatureReading.None(GpuAvailability.NoDriver, report.Failure);
            case AmdAvailability.Unusable or AmdAvailability.NotStarted:
                return GpuFeatureReading.None(GpuAvailability.Unreadable, report.Failure ?? "ADLX did not start.");
        }

        if (report.Gpus.Count == 0)
        {
            return GpuFeatureReading.None(GpuAvailability.Unreadable, "ADLX lists no GPU.");
        }

        // A discrete card that offers Anti-Lag first, then any GPU that does; otherwise the first GPU, so the answer says why it isn't offered.
        var gpu = report.Gpus.FirstOrDefault(g => g.AntiLag.Supported == true && g.External == true)
            ?? report.Gpus.FirstOrDefault(g => g.AntiLag.Supported == true)
            ?? report.Gpus[0];
        return new GpuFeatureReading(GpuAvailability.Available, gpu.Name, gpu.Index, gpu.AntiLag.Supported, gpu.AntiLag.Enabled, gpu.AntiLagLevel, gpu.AntiLag.Problem);
    }

    private static bool? AmdAdapterPresent()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\cimv2", "SELECT PNPDeviceID FROM Win32_VideoController");
            using var results = searcher.Get();
            var any = false;
            foreach (var row in results)
            {
                using (row)
                {
                    any |= (row["PNPDeviceID"] as string)?.Contains(AmdPciVendor, StringComparison.OrdinalIgnoreCase) == true;
                }
            }

            return any;
        }
        catch (Exception e) when (e is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }
}
