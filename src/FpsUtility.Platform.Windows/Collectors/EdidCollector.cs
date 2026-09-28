using FpsUtility.Core.Privacy;
using FpsUtility.Core.Raw;
using Microsoft.Win32;
using Windows.Win32;
using Windows.Win32.Devices.DeviceAndDriverInstallation;
using Windows.Win32.System.Registry;

namespace FpsUtility.Platform.Windows.Collectors;

/// <summary>
/// <c>edid</c>: the stored EDID of each active output's monitor, keyed by the monitor device path
/// DisplayConfig reports (architecture §4.4, §7). The registry is never read by a hard-coded
/// <c>Enum</c> path: the monitor's device node is located from that path and its hardware key is
/// opened with <c>CM_Open_DevNode_Key</c>, read-only. Stale EDIDs of disconnected monitors are
/// therefore never read. Serial fields are zeroed before anything leaves this class (§13).
/// </summary>
internal static class EdidCollector
{
    public const string Source = "win32.QueryDisplayConfig, win32.CM_Open_DevNode_Key (CM_REGISTRY_HARDWARE) value EDID";

    public static EdidData Collect()
    {
        var (paths, _) = DisplayConfig.QueryActivePaths();
        var monitors = new Dictionary<string, MonitorEdid>(StringComparer.Ordinal);

        foreach (var path in paths)
        {
            var (_, devicePath) = DisplayConfig.GetTargetName(path.targetInfo);
            if (!devicePath.IsKnown || monitors.ContainsKey(devicePath.Value!))
            {
                continue; // no path to key it by (the display section records why), or already read for a cloned output
            }

            monitors.Add(devicePath.Value!, new MonitorEdid(devicePath.Value!, ReadEdid(devicePath.Value!)));
        }

        return new EdidData([.. monitors.Values]);
    }

    private static Reading<EdidPayload> ReadEdid(string monitorDevicePath)
    {
        var instanceId = DeviceNodes.GetInterfaceInstanceId(monitorDevicePath);
        if (!instanceId.IsKnown)
        {
            return Reading.Missing<EdidPayload>(instanceId.Missing!.Value, instanceId.Detail!);
        }

        var located = DeviceNodes.Locate(instanceId.Value!, out var node);
        if (located != CONFIGRET.CR_SUCCESS)
        {
            return Reading.Missing<EdidPayload>(Win32Errors.Reason(located), Win32Errors.Describe("CM_Locate_DevNodeW(monitor)", located));
        }

        var opened = PInvoke.CM_Open_DevNode_Key(
            node,
            (uint)REG_SAM_FLAGS.KEY_READ,
            0,
            PInvoke.RegDisposition_OpenExisting,
            out var handle,
            PInvoke.CM_REGISTRY_HARDWARE);

        if (opened != CONFIGRET.CR_SUCCESS)
        {
            handle.Dispose();
            return Reading.Missing<EdidPayload>(Win32Errors.Reason(opened), Win32Errors.Describe("CM_Open_DevNode_Key(CM_REGISTRY_HARDWARE)", opened));
        }

        using var key = RegistryKey.FromHandle(handle);

        if (key.GetValue("EDID", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not byte[] stored)
        {
            return Reading.Missing<EdidPayload>(MissingReason.NotPresent, "The monitor's hardware key has no binary EDID value");
        }

        EdidScrubber.Result scrubbed;
        try
        {
            scrubbed = EdidScrubber.Scrub(stored);
        }
        catch (FormatException ex)
        {
            // Not stored: its serial fields couldn't be located, so it can't be scrubbed.
            return Reading.Missing<EdidPayload>(MissingReason.ParseFailed, $"The stored EDID was not kept: {ex.Message}");
        }

        return Reading.Known(new EdidPayload(
            scrubbed.Bytes,
            scrubbed.ChecksumValidBeforeScrub,
            scrubbed.FieldsScrubbed,
            ReadOverridePresent(key)));
    }

    /// <summary>An INF EDID override lives in the <c>EDID_OVERRIDE</c> subkey of the same hardware key.</summary>
    private static Reading<bool> ReadOverridePresent(RegistryKey hardwareKey)
    {
        try
        {
            using var overrideKey = hardwareKey.OpenSubKey("EDID_OVERRIDE");
            return Reading.Known(overrideKey is not null);
        }
        catch (System.Security.SecurityException ex)
        {
            return Reading.Missing<bool>(MissingReason.AccessDenied, $"EDID_OVERRIDE: {ex.GetType().Name}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return Reading.Missing<bool>(MissingReason.AccessDenied, $"EDID_OVERRIDE: {ex.GetType().Name}");
        }
    }
}
