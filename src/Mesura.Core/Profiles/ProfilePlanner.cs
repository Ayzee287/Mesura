using Mesura.Core.Hardware;
using Mesura.Core.Windows;

namespace Mesura.Core.Profiles;

/// <summary>Where one entry of a profile stands on this PC.</summary>
public enum EntryState
{
    /// <summary>Applicable and not in its target state yet: applying changes something.</summary>
    ToApply,

    /// <summary>Already in its target state.</summary>
    Applied,

    /// <summary>Stored and read back, but waiting for a restart, a sign-out or an Explorer restart before it is in effect.</summary>
    Waiting,

    /// <summary>The profile's own power plan already writes the same value, so applying the entry on its own would change nothing.</summary>
    Covered,

    /// <summary>Not for this PC (its build, edition, battery, memory, processor, graphics or drives), with the reason.</summary>
    Unsupported,

    /// <summary>A fact it depends on couldn't be read, or the setting couldn't be: left alone, with the reason.</summary>
    CantTell,
}

/// <summary>The reasons an entry is not applied, as codes the app words in its own language; the English <see cref="PlanEntry.Detail"/> goes with each.</summary>
public static class PlanReason
{
    public const string NotInCatalog = "not-in-catalog";
    public const string Build = "build";
    public const string Battery = "battery";
    public const string ModernStandby = "modern-standby";
    public const string Edition = "edition";
    public const string Memory = "memory";
    public const string Hybrid = "hybrid";
    public const string EffectUnsupported = "effect-unsupported";
    public const string Absent = "absent";
    public const string Unread = "unread";
    public const string Blocked = "blocked";
    public const string Covered = "covered";
    public const string NotApplicable = "not-applicable";

    /// <summary>The prefix of a hardware reason: <c>hardware.disk.nvme</c>, <c>hardware.gpu.nvidia</c>.</summary>
    public const string HardwarePrefix = "hardware.";
}

/// <summary>
/// One entry of a profile as planned for this PC.
/// </summary>
/// <param name="Operation">The catalog operation, or <c>null</c> when this catalog doesn't have it (an edited copy).</param>
/// <param name="Reason">A <see cref="PlanReason"/> code for an entry that isn't applied here (unsupported, can't tell, covered), otherwise <c>null</c>.</param>
/// <param name="Detail">The same in English, from the engine or the hardware condition.</param>
/// <param name="Waiting">What an applied-but-not-yet-in-effect entry waits for.</param>
/// <param name="Included">Whether the entry would be applied now: it can be, its role is default (or the user ticked it) and its group is on.</param>
/// <param name="CoveredBy">The operation id of the plan that covers it.</param>
public sealed record PlanEntry(
    ProfileEntry Entry, WindowsOperation? Operation, EntryState State, string? Reason, string Detail, Activation? Waiting, bool Included, string? CoveredBy)
{
    public string OperationId => Entry.OperationId;

    /// <summary>Whether the user can tick it: it is applicable and would change something.</summary>
    public bool Selectable => State == EntryState.ToApply;
}

/// <summary>A group of a plan: its entries and whether the user has it switched on.</summary>
public sealed record PlanGroup(ProfileGroup Group, IReadOnlyList<PlanEntry> Entries, bool Enabled)
{
    public string Id => Group.Id;

    /// <summary>Entries that would be applied now.</summary>
    public int Included => Entries.Count(e => e.Included);

    /// <summary>Entries that could be applied (ticked or not).</summary>
    public int Available => Entries.Count(e => e.Selectable);

    public int Applied => Entries.Count(e => e.State is EntryState.Applied or EntryState.Waiting);

    public int Waiting => Entries.Count(e => e.State == EntryState.Waiting);

    public int Covered => Entries.Count(e => e.State == EntryState.Covered);

    public int NotHere => Entries.Count(e => e.State is EntryState.Unsupported or EntryState.CantTell);

    /// <summary>Whether the group has anything to show on this PC: something to apply, applied, waiting or covered. A group whose every entry is unsupported is only counted.</summary>
    public bool Visible => Entries.Any(e => e.State is not (EntryState.Unsupported or EntryState.CantTell));
}

/// <summary>
/// What the user chose over the profile's defaults. A group that is off applies none of its entries; an entry choice overrides its default
/// (ticked for an optional entry, unticked for a default one). Neither is stored by the planner.
/// </summary>
public sealed record ProfileChoices(IReadOnlySet<string> GroupsOff, IReadOnlyDictionary<string, bool> Entries)
{
    public static readonly ProfileChoices None = new(new HashSet<string>(), new Dictionary<string, bool>());
}

