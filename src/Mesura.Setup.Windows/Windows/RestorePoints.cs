using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using Mesura.Core.Windows;

namespace Mesura.Setup.Windows.Windows;

/// <summary>
/// System Restore points through Windows' own management class (<c>root\default:SystemRestore</c>): create one before a
/// run changes settings for the whole PC, and list the ones that exist. The setup layer's owner of that one call (it is a
/// write: the method that makes the point), made only when the user's setting asks for it. Windows allows one automatic
/// restore point per 24 hours and quietly skips another; Mesura checks the list afterwards and says so instead of claiming
/// a point it doesn't have.
/// </summary>
public sealed class RestorePoints : IRestorePoints
{
    private const string Scope = @"\\.\root\default";
    private const int ModifySettings = 12; // MODIFY_SETTINGS
    private const int BeginSystemChange = 100; // BEGIN_SYSTEM_CHANGE

    public string? Create(string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        var before = List().Select(p => p.Sequence).DefaultIfEmpty().Max();
        try
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
            if (code != 0)
            {
                return code == 1058 ? "System Protection is turned off for this PC, so Windows can't make a restore point."
                    : $"Windows couldn't make a restore point (code {code}).";
            }
        }
        catch (Exception e) when (e is ManagementException or UnauthorizedAccessException or COMException or InvalidOperationException)
        {
            return $"Windows couldn't make a restore point: {e.Message}";
        }

        return List().Any(p => p.Sequence > before)
            ? null
            : "Windows skipped the restore point (it makes one automatic point per 24 hours); Undo in Activity still covers this run.";
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
}
