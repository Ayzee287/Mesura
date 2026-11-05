namespace Mesura.Core.Windows;

/// <summary>One setting of one adapter that differs from the snapshot, and what a restore would do about it.</summary>
/// <param name="SkipReason">Why it will not be written (<c>null</c> when it will). A setting is written only where the driver that is there now declares it and lists the value: never blind.</param>
public sealed record RestoreItem(AdapterSettingKind Kind, string Key, string DisplayName, string? Current, string Wanted, string? SkipReason)
{
    public bool Writable => SkipReason is null;
}

/// <summary>What a restore would do to one adapter of the snapshot.</summary>
/// <param name="Present">Whether an adapter with that interface GUID exists now (a replaced card has a new GUID).</param>
/// <param name="Notes">Facts to read before trusting the rest: the driver changed, the kind changed, the adapter is down.</param>
/// <param name="Unchanged">How many of the snapshot's settings already equal the snapshot.</param>
public sealed record AdapterRestorePlan(string InterfaceGuid, string Label, bool Present, IReadOnlyList<string> Notes, IReadOnlyList<RestoreItem> Items, int Unchanged)
{
    public IEnumerable<RestoreItem> ToWrite => Items.Where(i => i.Writable);
}

/// <summary>What a restore would do to every adapter of a snapshot, from fresh readings. Nothing is written by building it.</summary>
public sealed record NetworkRestorePlan(IReadOnlyList<AdapterRestorePlan> Adapters, IReadOnlyList<string> NewAdapters)
{
    public int Writes => Adapters.Sum(a => a.ToWrite.Count());

    public int Skipped => Adapters.Sum(a => a.Items.Count(i => !i.Writable));

    public int Differences => Adapters.Sum(a => a.Items.Count);
}

public enum RestoreOutcome
{
    /// <summary>Written, and a fresh reading from the driver shows the snapshot's value.</summary>
    Restored,

    /// <summary>The write was refused, or the driver does not report the value afterwards, or the adapter did not come back.</summary>
    Failed,

    /// <summary>Not tried because an earlier write on the same adapter left it down.</summary>
    NotAttempted,
}

public sealed record RestoreItemResult(string InterfaceGuid, string Label, RestoreItem Item, RestoreOutcome Outcome, string Message);

public sealed record NetworkRestoreResult(IReadOnlyList<RestoreItemResult> Items)
{
    public int Restored => Items.Count(i => i.Outcome == RestoreOutcome.Restored);

    public bool Complete => Items.All(i => i.Outcome == RestoreOutcome.Restored);
}

/// <summary>
/// Puts adapter settings back to a <see cref="NetworkSnapshot"/> (0.6). The plan compares the snapshot with fresh readings, adapter by adapter, matching on the stable interface GUID. A setting
/// is written only when it differs, the adapter is a physical Ethernet or Wi-Fi adapter of the same kind, and the driver that is there now still declares the keyword and lists the snapshot's
/// value (the rule of <see cref="AdapterSetting.Wanted"/>); everything else is shown with its reason. A write goes through the same Windows cmdlets as every adapter change and is verified by
/// reading the adapter again; a driver that does not keep the value, or an adapter that does not come back up, is reported as failed.
/// </summary>
public static class NetworkRestorer
{
    public static NetworkRestorePlan Plan(NetworkSnapshot snapshot, IReadOnlyList<AdapterReading> current)
    {
        var plans = new List<AdapterRestorePlan>();
        foreach (var saved in snapshot.Adapters)
        {
            var now = current.FirstOrDefault(a => string.Equals(a.InterfaceGuid, saved.InterfaceGuid, StringComparison.OrdinalIgnoreCase));
            if (now is null)
            {
                plans.Add(new AdapterRestorePlan(saved.InterfaceGuid, saved.Label, false, ["There is no adapter with that GUID now (the card was replaced, removed or reinstalled); nothing to restore."], [], 0));
                continue;
            }

            var notes = new List<string>();
            if (!NetworkSnapshot.Counts(now) || now.Kind != saved.Kind)
            {
                plans.Add(new AdapterRestorePlan(saved.InterfaceGuid, now.Label, true, [$"The adapter is now {(NetworkSnapshot.Counts(now) ? now.Kind.ToString() : "not a physical Ethernet or Wi-Fi adapter")} (it was {saved.Kind}); Mesura leaves it alone."], [], 0));
                continue;
            }

            if (!string.Equals(saved.DriverVersion, now.DriverVersion, StringComparison.OrdinalIgnoreCase))
            {
                notes.Add($"The driver changed since the snapshot ({saved.DriverVersion} {saved.DriverDate} -> {now.DriverVersion} {now.DriverDate}); values are restored only where this driver lists them.");
            }

            if (!now.Up)
            {
                notes.Add("The adapter is down now; writes are verified by reading the driver, not by the link.");
            }

            var items = new List<RestoreItem>();
            var unchanged = 0;
            foreach (var property in saved.Properties)
            {
                var setting = new AdapterSetting(AdapterSettingKind.Property, property.Keyword, AdapterGoal.RegistryValue, property.RegistryValue, true, [now.Kind], []);
                Compare(setting, now, property.RegistryValue, property.DisplayName, items, ref unchanged);
            }

            foreach (var (component, enabled) in saved.Bindings.OrderBy(b => b.Key, StringComparer.OrdinalIgnoreCase))
            {
                var setting = new AdapterSetting(AdapterSettingKind.Binding, component, AdapterGoal.RegistryValue, string.Empty, enabled, [now.Kind], []);
                Compare(setting, now, enabled.ToString(), component, items, ref unchanged);
            }

            if (saved.AllowTurnOff is { } allow)
            {
                var setting = new AdapterSetting(AdapterSettingKind.PowerManagement, "AllowComputerToTurnOffDevice", AdapterGoal.RegistryValue, string.Empty, allow, [now.Kind], []);
                Compare(setting, now, allow.ToString(), "Allow the computer to turn off this device", items, ref unchanged);
            }

            plans.Add(new AdapterRestorePlan(saved.InterfaceGuid, now.Label, true, notes, items, unchanged));
        }

        var known = snapshot.Adapters.Select(a => a.InterfaceGuid).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = current.Where(a => NetworkSnapshot.Counts(a) && !known.Contains(a.InterfaceGuid)).Select(a => a.Label).ToList();
        return new NetworkRestorePlan(plans, added);
    }

