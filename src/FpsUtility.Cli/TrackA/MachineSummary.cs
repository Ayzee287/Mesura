using System.Globalization;
using FpsUtility.Core.Facts;
using FpsUtility.Core.Raw;

namespace FpsUtility.Cli.TrackA;

/// <summary>
/// What the scan read, shown to the operator before any question, so nobody types a machine fact (spec §10, §34).
/// Display only: it is never written, and the normalised facts stay the record.
/// </summary>
internal static class MachineSummary
{
    public static IEnumerable<string> Lines(NormalizedSnapshot facts, RawSnapshot scrubbed)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(scrubbed);

        yield return "This PC, as the scan read it (you won't be asked any of this):";
        yield return $"  Windows    build {Show(facts.Os.Build)}.{Show(facts.Os.UpdateRevision)} {Show(facts.Os.Edition)} {Show(facts.Os.Version)}, session {Show(facts.Os.Session)}";
        yield return $"  Machine    {Show(facts.Machine.SystemManufacturer)} {Show(facts.Machine.SystemProduct)}, board {Show(facts.Machine.BoardProduct)}, firmware chassis {Show(facts.Machine.ChassisType)}";

        foreach (var cpu in (facts.Processors.Value ?? []).Where(cpu => cpu.Populated.Value))
        {
            yield return $"  Processor  {Show(cpu.Version)}";
        }

        foreach (var gpu in facts.Gpus.Value ?? [])
        {
            var kind = gpu.IsHardware.Value is false ? "software" : gpu.IsIntegrated.Value is true ? "integrated" : gpu.IsIntegrated.IsKnown ? "discrete" : "kind unknown";
            yield return $"  GPU        {gpu.Id}: {Show(gpu.Name)} ({kind})";
        }

        foreach (var display in facts.Displays.Value ?? [])
        {
            var modes = display.OfferedModes.Value?.Where(mode => !mode.Interlaced).ToList();
            var highest = modes is { Count: > 0 } ? modes.Max(mode => mode.RefreshHz).ToString(CultureInfo.InvariantCulture) + " Hz" : "unknown";
            var current = display.SignalRefresh.Value is { } rate ? ((decimal)rate.Numerator / rate.Denominator).ToString("0.##", CultureInfo.InvariantCulture) + " Hz" : "unknown";
            var resolution = display.DesktopResolution.Value is { } size ? string.Create(CultureInfo.InvariantCulture, $"{size.Width}x{size.Height}") : "unknown";
            var shared = display.SharesSourceWith.Value is { Count: > 0 } others ? $", duplicates {string.Join(", ", others)}" : "";
            yield return $"  Display    {display.Id}: {Show(display.Connection)} on {Show(display.Gpu)}, {resolution} at {current} (highest offered {highest}){shared}";
        }

        foreach (var disk in facts.Disks.Value ?? [])
        {
            var size = disk.SizeBytes.Value is { } bytes ? (bytes / 1_000_000_000m).ToString("0", CultureInfo.InvariantCulture) + " GB" : "size unknown";
            yield return $"  Disk       {disk.Id}: {Show(disk.Model)}, {Show(disk.MediaType)} on {Show(disk.BusType)}, {size}";
        }

        yield return facts.Steam.Registration.IsKnown
            ? facts.Steam.Registration.Value == SteamRegistration.Registered
                ? $"  Steam      registered: {facts.Steam.Libraries.Value?.Count.ToString(CultureInfo.InvariantCulture) ?? "?"} libraries, {facts.Steam.Libraries.Value?.Sum(library => library.Apps.Value?.Count ?? 0).ToString(CultureInfo.InvariantCulture) ?? "?"} installed apps"
                : "  Steam      not registered for this user"
            : "  Steam      couldn't be read";
        yield return $"  Power      battery {Show(facts.Power.BatteryPresent)}, on AC {Show(facts.Power.OnAcPower)}";

        var unavailable = Unavailable(scrubbed).ToList();
        yield return unavailable.Count == 0 ? "  Sources    every source was read" : $"  Sources    unavailable: {string.Join(", ", unavailable)}";
    }

    public static IEnumerable<string> Unavailable(RawSnapshot snapshot)
    {
        var sources = snapshot.Sources;
        (string Name, SourceStatus Status, MissingReason? Reason)[] entries =
        [
            ("display", sources.Display.Status, sources.Display.Reason),
            ("edid", sources.Edid.Status, sources.Edid.Reason),
            ("adapters", sources.Adapters.Status, sources.Adapters.Reason),
            ("smbios", sources.Smbios.Status, sources.Smbios.Reason),
            ("pcie", sources.Pcie.Status, sources.Pcie.Reason),
            ("storage", sources.Storage.Status, sources.Storage.Reason),
            ("steam", sources.Steam.Status, sources.Steam.Reason),
            ("power", sources.Power.Status, sources.Power.Reason),
        ];

        return entries.Where(entry => entry.Status != SourceStatus.Ok).Select(entry => $"{entry.Name} ({(entry.Reason is { } reason ? System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(reason.ToString()) : "?")})");
    }

    private static string Show<T>(Fact<T> fact) => !fact.IsKnown
        ? "unknown"
        : fact.Value switch
        {
            bool value => value ? "yes" : "no",
            Enum value => System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(value.ToString()),
            var value => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
        };
}
