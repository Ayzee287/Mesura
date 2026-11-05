using System.Globalization;
using Mesura.Core.Windows;
using Mesura.Platform.Windows.Windows;
using Mesura.Setup.Windows;
using Mesura.Setup.Windows.Windows;

namespace Mesura.Cli.Setup;

/// <summary>
/// <c>mesura network</c> (0.6): the adapters Mesura can tune, a full snapshot of their settings, a comparison of the present state with a snapshot, and a restore. Everything except
/// <c>restore --apply</c> only reads. A restore writes through the same Windows cmdlets as every adapter change, only where the driver that is there now still lists the snapshot's value,
/// verifies each write by reading the driver, and first saves a snapshot of the present state so that the restore itself can be taken back.
/// </summary>
internal static class NetworkCli
{
    public const string Usage = """
        mesura network [inventory] [--json]
          mesura network snapshot [--name <words>]
          mesura network snapshots
          mesura network diff <snapshot id | file | latest>
          mesura network restore <snapshot id | file | latest> [--apply]
        """;

    public static int Run(string[] args)
    {
        var bare = args.Length == 0 || args[0].StartsWith("--", StringComparison.Ordinal);   // "mesura network" and "mesura network --json" mean the inventory
        var verb = bare ? "inventory" : args[0].ToLowerInvariant();
        var rest = bare ? args : args[1..];
        return verb switch
        {
            "inventory" => Inventory(rest),
            "snapshot" => Snapshot(rest),
            "snapshots" => Snapshots(),
            "diff" => Diff(rest, apply: false, restore: false),
            "restore" => Diff(rest, apply: rest.Contains("--apply", StringComparer.OrdinalIgnoreCase), restore: true),
            _ => Bad($"network: unknown verb '{verb}'."),
        };
    }

    private static int Bad(string message)
    {
        Console.Error.WriteLine(message);
        Console.Error.WriteLine($"Usage: {Usage}");
        return 2;
    }

    private static IReadOnlyList<AdapterReading>? ReadOrComplain(SystemStateReader reader)
    {
        var adapters = reader.ReadAdapters();
        if (adapters is null)
        {
            Console.Error.WriteLine("The network adapters could not be read (Windows' network management classes did not answer).");
        }

        return adapters;
    }

    // ---- read only -------------------------------------------------------------------------------------------------------------------------------------------

    private static int Inventory(string[] args)
    {
        var json = args.Contains("--json", StringComparer.OrdinalIgnoreCase);
        if (args.Any(a => !a.Equals("--json", StringComparison.OrdinalIgnoreCase)))
        {
            return Bad("network inventory: only --json is accepted.");
        }

        var reader = new SystemStateReader(new SystemTools());
        if (ReadOrComplain(reader) is not { } adapters)
        {
            return 1;
        }

        var snapshot = NetworkSnapshot.From(adapters, DateTimeOffset.UtcNow, "inventory");
        if (json)
        {
            Console.WriteLine(snapshot.ToJson());
            return 0;
        }

        Console.WriteLine($"Network adapters Mesura can tune (physical Ethernet and Wi-Fi); read only, nothing was changed. Elevated: {(reader.IsElevated ? "yes" : "no")}.");
        if (snapshot.Adapters.Count == 0)
        {
            Console.WriteLine("  None found.");
        }

        foreach (var a in snapshot.Adapters)
        {
            Console.WriteLine();
            Console.WriteLine($"  {a.Label}");
            Console.WriteLine($"    {a.Kind}, {(string.IsNullOrWhiteSpace(a.Vendor) ? "maker unknown" : a.Vendor)}, driver {a.DriverVersion} ({a.DriverDate}), {(a.Up ? "up" : "down")}, GUID {a.InterfaceGuid}");
            Console.WriteLine($"    Power: allow the computer to turn it off = {(a.AllowTurnOff is { } p ? (p ? "yes" : "no") : "driver does not say")}");
            Console.WriteLine($"    Bindings ({a.Bindings.Count}): {string.Join(", ", a.Bindings.OrderBy(b => b.Key, StringComparer.OrdinalIgnoreCase).Select(b => $"{b.Key}={(b.Value ? "on" : "off")}"))}");
            Console.WriteLine($"    Advanced properties ({a.Properties.Count}):");
            foreach (var property in a.Properties)
            {
                Console.WriteLine($"      {property.Keyword,-34} = {property.RegistryValue,-8} {property.DisplayValue}  ({property.DisplayName})");
            }
        }

        var others = adapters.Where(a => !NetworkSnapshot.Counts(a)).ToList();
        if (others.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"  Not tuned (virtual, VPN or other; Mesura never reads or writes their settings): {string.Join("; ", others.Select(o => $"{o.Name} [{o.Kind}]"))}");
        }

