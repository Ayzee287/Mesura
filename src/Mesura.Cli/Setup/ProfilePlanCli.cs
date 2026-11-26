using System.Text.Json;
using System.Text.Json.Serialization;
using Mesura.Core.Hardware;
using Mesura.Core.Profiles;
using Mesura.Core.Windows;
using Mesura.Platform.Windows.Windows;

namespace Mesura.Cli.Setup;

/// <summary>
/// <c>mesura windows plan [--profile &lt;id&gt;] [--group-off a,b] [--entry-off x,y] [--entry-on x,y] [--json]</c>: the profile as planned for this PC (0.6), group by
/// group: what would be applied, what is already set, what waits for a restart, what the plan covers, and what is not for this PC with the reason. Read-only:
/// it asks Windows and changes nothing; applying it is <c>windows apply --profile &lt;id&gt; --apply</c> (the same operations) or the app's Review and apply.
/// </summary>
internal static class ProfilePlanCli
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    /// <summary>
    /// The profile named by <c>--profile</c> (maximum-performance for <c>windows plan</c> when none is given) planned for this PC with the group and entry choices of the
    /// command line, exactly as the app plans it; <c>null</c> (with the reason in <paramref name="error"/>) when there is no such profile.
    /// </summary>
    public static ProfilePlan? Build(string verb, WindowsCatalog catalog, SystemStateReader reader, Dictionary<string, List<string>> options, out HardwareProfile? hardware, out string? error)
    {
        var id = options.TryGetValue("--profile", out var chosen) && chosen.Count > 0 ? chosen[0] : "maximum-performance";
        var profile = OptimizationProfiles.Default.ById(id);
        hardware = null;
        error = null;
        if (profile is null)
        {
            error = $"{verb}: no profile '{id}' (known: {string.Join(", ", OptimizationProfiles.Default.Profiles.Select(p => p.Id))}).";
            return null;
        }

        var choices = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var off in options.GetValueOrDefault("--entry-off") ?? [])
        {
            choices[off] = false;
        }

        foreach (var on in options.GetValueOrDefault("--entry-on") ?? [])
        {
            choices[on] = true;
        }

        hardware = ReadHardware(reader);
        return LevelPlanning.Plan(profile, PlanningContext.Reading(catalog, hardware, reader),
            new ProfileChoices(new HashSet<string>(options.GetValueOrDefault("--group-off") ?? [], StringComparer.OrdinalIgnoreCase), choices));
    }

    /// <summary>
    /// This PC's hardware profile for planning a level, or <c>null</c> when it couldn't be read (the reason goes to the error stream): entries for some
    /// hardware are then left alone, and say so, rather than applied on a guess.
    /// </summary>
    public static HardwareProfile? ReadHardware(SystemStateReader reader)
    {
        try
        {
            return HardwareProfileReader.Read(reader, "cli");
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException
            or System.Management.ManagementException)
        {
            Console.Error.WriteLine($"The hardware profile couldn't be read ({e.Message}); settings that are only for some hardware are left alone.");
            return null;
        }
    }

    /// <summary>The entries a plan leaves out on this PC, each with its reason in English (the <c>windows plan</c> words), under a heading; nothing when there are none.</summary>
    public static void WriteNotHere(IReadOnlyList<PlanEntry> notHere, string heading)
    {
        if (notHere.Count == 0)
        {
            return;
        }

        Console.WriteLine(heading);
        foreach (var entry in notHere)
        {
            Console.WriteLine($"  - {entry.OperationId,-42} {entry.State}  <- {entry.Reason}: {entry.Detail}");
        }
    }

    public static int Run(WindowsCatalog catalog, SystemStateReader reader, Dictionary<string, List<string>> options)
    {
        if (Build("windows plan", catalog, reader, options, out var hardware, out var error) is not { } plan)
        {
            Console.Error.WriteLine(error);
            return 2;
        }

        var profile = plan.Profile;

        if (options.ContainsKey("--json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                profile = profile.Id,
                operations = plan.OperationIds,
                groups = plan.Groups.Select(g => new
                {
                    id = g.Id,
                    enabled = g.Enabled,
                    entries = g.Entries.Select(e => new { operation = e.OperationId, state = e.State, reason = e.Reason, detail = e.State is EntryState.ToApply or EntryState.Applied ? null : e.Detail, included = e.Included, role = e.Entry.Role, sideEffects = e.Entry.SideEffects }),
                }),
            }, Json));
            return 0;
        }

        Console.WriteLine(hardware is null
            ? $"Profile '{profile.Id}' planned for this PC (hardware unread); nothing was changed."
            : $"Profile '{profile.Id}' planned for this PC ({hardware.Form}, {hardware.Cpu?.Name ?? "processor unread"}); nothing was changed.");
        foreach (var group in plan.Groups)
        {
            Console.WriteLine();
            Console.WriteLine($"{group.Group.Name}{(group.Enabled ? "" : "  [switched off]")}: {group.Included} to apply, {group.Applied} applied, {group.Covered} covered by the plan, {group.NotHere} not on this PC");
            foreach (var entry in group.Entries)
            {
                var mark = entry.Included ? "+" : entry.State == EntryState.ToApply ? "o" : " ";
                var reason = entry.Reason is { } r ? $"  <- {r}: {entry.Detail}" : "";
                Console.WriteLine($"  {mark} {entry.OperationId,-42} {entry.State}{(entry.Entry.SideEffects ? "  [has a cost]" : "")}{(entry.Entry.Role == EntryRole.Optional ? "  [optional]" : "")}{reason}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"{plan.OperationIds.Count} operation(s) would be applied, in this order: {string.Join(", ", plan.OperationIds)}");
        return 0;
    }
}
