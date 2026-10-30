using System.Globalization;
using System.Text;

namespace Mesura.Core.SystemInfo;

/// <summary>
/// The report as text (Markdown, readable as plain text): what "Copy report" puts on the clipboard and "Save report" writes.
/// English on purpose (a report is read by other people and by support), with values formatted the same way the page does.
/// </summary>
public static class SystemReportText
{
    public const string Unavailable = "unavailable";

    public static string Render(SystemReport report, string appVersion)
    {
        ArgumentNullException.ThrowIfNull(report);
        var text = new StringBuilder();
        text.AppendLine("# System report");
        text.AppendLine(CultureInfo.InvariantCulture, $"Made by Mesura {appVersion} on {report.GeneratedAt.ToUniversalTime():yyyy-MM-dd HH:mm} UTC. Read-only: no serial numbers, network addresses, keys or user names are included.");
        text.AppendLine();

        text.AppendLine("## Computer");
        Line(text, "Model", Join(" ", report.Machine.Manufacturer, report.Machine.Model));
        Line(text, "Type", report.Machine.Kind);
        Line(text, "Motherboard", Join(" ", report.Machine.BoardManufacturer, report.Machine.Board));
        Line(text, "BIOS / UEFI", Join(" ", report.Firmware.Vendor, report.Firmware.Version, report.Firmware.ReleaseDate is { } date ? $"({date})" : null));
        Line(text, "Boot mode", Join(", ", report.Firmware.BootMode, report.Firmware.SecureBoot is { } sb ? (sb ? "Secure Boot on" : "Secure Boot off") : null));

        text.AppendLine().AppendLine("## Windows");
        Line(text, "Version", Join(" ", report.Os.Name, report.Os.Edition is { } ed && report.Os.Name?.Contains(ed, StringComparison.OrdinalIgnoreCase) != true ? ed : null, report.Os.Version));
        Line(text, "Build", report.Os.Build is { } build ? build.ToString(CultureInfo.InvariantCulture) + (report.Os.Revision is { } rev ? "." + rev.ToString(CultureInfo.InvariantCulture) : "") : null);
        Line(text, "Architecture", report.Os.Architecture);
        Line(text, "Installed", report.Os.InstalledOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Line(text, "Uptime", report.Os.Uptime is { } up ? Uptime(up) : null);

        text.AppendLine().AppendLine("## Processor");
        if (report.Processors.Count == 0)
        {
            Line(text, "Processor", null);
        }

        foreach (var cpu in report.Processors)
        {
            Line(text, "Processor", Join(" ", cpu.Name));
            Line(text, "Cores / threads", cpu.Cores is { } cores ? $"{cores} / {cpu.Threads?.ToString(CultureInfo.InvariantCulture) ?? "?"}" : null);
            Line(text, "Maximum clock", cpu.MaxMhz is { } mhz ? Ghz(mhz) : null);
        }

        text.AppendLine().AppendLine("## Memory");
        Line(text, "Installed", Bytes(report.Memory.TotalBytes));
        Line(text, "In use", report.Memory.UsedBytes is { } used && report.Memory.TotalBytes is { } all ? $"{Bytes(used)} ({Percent(used, all)})" : null);
        foreach (var module in report.Memory.Modules)
        {
            Line(text, module.Slot ?? "Module", Join(", ", Bytes(module.SizeBytes), Join(" ", NullIfEmpty(module.Type), module.SpeedMts is { } speed ? $"{speed} MT/s" : null), NullIfEmpty(module.FormFactor), module.Manufacturer));
        }

        text.AppendLine().AppendLine("## Graphics");
        if (report.Gpus.Count == 0)
        {
            Line(text, "Graphics", null);
        }

        foreach (var gpu in report.Gpus)
        {
            Line(text, gpu.Integrated switch { true => "Integrated", false => "Discrete", _ => "Graphics" }, gpu.Name);
            Line(text, "  Driver", gpu.DriverVersion);
            Line(text, "  Video memory", gpu.MemoryBytes is { } vram ? Bytes(vram) : null);
        }

        text.AppendLine().AppendLine("## Storage");
        if (report.Disks.Count == 0)
        {
            Line(text, "Drives", null);
        }

        foreach (var disk in report.Disks)
        {
            Line(text, "Drive", Join(", ", disk.Model, Bytes(disk.SizeBytes, decimalUnits: true), Join(" ", disk.Media, disk.Bus), disk.Health));
        }

        foreach (var volume in report.Volumes)
        {
            Line(text, volume.Letter + ":", volume.SizeBytes is { } size
                ? $"{Bytes(volume.FreeBytes)} free of {Bytes(size)}" + (volume.FileSystem is { } fs ? $" ({fs})" : "")
                : null);
        }

        text.AppendLine().AppendLine("## Displays");
        if (report.Displays.Count == 0)
        {
            Line(text, "Displays", null);
        }

        foreach (var display in report.Displays)
        {
            Line(text, display.Name ?? "Display", Join(", ", display.Width is { } w && display.Height is { } h ? $"{w} x {h}" : null, display.RefreshHz is { } hz ? $"{hz:0.##} Hz" : null, display.MaxRefreshHz is { } max && max > (display.RefreshHz ?? 0) ? $"up to {max:0.##} Hz" : null, display.Connection));
        }

        text.AppendLine().AppendLine("## Power");
        Line(text, "Power source", report.Power.OnAc switch { true => "Plugged in", false => "On battery", _ => null });
        Line(text, "Battery", report.Power.HasBattery switch { true => "Present", false => "None", _ => null });
        Line(text, "Power plan", report.Power.PlanName);
        Line(text, "Modern Standby", report.Power.ModernStandby switch { true => "Supported", false => "Not supported", _ => null });
        return text.ToString();
    }

    public static string Bytes(ulong? bytes, bool decimalUnits = false, string mb = "MB", string gb = "GB", string tb = "TB")
    {
        if (bytes is not { } value)
        {
            return Unavailable;
        }

        var unit = decimalUnits ? 1000d : 1024d;
        var size = value / (unit * unit * unit);
        if (size >= unit)
        {
            return (size / unit).ToString("0.##", CultureInfo.InvariantCulture) + " " + tb;
        }

        if (size < 1)
        {
            return Math.Round(value / (unit * unit)).ToString("0", CultureInfo.InvariantCulture) + " " + mb;
        }

        return size >= 10 ? size.ToString("0", CultureInfo.InvariantCulture) + " " + gb : size.ToString("0.#", CultureInfo.InvariantCulture) + " " + gb;
    }

    public static string Percent(ulong part, ulong whole) => whole == 0 ? "" : Math.Round(part * 100.0 / whole).ToString("0", CultureInfo.InvariantCulture) + "%";

    public static string Ghz(int mhz) => (mhz / 1000.0).ToString("0.0#", CultureInfo.InvariantCulture) + " GHz";

    public static string Uptime(TimeSpan span) =>
        span.TotalDays >= 1 ? $"{(int)span.TotalDays} d {span.Hours} h" : span.TotalHours >= 1 ? $"{(int)span.TotalHours} h {span.Minutes} min" : $"{Math.Max(1, span.Minutes)} min";

    private static void Line(StringBuilder text, string label, string? value) =>
        text.AppendLine(CultureInfo.InvariantCulture, $"- {label}: {(string.IsNullOrWhiteSpace(value) ? Unavailable : value)}");

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static string? Join(string separator, params string?[] parts)
    {
        var present = parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        return present.Count == 0 ? null : string.Join(separator, present);
    }
}
