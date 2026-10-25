using System.Globalization;

namespace Mesura.Core.Windows;

/// <summary>
/// A registry value as read: whether its key and value exist, and the value as text (a DWORD in decimal).
/// <see cref="Unreadable"/> means the read itself failed (access denied, I/O): nothing is known, which is not the same as
/// a missing value (a target that counts a missing value as done must not count a failed read as done).
/// </summary>
public sealed record RegistryReading(bool KeyExists, bool ValueExists, RegistryKind? Kind, string? Value, bool Unreadable = false)
{
    public static readonly RegistryReading Missing = new(false, false, null, null);

    public static readonly RegistryReading Failed = new(false, false, null, null, Unreadable: true);
}

/// <summary>A service as read from its key: whether it exists, and its <c>Start</c> value.</summary>
public sealed record ServiceReading(bool Exists, int? Start)
{
    public static readonly ServiceReading Absent = new(false, null);
}

/// <summary>An optional feature's state. <see cref="Absent"/> means this Windows image doesn't carry it.</summary>
public enum FeatureState
{
    Enabled,
    Disabled,
    Absent,
    Unknown,
}

/// <summary>The active power scheme and every installed scheme, as GUIDs in lower case.</summary>
public sealed record PowerReading(string? ActiveScheme, IReadOnlySet<string> Schemes);

/// <summary>
/// Reads the current Windows state an operation depends on. The platform implementation only reads; tests pass a
/// stand-in. Every method returns "missing" or "unknown" rather than throwing.
/// </summary>
public interface ISystemReader
{
    /// <summary>The Windows build number.</summary>
    int Build { get; }

    /// <summary>Whether this process is elevated, so machine-wide operations can run.</summary>
    bool IsElevated { get; }

    /// <summary>Whose per-user (<c>HKCU</c>) settings are read and written; unresolved blocks per-user operations.</summary>
    UserContext User { get; }

    /// <summary>Whether Windows reports a system battery (a laptop or tablet); <c>null</c> when it can't tell.</summary>
    bool? HasBattery { get; }

    /// <summary>Whether this is a Modern Standby (S0 low power idle) PC; <c>null</c> when it can't tell.</summary>
    bool? ModernStandby { get; }

    /// <summary>Whether this Windows is Pro or higher, so machine policies take effect (not Home); <c>null</c> when it can't tell.</summary>
    bool? PolicyEdition { get; }

    RegistryReading ReadRegistry(string hive, string path, string name);

    ServiceReading ReadService(string name);

    FeatureState ReadFeature(string name);

    /// <summary>A Windows scheduled task's state by its full path: Enabled, Disabled, Absent (not on this PC) or Unknown.</summary>
    FeatureState ReadTask(string path);

    PowerReading ReadPower();

    /// <summary>
    /// A power setting's effective AC value in a scheme (the scheme's own value if set, else the Windows default), or
    /// <c>null</c> when this machine doesn't have the setting.
    /// </summary>
    uint? ReadPowerSetting(string scheme, string subgroup, string setting);

    /// <summary>
    /// A global TCP/IP setting by its netsh name (<see cref="TcpGlobalSetting"/>), in netsh's lower-case words
    /// (<c>normal</c>, <c>enabled</c>), or <c>null</c> when it couldn't be read. For <c>autotuninglevel</c> every TCP
    /// setting template that reports a level is read, and the value is <c>normal</c> only when all of them are.
    /// </summary>
    string? ReadTcpGlobal(string name) => null;
}

/// <summary>What an operation's settings look like on this machine right now.</summary>
public enum OperationState
{
    /// <summary>Every setting already has its target value: applying would change nothing.</summary>
    Applied,

    NotApplied,

    /// <summary>Some settings have the target value and some don't.</summary>
    Partial,

    /// <summary>Not for this machine: outside its builds, or every component it changes is absent (e.g. removed).</summary>
    Inapplicable,

