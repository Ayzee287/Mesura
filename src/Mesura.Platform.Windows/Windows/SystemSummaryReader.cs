using Mesura.Core.Nvidia;
using Mesura.Core.Windows;

namespace Mesura.Platform.Windows.Windows;

/// <summary>
/// Reads the <see cref="SystemSummary"/> Optimize shows (read-only): the OS facts the platform check already reads, the
/// power capabilities the system reader already has, the graphics adapters from DXCore, and the presence of the
/// Ghost Spectre toolbox folder (<c>C:\Ghost Toolbox</c>, created by the image's installer; read on a Superlite 25H2
/// install, PC-000). Each part that fails is reported as unknown; nothing here throws.
/// </summary>
public static class SystemSummaryReader
{
    /// <summary>The folder a Ghost Spectre image installs at the root of the system drive.</summary>
    public const string GhostToolboxFolder = "Ghost Toolbox";

    public static SystemSummary Read(SystemStateReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var edition = "";
        var version = "";
        try
        {
            var os = OsInfoReader.Read();
            edition = os.Edition.Value ?? "";
            version = os.Version.Value ?? "";
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Unread: shown as Windows and its build only.
        }

        var gpus = new List<string>();
        var nvidia = new List<string>();
        var adapters = ReadNvidia(gpus);
        nvidia.AddRange(adapters?.Select(a => a.Name) ?? []);

        var systemDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        return new SystemSummary(edition, version, reader.Build, reader.HasBattery, reader.ModernStandby, gpus, nvidia, adapters is not null,
            Directory.Exists(Path.Combine(systemDrive, GhostToolboxFolder)), adapters ?? []);
    }

    /// <summary>
    /// The NVIDIA adapters now, with their device id and driver version (read again while the NVIDIA driver step waits
    /// for the install to finish), or <c>null</c> when the adapters couldn't be read.
    /// </summary>
    public static IReadOnlyList<NvidiaGpu>? ReadNvidia() => ReadNvidia(null);

    private static List<NvidiaGpu>? ReadNvidia(List<string>? allNames)
    {
        try
        {
            var nvidia = new List<NvidiaGpu>();
            foreach (var adapter in Collectors.AdapterCollector.Collect().Adapters)
            {
                if (adapter.IsHardware.Value is false)
                {
                    continue; // the Basic Render Driver and other software adapters
                }

                var name = adapter.Description.Value ?? "";
                allNames?.Add(name);
                var ids = adapter.PciIds.Value is { } pci ? (pci.VendorId, pci.DeviceId)
                    : adapter.HardwareId.Value is { } hw ? (hw.VendorId, hw.DeviceId)
                    : (0, 0);
                if (ids.Item1 == SystemSummary.NvidiaVendorId)
                {
                    nvidia.Add(new NvidiaGpu(name, ids.Item2, adapter.DriverVersion.Value ?? ""));
                }
            }

            return nvidia;
        }
        catch (Exception e) when (e is SourceUnavailableException or System.Runtime.InteropServices.COMException or InvalidCastException)
        {
            return null;
        }
    }
}
