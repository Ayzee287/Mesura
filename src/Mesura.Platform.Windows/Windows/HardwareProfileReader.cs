using Mesura.Core.Hardware;
using Mesura.Core.Normalize;
using Mesura.Core.Raw;
using Mesura.Core.Windows;
using Windows.Win32;
using Windows.Win32.System.SystemInformation;

namespace Mesura.Platform.Windows.Windows;

/// <summary>
/// Reads the <see cref="HardwareProfile"/> of this PC (read-only, no administrator rights): the diagnostic scan the product already has
/// (firmware tables, adapters, storage, power), normalised into facts, plus what the setup layer's state reader already knows for the operations'
/// requirements (battery, Modern Standby, memory, hybrid cores, edition) and the network adapters. A part that can't be read stays unknown; nothing
/// here throws for a missing source, and nothing is written.
/// </summary>
public static class HardwareProfileReader
{
    private const string ProcessorKey = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";

    public static HardwareProfile Read(SystemStateReader reader, string appVersion = "hardware")
    {
        ArgumentNullException.ThrowIfNull(reader);
        var os = OsInfoReader.Read();
        var snapshot = SnapshotCollector.Collect(os, new AppInfo("Mesura", appVersion, Reading.Missing<string>(MissingReason.NotPresent, "A hardware profile read, not a capture")));
        var facts = Normalizer.Normalize(snapshot);

        return HardwareProfileBuilder.Build(facts, new HardwareExtras
        {
            WindowsBuild = reader.Build,
            PolicyEdition = reader.PolicyEdition,
            HasBattery = reader.HasBattery,
            ModernStandby = reader.ModernStandby,
            MemoryBytes = reader.PhysicalMemoryBytes,
            HybridCpu = reader.HybridCpu,
            Caches = ReadCaches(),
            CpuName = Text(reader, "ProcessorNameString"),
            CpuVendorId = Text(reader, "VendorIdentifier"),
            SystemDriveLetter = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\', ':'),
            Adapters = reader.ReadAdapters(),
        });
    }

    /// <summary>
    /// The processor's caches (0.8): <c>GetLogicalProcessorInformationEx(RelationCache)</c>, parsed by <see cref="CpuTopology.ParseCaches"/>; <c>null</c>
    /// when Windows doesn't answer. Read-only, no administrator rights.
    /// </summary>
    private static unsafe IReadOnlyList<CacheGroup>? ReadCaches()
    {
        try
        {
            uint length = 0;
            PInvoke.GetLogicalProcessorInformationEx(LOGICAL_PROCESSOR_RELATIONSHIP.RelationCache, null, ref length);
            if (length == 0)
            {
                return null;
            }

            var buffer = new byte[length];
            fixed (byte* p = buffer)
            {
                if (!PInvoke.GetLogicalProcessorInformationEx(LOGICAL_PROCESSOR_RELATIONSHIP.RelationCache, new Span<SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX>(p, 1), ref length))
                {
                    return null;
                }
            }

            return CpuTopology.ParseCaches(buffer.AsSpan(0, (int)length));
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    private static string? Text(ISystemReader reader, string name) =>
        reader.ReadRegistry("HKLM", ProcessorKey, name) is { ValueExists: true, Kind: RegistryKind.String, Value: { Length: > 0 } value } ? value : null;
}