    /// <summary>The state couldn't be read.</summary>
    Unknown,

    /// <summary>Per-user settings, but the user they belong to couldn't be established: nothing is read or changed.</summary>
    Blocked,

    /// <summary>A procedure the user carries out by hand.</summary>
    Manual,

    /// <summary>Shown for information only; the change is made in Windows' own settings.</summary>
    Observation,
}

/// <summary>One setting of an operation as found: its current value, the target, and whether the target is met.</summary>
public sealed record ItemState(string Target, string Current, string Desired, bool Matches, bool Present);

/// <summary>An operation evaluated against the current state.</summary>
public sealed record OperationEvaluation(WindowsOperation Operation, OperationState State, string Reason, IReadOnlyList<ItemState> Items);

/// <summary>Evaluates catalog operations against the current state. Pure: it reads only through <see cref="ISystemReader"/>.</summary>
public static class WindowsEvaluator
{
    public const string NotSet = "(not set)";

    public static OperationEvaluation Evaluate(WindowsOperation op, ISystemReader reader)
    {
        ArgumentNullException.ThrowIfNull(op);
        ArgumentNullException.ThrowIfNull(reader);

        if (op.MinBuild is { } min && reader.Build < min)
        {
            return new(op, OperationState.Inapplicable, $"Needs Windows build {min} or later (this is {reader.Build}).", []);
        }

        if (op.MaxBuild is { } max && reader.Build > max)
        {
            return new(op, OperationState.Inapplicable, $"Applies up to Windows build {max} (this is {reader.Build}).", []);
        }

        if (op.Manual is not null)
        {
            return new(op, OperationState.Manual, "A procedure carried out by hand.", []);
        }

        foreach (var requirement in op.Requirements)
        {
            var (holds, unmet, unread) = requirement switch
            {
                DeviceRequirement.NoBattery => (reader.HasBattery is null ? (bool?)null : !reader.HasBattery.Value,
                    "Meant for PCs without a battery (desktops); this PC has one, so it would cost battery life.",
                    "Couldn't tell whether this PC has a battery, so it is left alone."),
                DeviceRequirement.ProEdition => (reader.PolicyEdition,
                    "This is a Windows Home edition, which doesn't apply this machine policy; the value would be written and ignored.",
                    "Couldn't tell which Windows edition this is, so this machine policy is left alone."),
                _ => (reader.ModernStandby is null ? (bool?)null : !reader.ModernStandby.Value,
                    "This is a Modern Standby PC: Windows offers only the Balanced plan here; use Settings > System > Power > Power mode instead.",
                    "Couldn't tell whether this is a Modern Standby PC, so it is left alone."),
            };
            if (holds is null)
            {
                return new(op, OperationState.Unknown, unread, []);
            }

            if (!holds.Value)
            {
                return new(op, OperationState.Inapplicable, unmet, []);
            }
        }

        if (op.PerUser && !reader.User.Resolved)
        {
            return new(op, OperationState.Blocked, $"Blocked: {reader.User.Reason} Per-user settings are never written to another account.", []);
        }

        if (op.Observation is { } observation)
        {
            return Observe(op, observation, reader);
        }

        var items = new List<ItemState>();
        var unknown = false;
        foreach (var setting in op.Registry)
        {
            var reading = reader.ReadRegistry(setting.Hive, setting.Path, setting.Name);
            unknown |= reading.Unreadable;
            items.Add(RegistryItem(setting, reading));
        }

        items.AddRange(op.Services.Select(s => ServiceItem(s, reader.ReadService(s.Name))));

        foreach (var feature in op.Features)
        {
            var state = reader.ReadFeature(feature.Name);
            unknown |= state == FeatureState.Unknown;
            items.Add(new ItemState(feature.Target, state.ToString(), feature.Enabled ? "Enabled" : "Disabled",
                state == (feature.Enabled ? FeatureState.Enabled : FeatureState.Disabled), state is not FeatureState.Absent));
        }

        foreach (var task in op.Tasks)
        {
            var state = reader.ReadTask(task.Path);
            unknown |= state == FeatureState.Unknown;
            items.Add(new ItemState(task.Target, state.ToString(), task.Enabled ? "Enabled" : "Disabled",
                state == (task.Enabled ? FeatureState.Enabled : FeatureState.Disabled), state is not FeatureState.Absent));
        }

        foreach (var tcp in op.TcpSettings)
        {
            var current = reader.ReadTcpGlobal(tcp.Name);
            unknown |= current is null;
            items.Add(new ItemState(tcp.Target, current ?? "unknown", tcp.Value,
                string.Equals(current, tcp.Value, StringComparison.OrdinalIgnoreCase), true));
        }

        if (op.PowerSettings.Count > 0)
        {
            var active = reader.ReadPower().ActiveScheme;
            unknown |= active is null;
            foreach (var setting in op.PowerSettings)
            {
                var current = active is null ? null : reader.ReadPowerSetting(active, setting.Subgroup, setting.Setting);
                items.Add(current is null
                    ? new ItemState(setting.Target, "absent", setting.AcValue.ToString(CultureInfo.InvariantCulture), false, active is null)
                    : new ItemState(setting.Target, current.Value.ToString(CultureInfo.InvariantCulture),
                        setting.AcValue.ToString(CultureInfo.InvariantCulture), current.Value == setting.AcValue, true));
            }
        }

        if (op.PowerPlan is { } plan)
        {
            var power = reader.ReadPower();
            var installed = power.Schemes.Contains(plan.SchemeGuid.ToLowerInvariant());
            if (!installed && !plan.CreateIfMissing)
            {
                return new(op, OperationState.Inapplicable, $"The {plan.Name} scheme isn't installed on this PC.", []);
            }

            unknown |= power.ActiveScheme is null;
            items.Add(new ItemState(plan.Target, power.ActiveScheme ?? "unknown", plan.SchemeGuid.ToLowerInvariant(),
                string.Equals(power.ActiveScheme, plan.SchemeGuid, StringComparison.OrdinalIgnoreCase), true));
        }

        if (unknown)
        {
            return new(op, OperationState.Unknown, "Part of the current state couldn't be read.", items);
        }

        var present = items.Where(i => i.Present).ToList();
        if (present.Count == 0)
        {
            return new(op, OperationState.Inapplicable, "Nothing it changes is present on this system (already removed).", items);
        }

        var absentNote = present.Count < items.Count
            ? $" {items.Count - present.Count} of its components aren't present and are left out."
            : "";
        if (present.All(i => i.Matches))
        {
            return new(op, OperationState.Applied, "Already in the target state; nothing to do." + absentNote, items);
        }

        return present.Any(i => i.Matches)
            ? new(op, OperationState.Partial, "Partly in the target state." + absentNote, items)
            : new(op, OperationState.NotApplied, "Not applied." + absentNote, items);
    }

