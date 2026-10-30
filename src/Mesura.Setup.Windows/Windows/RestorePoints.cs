using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using Mesura.Core.Windows;

namespace Mesura.Setup.Windows.Windows;

/// <summary>
/// System Restore points: create one before a run changes settings for the whole PC, and list the ones that exist. The setup layer's owner of
/// that one call (it is a write), made only when the user's setting asks for it. Windows allows one automatic restore point per 24 hours and
/// quietly skips another; Mesura checks the list afterwards and says so instead of claiming a point it doesn't have.
/// <para>
/// The point is asked for through Windows' management class (<c>root\default:SystemRestore</c>); when that provider is missing or fails to load
/// ("Provider load failure", common on stripped Windows images and after a damaged WMI repository) the System Restore API (<c>SRSetRestorePointW</c>,
/// which talks to the service directly) is tried before the answer is "not available". The 0.4.1 change; the API route could not be run on a PC
/// where the provider is broken, so it is reported with its own technical text.
/// </para>
/// </summary>
public sealed class RestorePoints : IRestorePoints
{
    private const string Scope = @"\\.\root\default";
    private const int ModifySettings = 12; // MODIFY_SETTINGS
    private const int BeginSystemChange = 100; // BEGIN_SYSTEM_CHANGE
    private const int EndSystemChange = 101; // END_SYSTEM_CHANGE
    private const uint ErrorServiceDisabled = 1058;
    private const uint ErrorAccessDenied = 5;

    public RestoreResult TryCreate(string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        var before = List().Select(p => p.Sequence).DefaultIfEmpty().Max();
        RestoreResult result;
        try
        {
            result = ThroughWmi(description);
        }
        catch (ManagementException e) when (e.ErrorCode is ManagementStatus.ProviderLoadFailure or ManagementStatus.InvalidClass or ManagementStatus.NotFound or ManagementStatus.ProviderFailure)
        {
            // The provider isn't usable: try the API, and keep what WMI said for the technical text.
            result = ThroughApi(description, $"WMI: {e.Message} (0x{(uint)e.ErrorCode:X8})");
        }
        catch (ManagementException e) when (e.ErrorCode == ManagementStatus.AccessDenied)
        {
            return new RestoreResult(RestoreFailure.AccessDenied, $"{e.Message} (0x{(uint)e.ErrorCode:X8})");
        }
        catch (Exception e) when (e is ManagementException or UnauthorizedAccessException or COMException or InvalidOperationException)
        {
            return new RestoreResult(e is UnauthorizedAccessException ? RestoreFailure.AccessDenied : RestoreFailure.Other, $"{e.GetType().Name}: {e.Message}");
        }

        if (!result.Made)
        {
            return result;
        }

        return List().Any(p => p.Sequence > before) ? result : new RestoreResult(RestoreFailure.Skipped);
    }

    private static RestoreResult ThroughWmi(string description)
    {
        using var system = new ManagementClass(Scope, "SystemRestore", null);
        using var arguments = system.GetMethodParameters("CreateRestorePoint");
        arguments["Description"] = description;
        arguments["RestorePointType"] = ModifySettings;
        arguments["EventType"] = BeginSystemChange;
#pragma warning disable RS0030 // The setup layer's one restore-point call, made only when the user's setting asks for it (class remarks).
        using var result = system.InvokeMethod("CreateRestorePoint", arguments, null);
#pragma warning restore RS0030
        var code = Convert.ToUInt32(result?["ReturnValue"] ?? 0u, CultureInfo.InvariantCulture);
        return code == 0 ? new RestoreResult(RestoreFailure.None) : FromCode(code, "WMI");
    }

    private static RestoreResult FromCode(uint code, string route) => code switch
    {
        ErrorServiceDisabled => new RestoreResult(RestoreFailure.ProtectionOff, $"{route}: {code}"),
        ErrorAccessDenied => new RestoreResult(RestoreFailure.AccessDenied, $"{route}: {code}"),
        _ => new RestoreResult(RestoreFailure.Other, $"{route}: code {code}"),
    };

    private static RestoreResult ThroughApi(string description, string wmiNote)
    {
        try
        {
            var info = new RestorePointInfoW { EventType = BeginSystemChange, RestorePointType = ModifySettings, Description = description.Length > 255 ? description[..255] : description };
            if (!SRSetRestorePointW(ref info, out var status))
            {
                return status.Status == 0
                    ? new RestoreResult(RestoreFailure.Unavailable, wmiNote + "; srclient: the call failed")
                    : FromCode(status.Status, "srclient") with { Technical = wmiNote + "; srclient: code " + status.Status };
            }

            if (status.Status != 0)
            {
                return FromCode(status.Status, "srclient") with { Technical = wmiNote + "; srclient: code " + status.Status };
            }

            var end = new RestorePointInfoW { EventType = EndSystemChange, RestorePointType = ModifySettings, SequenceNumber = status.SequenceNumber, Description = "" };
            _ = SRSetRestorePointW(ref end, out _);
            return new RestoreResult(RestoreFailure.None);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return new RestoreResult(RestoreFailure.Unavailable, wmiNote + $"; srclient.dll: {e.GetType().Name}");
        }
    }

    public IReadOnlyList<RestorePointInfo> List()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(Scope, "SELECT SequenceNumber, Description, CreationTime FROM SystemRestore");
            using var rows = searcher.Get();
            var points = new List<RestorePointInfo>();
            foreach (var row in rows)
            {
                using (row)
                {
                    points.Add(new RestorePointInfo(
                        Convert.ToUInt32(row["SequenceNumber"], CultureInfo.InvariantCulture),
                        row["Description"]?.ToString() ?? "",
                        new DateTimeOffset(ManagementDateTimeConverter.ToDateTime(row["CreationTime"]?.ToString() ?? ""))));
                }
            }

            return [.. points.OrderByDescending(p => p.Sequence)];
        }
        catch (Exception e) when (e is ManagementException or UnauthorizedAccessException or COMException or ArgumentException or FormatException or InvalidCastException)
        {
            return [];
        }
    }

    // srrestoreptapi.h: both structures are packed to one byte.
    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Unicode)]
    private struct RestorePointInfoW
    {
        public uint EventType;
        public uint RestorePointType;
        public long SequenceNumber;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Description;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct StateMgrStatus
    {
        public uint Status;
        public long SequenceNumber;
    }

    [DllImport("srclient.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SRSetRestorePointW(ref RestorePointInfoW restorePtSpec, out StateMgrStatus status);
}