        return 0;
    }

    private static int Snapshot(string[] args)
    {
        string? name = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--name", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                name = args[++i];
            }
            else
            {
                return Bad($"network snapshot: unexpected '{args[i]}'.");
            }
        }

        var reader = new SystemStateReader(new SystemTools());
        if (ReadOrComplain(reader) is not { } adapters)
        {
            return 1;
        }

        var store = new SetupStore();
        var (id, path) = Save(store, adapters, name ?? "snapshot");
        var snapshot = NetworkSnapshot.FromJson(store.ReadNetworkSnapshot(id) ?? string.Empty);
        Console.WriteLine($"Saved snapshot {id}: {snapshot?.Adapters.Count ?? 0} adapter(s), {snapshot?.Adapters.Sum(a => a.Properties.Count) ?? 0} advanced properties. Nothing was changed.");
        Console.WriteLine($"  {path}");
        Console.WriteLine($"  Compare: mesura network diff {id}    Put back: mesura network restore {id}");
        return 0;
    }

    private static (string Id, string Path) Save(SetupStore store, IReadOnlyList<AdapterReading> adapters, string words)
    {
        var now = DateTimeOffset.UtcNow;
        var clean = new string([.. words.Select(c => char.IsLetterOrDigit(c) ? c : '-')]).Trim('-');
        var id = $"{now:yyyyMMdd'T'HHmmss'Z'}-{(clean.Length == 0 ? "snapshot" : clean)}";
        var path = store.WriteNetworkSnapshot(id, NetworkSnapshot.From(adapters, now, words).ToJson());
        return (id, path);
    }

    private static int Snapshots()
    {
        var store = new SetupStore();
        var ids = store.ListNetworkSnapshots();
        if (ids.Count == 0)
        {
            Console.WriteLine($"No network snapshots yet ({store.NetworkFolder}). Take one with: mesura network snapshot");
            return 0;
        }

        foreach (var id in ids)
        {
            var snapshot = NetworkSnapshot.FromJson(store.ReadNetworkSnapshot(id) ?? string.Empty);
            Console.WriteLine(snapshot is null ? $"{id}  (unreadable)" : $"{id}  {snapshot.Adapters.Count} adapter(s)  {string.Join("; ", snapshot.Adapters.Select(a => a.Name))}");
        }

        return 0;
    }

    // ---- compare and restore -----------------------------------------------------------------------------------------------------------------------------------

    private static NetworkSnapshot? Load(SetupStore store, string what, out string label)
    {
        label = what;
        if (what.Equals("latest", StringComparison.OrdinalIgnoreCase))
        {
            what = store.ListNetworkSnapshots().FirstOrDefault() ?? string.Empty;
            label = what.Length == 0 ? "latest" : what;
        }

        var text = what.Length == 0 ? null : File.Exists(what) ? File.ReadAllText(what) : store.ReadNetworkSnapshot(what);
        return text is null ? null : NetworkSnapshot.FromJson(text);
    }

    private static int Diff(string[] args, bool apply, bool restore)
    {
        var what = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        if (what is null || args.Any(a => a.StartsWith("--", StringComparison.Ordinal) && !(restore && a.Equals("--apply", StringComparison.OrdinalIgnoreCase))))
        {
            return Bad($"network {(restore ? "restore" : "diff")}: name a snapshot (an id from 'network snapshots', a file, or 'latest').");
        }

        var store = new SetupStore();
        var snapshot = Load(store, what, out var label);
        if (snapshot is null)
        {
            Console.Error.WriteLine($"No readable network snapshot '{label}'.");
            return 1;
        }

        var reader = new SystemStateReader(new SystemTools());
        if (ReadOrComplain(reader) is not { } adapters)
        {
            return 1;
        }

        var plan = NetworkRestorer.Plan(snapshot, adapters);
        Print(plan, label, snapshot);
        if (!restore)
        {
            Console.WriteLine("Read only: nothing was changed.");
            return 0;
        }

        if (plan.Writes == 0)
        {
            Console.WriteLine("Nothing to restore: every setting the driver can take already equals the snapshot.");
            return 0;
        }

        if (!apply)
        {
            Console.WriteLine($"Preview only: nothing was changed. {plan.Writes} setting(s) would be written; each adapter restarts for a few seconds when a property is written. Add --apply to do it.");
            return 0;
        }

        if (!reader.IsElevated)
        {
            Console.Error.WriteLine("Changing adapter settings needs an elevated terminal (run as administrator). Nothing was changed.");
            return 3;
        }

        var (beforeId, _) = Save(store, adapters, "before-restore");
        Console.WriteLine($"Saved the present state as snapshot {beforeId}: 'mesura network restore {beforeId} --apply' takes this restore back.");
        var result = NetworkRestorer.Apply(plan, reader.ReadAdapters, new WindowsWriter(reader.User, reader.Tools as SystemTools));
        foreach (var item in result.Items)
        {
            Console.WriteLine($"  {item.Outcome,-12} {item.Label}: {item.Item.DisplayName} ({item.Item.Key}): {item.Message}");
        }

        var after = reader.ReadAdapters();
        var left = after is null ? -1 : NetworkRestorer.Plan(snapshot, after).Adapters.Sum(a => a.ToWrite.Count());
        Console.WriteLine($"{result.Restored} of {result.Items.Count} restored. {(left < 0 ? "The adapters could not be read again." : $"A fresh reading still differs from the snapshot in {left} setting(s) the driver could take.")}");
        return result.Complete && left == 0 ? 0 : 1;
    }

    private static void Print(NetworkRestorePlan plan, string label, NetworkSnapshot snapshot)
    {
        Console.WriteLine($"Snapshot {label} (taken {snapshot.TakenAt.ToLocalTime():yyyy-MM-dd HH:mm}, {snapshot.Adapters.Count} adapter(s)) compared with the present state:");
        foreach (var adapter in plan.Adapters)
        {
            Console.WriteLine();
            Console.WriteLine($"  {adapter.Label}{(adapter.Present ? string.Empty : "  [not present]")}");
            foreach (var note in adapter.Notes)
            {
                Console.WriteLine($"    note: {note}");
            }

            if (adapter.Present && adapter.Notes.Count == 0 || adapter.Items.Count > 0)
            {
                Console.WriteLine($"    {adapter.Unchanged.ToString(CultureInfo.InvariantCulture)} setting(s) equal the snapshot, {adapter.Items.Count} differ.");
            }

            foreach (var item in adapter.Items)
            {
                var what = item.Kind switch { AdapterSettingKind.Binding => "binding", AdapterSettingKind.PowerManagement => "power", _ => "property" };
                Console.WriteLine(item.Writable
                    ? $"    would write {what} {item.Key}: now '{item.Current}' -> snapshot '{item.Wanted}'  ({item.DisplayName})"
                    : $"    skip {what} {item.Key}: now '{item.Current ?? "-"}', snapshot '{item.Wanted}'. {item.SkipReason}");
            }
        }

        if (plan.NewAdapters.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"  Adapters present now that the snapshot does not have (left alone): {string.Join("; ", plan.NewAdapters)}");
        }

        Console.WriteLine();
        Console.WriteLine($"  Summary: {plan.Writes} would be written, {plan.Skipped} cannot be (shown above).");
    }
}