    /// <summary>Whether a registry reading equals a target value (a DWORD compared as a number).</summary>
    public static bool Matches(RegistryReading reading, RegistryKind kind, string value)
    {
        ArgumentNullException.ThrowIfNull(reading);
        if (!reading.ValueExists || reading.Value is null || reading.Kind != kind)
        {
            return false; // a value of another type isn't the target, even when its text is the same
        }

        return kind == RegistryKind.DWord
            ? uint.TryParse(reading.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var a)
              && uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var b) && a == b
            : string.Equals(reading.Value, value, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether a registry setting is already in its target state: the target value, or no value at all when the target
    /// is Windows' default (<see cref="RegistrySetting.AbsentMatches"/>).
    /// </summary>
    public static bool IsMet(RegistrySetting setting, RegistryReading reading)
    {
        ArgumentNullException.ThrowIfNull(setting);
        ArgumentNullException.ThrowIfNull(reading);
        if (reading.Unreadable)
        {
            return false;
        }

        if (setting.ClearBits != 0)
        {
            return reading.ValueExists ? BitField(reading) is { } bits && (bits & setting.ClearBits) == 0 : setting.AbsentMatches;
        }

        if (setting.SetBits != 0)
        {
            return reading.ValueExists ? BitField(reading) is { } set && (set & setting.SetBits) == setting.SetBits : setting.AbsentMatches;
        }

        return Matches(reading, setting.Kind, setting.Value) || (setting.AbsentMatches && !reading.ValueExists);
    }

    /// <summary>A value read as a number (a DWORD, or a decimal string), or <c>null</c> when it isn't one.</summary>
    public static uint? BitField(RegistryReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);
        return reading.ValueExists && reading.Kind is not null
            && uint.TryParse(reading.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var bits) ? bits : null;
    }