    private static void Compare(AdapterSetting setting, AdapterReading now, string wanted, string displayName, List<RestoreItem> items, ref int unchanged)
    {
        var current = setting.Current(now);
        if (current is not null && string.Equals(current, wanted, StringComparison.OrdinalIgnoreCase))
        {
            unchanged++;
            return;
        }

        string? skip = null;
        if (current is null)
        {
            skip = setting.Kind switch
            {
                AdapterSettingKind.Property => "The driver no longer declares this setting.",
                AdapterSettingKind.Binding => "The adapter no longer has this binding.",
                _ => "The driver does not answer the power-management query.",
            };
        }
        else if (setting.Wanted(now) is null)
        {
            skip = $"The driver does not list the value '{wanted}' now.";
        }

        items.Add(new RestoreItem(setting.Kind, setting.Key, displayName.Length == 0 ? setting.Key : displayName, current, wanted, skip));
    }

    /// <summary>
    /// Writes every writable item of <paramref name="plan"/> and reads the adapter again after each. <paramref name="read"/> must return fresh readings every time it is called.
    /// A failed write does not stop the others, except that an adapter left down stops its own remaining items.
    /// </summary>
    public static NetworkRestoreResult Apply(NetworkRestorePlan plan, Func<IReadOnlyList<AdapterReading>?> read, IWindowsWriter writer)
    {
        var results = new List<RestoreItemResult>();
        foreach (var adapter in plan.Adapters)
        {
            var down = false;
            foreach (var item in adapter.ToWrite)
            {
                if (down)
                {
                    results.Add(new(adapter.InterfaceGuid, adapter.Label, item, RestoreOutcome.NotAttempted, "Not tried: an earlier change left the adapter down."));
                    continue;
                }

                var before = read()?.FirstOrDefault(a => string.Equals(a.InterfaceGuid, adapter.InterfaceGuid, StringComparison.OrdinalIgnoreCase));
                try
                {
                    switch (item.Kind)
                    {
                        case AdapterSettingKind.Binding:
                            writer.SetAdapterBinding(adapter.InterfaceGuid, item.Key, bool.Parse(item.Wanted));
                            break;
                        case AdapterSettingKind.PowerManagement:
                            writer.SetAdapterPower(adapter.InterfaceGuid, bool.Parse(item.Wanted));
                            break;
                        default:
                            writer.SetAdapterProperty(adapter.InterfaceGuid, item.Key, item.Wanted);
                            break;
                    }
                }
                catch (Exception e) when (e is InvalidOperationException or ArgumentException or NotSupportedException)
                {
                    results.Add(new(adapter.InterfaceGuid, adapter.Label, item, RestoreOutcome.Failed, e.Message));
                    continue;
                }

                var after = read()?.FirstOrDefault(a => string.Equals(a.InterfaceGuid, adapter.InterfaceGuid, StringComparison.OrdinalIgnoreCase));
                var setting = new AdapterSetting(item.Kind, item.Key, AdapterGoal.RegistryValue, item.Wanted, false, after is null ? [] : [after.Kind], []);
                var seen = after is null ? null : setting.Current(after);
                if (seen is null || !string.Equals(seen, item.Wanted, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(new(adapter.InterfaceGuid, adapter.Label, item, RestoreOutcome.Failed,
                        $"The driver reports {(seen is null ? "nothing" : $"'{seen}'")} after the write, not '{item.Wanted}' (it may refuse or reset the value)."));
                }
                else if (before is { Up: true } && after is { Up: false })
                {
                    down = true;
                    results.Add(new(adapter.InterfaceGuid, adapter.Label, item, RestoreOutcome.Failed, "The value is back, but the adapter did not come up again."));
                }
                else
                {
                    results.Add(new(adapter.InterfaceGuid, adapter.Label, item, RestoreOutcome.Restored, $"'{item.Current ?? "?"}' -> '{item.Wanted}', read back from the driver."));
                }
            }
        }

        return new NetworkRestoreResult(results);
    }
}