/// <summary>The profile planned for this PC: every group with every entry's state, and the operations that would be applied.</summary>
public sealed record ProfilePlan(OptimizationProfile Profile, IReadOnlyList<PlanGroup> Groups)
{
    public IEnumerable<PlanEntry> Entries => Groups.SelectMany(g => g.Entries);

    /// <summary>The operations that would be applied, power plans first, then group by group in the profile's order.</summary>
    public IReadOnlyList<string> OperationIds =>
        [.. Entries.Where(e => e.Included).OrderBy(e => e.Operation?.PowerPlan is null ? 1 : 0).Select(e => e.OperationId)];

    public PlanEntry? Entry(string operationId) =>
        Entries.FirstOrDefault(e => e.OperationId.Equals(operationId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Entries that aren't for this PC or couldn't be read, with their reasons.</summary>
    public IReadOnlyList<PlanEntry> NotHere => [.. Entries.Where(e => e.State is EntryState.Unsupported or EntryState.CantTell)];
}

/// <summary>
/// Plans a profile for one PC (0.6): for each entry whether it applies here, is already done, waits for a restart, is covered by the profile's own
/// power plan, is not for this hardware, or can't be told; then what the user's choices make of it. Pure: the readings come in as arguments (the
/// operations' evaluations the app already holds, the hardware profile, the system reader for the reason words), and nothing is changed.
/// </summary>
public static class ProfilePlanner
{
    /// <param name="hardware">The hardware profile, or <c>null</c> before it was read (entries that depend on hardware are then "can't tell").</param>
    /// <param name="evaluate">An operation's evaluation on this PC now, or <c>null</c> when it hasn't been read.</param>
    /// <param name="waiting">What each applied operation still waits for (<see cref="Setup.PendingActivation"/>).</param>
    public static ProfilePlan Plan(
        OptimizationProfile profile,
        WindowsCatalog catalog,
        HardwareProfile? hardware,
        ISystemReader reader,
        Func<string, OperationEvaluation?> evaluate,
        IReadOnlyDictionary<string, Activation>? waiting = null,
        ProfileChoices? choices = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(evaluate);
        choices ??= ProfileChoices.None;

        var entries = new List<PlanEntry>();
        foreach (var entry in profile.Entries)
        {
            var planned = Base(entry, catalog.ById(entry.OperationId), hardware, reader, evaluate(entry.OperationId), waiting);
            var groupOn = !choices.GroupsOff.Contains(entry.GroupId);
            var wanted = choices.Entries.TryGetValue(entry.OperationId, out var chosen) ? chosen : entry.Role == EntryRole.Default;
            entries.Add(planned with { Included = planned.Selectable && groupOn && wanted });
        }

        entries = Cover(entries, reader);
        return new ProfilePlan(profile, [.. profile.Groups.Select(g => new PlanGroup(g, [.. entries.Where(e => e.Entry.GroupId.Equals(g.Id, StringComparison.OrdinalIgnoreCase))], !choices.GroupsOff.Contains(g.Id)))]);
    }

    private static PlanEntry Base(
        ProfileEntry entry, WindowsOperation? op, HardwareProfile? hardware, ISystemReader reader, OperationEvaluation? evaluation, IReadOnlyDictionary<string, Activation>? waiting)
    {
        PlanEntry Not(EntryState state, string reason, string detail) => new(entry, op, state, reason, detail, null, false, null);

        if (op is null)
        {
            return Not(EntryState.Unsupported, PlanReason.NotInCatalog, "This operation is not in the Windows catalog in use.");
        }

        if (!op.Applicable)
        {
            return Not(EntryState.Unsupported, PlanReason.NotApplicable, "Not a setting Mesura applies: it is a guide or an observation.");
        }

        if (entry.When is { } when)
        {
            if (hardware is null)
            {
                return Not(EntryState.CantTell, PlanReason.Unread, "The hardware hasn't been read yet.");
            }

            switch (when.Holds(hardware, out var failed))
            {
                case false:
                    return Not(EntryState.Unsupported, PlanReason.HardwarePrefix + failed, $"This PC doesn't have the hardware it is for ({failed}).");
                case null:
                    return Not(EntryState.CantTell, PlanReason.HardwarePrefix + failed, $"Couldn't tell whether this PC has the hardware it is for ({failed}), so it is left alone.");
            }
        }

        if (evaluation is null)
        {
            return Not(EntryState.CantTell, PlanReason.Unread, "The setting hasn't been read yet.");
        }

        var detail = evaluation.Reason;
        switch (evaluation.State)
        {
            case OperationState.Inapplicable:
                return Not(EntryState.Unsupported, InapplicableCode(op, reader), detail);
            case OperationState.Blocked:
                return Not(EntryState.CantTell, PlanReason.Blocked, detail);
            case OperationState.Unknown:
                return Not(EntryState.CantTell, PlanReason.Unread, detail);
            case OperationState.Manual or OperationState.Observation:
                return Not(EntryState.Unsupported, PlanReason.NotApplicable, detail);
            case OperationState.PendingRestart:
                return new PlanEntry(entry, op, EntryState.Waiting, null, detail, op.Activation, false, null);
            case OperationState.Applied:
                return waiting is not null && waiting.TryGetValue(op.Id, out var activation)
                    ? new PlanEntry(entry, op, EntryState.Waiting, null, detail, activation, false, null)
                    : new PlanEntry(entry, op, EntryState.Applied, null, detail, null, false, null);
            default:
                return new PlanEntry(entry, op, EntryState.ToApply, null, detail, null, false, null);
        }
    }

    /// <summary>
    /// Entries that only set power values the profile's own plan, once it is applied, already writes with the same value: they become "covered" and are no
    /// longer applied (nothing would change). Only a plan that is itself going to be applied covers anything: if the user unticks it, the entries are theirs again.
    /// </summary>
    private static List<PlanEntry> Cover(List<PlanEntry> entries, ISystemReader reader)
    {
        var plan = entries.FirstOrDefault(e => e is { Included: true, Operation.PowerPlan: { IsMesuraPlan: true } });
        if (plan is null)
        {
            return entries;
        }

        var written = new Dictionary<(string, string), uint>();
        foreach (var value in plan.Operation!.PowerPlan!.PlanValues)
        {
            if (value.Ac is { } ac && value.Needs.All(need => WindowsEvaluator.RequirementHolds(need, reader) == true))
            {
                written[(value.Subgroup.ToLowerInvariant(), value.Setting.ToLowerInvariant())] = ac;
            }
        }

        // A setting this PC doesn't register is skipped by the engine, so it neither needs covering nor stops the rest from being covered.
        var scheme = plan.Operation.PowerPlan!.BasedOn!;
        bool Covers(PowerSetting s) => written.TryGetValue((s.Subgroup.ToLowerInvariant(), s.Setting.ToLowerInvariant()), out var v) && v == s.AcValue;
        bool Registered(PowerSetting s) => reader.ReadPowerSetting(scheme, s.Subgroup, s.Setting) is not null;

        return [.. entries.Select(e => e.State == EntryState.ToApply && e.Operation is { } op && OnlyPowerSettings(op)
            && op.PowerSettings.Any(Registered) && op.PowerSettings.Where(Registered).All(Covers)
                ? e with { State = EntryState.Covered, Reason = PlanReason.Covered, Detail = $"The {plan.OperationId} plan already writes this value.", Included = false, CoveredBy = plan.OperationId }
                : e)];
    }

    private static bool OnlyPowerSettings(WindowsOperation op) =>
        op.PowerSettings.Count > 0 && op.PowerPlan is null && op.Registry.Count == 0 && op.RegistryEachSettings.Count == 0 && op.Services.Count == 0
        && op.Features.Count == 0 && op.Tasks.Count == 0 && op.TcpSettings.Count == 0 && op.SystemSettings.Count == 0 && op.AppxSettings.Count == 0 && op.AdapterSettings.Count == 0;

    /// <summary>Why an operation that doesn't apply here doesn't, as a code (the same questions the engine asked, in the order it asks them).</summary>
    public static string InapplicableCode(WindowsOperation op, ISystemReader reader)
    {
        ArgumentNullException.ThrowIfNull(op);
        ArgumentNullException.ThrowIfNull(reader);
        if ((op.MinBuild is { } min && reader.Build < min) || (op.MaxBuild is { } max && reader.Build > max))
        {
            return PlanReason.Build;
        }

        if (op.Requirements.Contains(DeviceRequirement.NoBattery) && reader.HasBattery == true)
        {
            return PlanReason.Battery;
        }

        if (op.Requirements.Contains(DeviceRequirement.NoModernStandby) && reader.ModernStandby == true)
        {
            return PlanReason.ModernStandby;
        }

        if (op.Requirements.Contains(DeviceRequirement.ProEdition) && reader.PolicyEdition == false)
        {
            return PlanReason.Edition;
        }

        if (op.Requirements.Contains(DeviceRequirement.Ram16Gb) && reader.PhysicalMemoryBytes < 15UL * 1024 * 1024 * 1024)
        {
            return PlanReason.Memory;
        }

        if (op.Requirements.Contains(DeviceRequirement.HybridCpu) && reader.HybridCpu == false)
        {
            return PlanReason.Hybrid;
        }

        if (op.Effect is { } check && string.Equals(reader.ReadEffect(check.Kind), "unsupported", StringComparison.OrdinalIgnoreCase))
        {
            return PlanReason.EffectUnsupported;
        }

        return PlanReason.Absent;
    }
}