    /// <summary>
    /// The value and type a setting writes over a reading: the catalog value, or for a bit field the value as found with
    /// only the setting's bits cleared, in the type it already has. <c>null</c> when a bit field holds something that isn't
    /// a number, which is left alone.
    /// </summary>
    public static (RegistryKind Kind, string Value)? ValueToWrite(RegistrySetting setting, RegistryReading reading)
    {
        ArgumentNullException.ThrowIfNull(setting);
        ArgumentNullException.ThrowIfNull(reading);
        if ((setting.ClearBits == 0 && setting.SetBits == 0) || !reading.ValueExists)
        {
            return (setting.Kind, setting.Value);
        }

        return BitField(reading) is { } bits
            ? (reading.Kind!.Value, ((bits & ~setting.ClearBits) | setting.SetBits).ToString(CultureInfo.InvariantCulture))
            : null;
    }

    private static ItemState RegistryItem(RegistrySetting setting, RegistryReading reading) =>
        new(setting.Target, reading.ValueExists ? reading.Value ?? "" : NotSet,
            setting.ClearBits != 0
                ? $"bit 0x{setting.ClearBits:X} cleared, other bits kept ({(reading.ValueExists ? ValueToWrite(setting, reading)?.Value ?? "not a number" : setting.Value)})"
                : setting.SetBits != 0
                    ? $"bit 0x{setting.SetBits:X} set, other bits kept ({(reading.ValueExists ? ValueToWrite(setting, reading)?.Value ?? "not a number" : setting.Value)})"
                    : setting.AbsentMatches ? $"{setting.Value} or {NotSet} (Windows' default)" : setting.Value,
            IsMet(setting, reading), true);

    private static ItemState ServiceItem(ServiceSetting setting, ServiceReading reading) =>
        reading.Exists
            ? new(setting.Target, StartName(reading.Start), setting.Start.ToString(), reading.Start == (int)setting.Start, true)
            : new(setting.Target, "absent", setting.Start.ToString(), false, false);

    public static string StartName(int? start) => start switch
    {
        0 => "Boot",
        1 => "System",
        2 => "Automatic",
        3 => "Manual",
        4 => "Disabled",
        null => "unknown",
        _ => start.Value.ToString(CultureInfo.InvariantCulture),
    };

    private static OperationEvaluation Observe(WindowsOperation op, ObservationSetting observation, ISystemReader reader)
    {
        var items = new List<ItemState>();
        if (observation.Service is { } name)
        {
            var service = reader.ReadService(name);
            items.Add(new ItemState($"service {name}", service.Exists ? StartName(service.Start) : "absent", "", false, service.Exists));
        }

        foreach (var read in observation.Reads)
        {
            var reading = reader.ReadRegistry(read.Hive, read.Path, read.Name);
            items.Add(new ItemState(read.Target, reading.ValueExists ? reading.Value ?? "" : NotSet, "", false, reading.KeyExists));
        }

        return new(op, OperationState.Observation, $"Shown only; change it in Windows ({observation.OpenUri}).", items);
    }
}
