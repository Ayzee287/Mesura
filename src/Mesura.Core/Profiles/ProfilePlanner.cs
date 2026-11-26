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

    /// <summary>No physical adapter of the kind the operation is for: <c>adapter-none.ethernet</c> (no Ethernet port), <c>adapter-none.wifi</c>, <c>adapter-none.any</c>.</summary>
    public const string AdapterNonePrefix = "adapter-none.";

    /// <summary>An adapter of the right kind, from another maker than the operation is for: <c>adapter-maker.ethernet</c>, <c>adapter-maker.wifi</c>, <c>adapter-maker.any</c>.</summary>
    public const string AdapterMakerPrefix = "adapter-maker.";

    /// <summary>The right adapter is there and its driver offers none of the settings with the wanted values: the hardware or driver says no (so "not supported", not "not here").</summary>
    public const string AdapterDriver = "adapter-driver";

    /// <summary>No graphics adapter of the maker the setting is for.</summary>
    public const string GpuNone = "gpu-none";

    /// <summary>The graphics adapter is there but its driver can't offer or read the control: the hardware or driver says no.</summary>
    public const string GpuDriver = "gpu-driver";

    /// <summary>
    /// Whether a code means that the hardware or its driver says no ("not supported"), as opposed to something simply not being on this PC ("not for this PC"). Neither is an
    /// error, and neither is said in the other's words.
    /// </summary>
    public static bool MeansUnsupported(string? code) => code is EffectUnsupported or AdapterDriver or GpuDriver;

    /// <summary>
    /// The interface's string key for the adapter and graphics codes of 0.7 (<c>Reason.NoAdapter.ethernet</c>...), or <c>null</c> for any other code. One place, so the list of settings
    /// and the plan of Optimization say the same thing.
    /// </summary>
    public static string? AdapterReasonKey(string? code) => code switch
    {
        null => null,
        AdapterDriver => "Reason.AdapterDriver",
        GpuNone => "Reason.NoGpu",
        GpuDriver => "Reason.GpuDriver",
        _ when code.StartsWith(AdapterNonePrefix, StringComparison.Ordinal) => "Reason.NoAdapter." + code[AdapterNonePrefix.Length..],
        _ when code.StartsWith(AdapterMakerPrefix, StringComparison.Ordinal) => "Reason.OtherMaker." + code[AdapterMakerPrefix.Length..],
        _ => null,
    };
    public const string Unread = "unread";
    public const string Blocked = "blocked";

    /// <summary>
    /// Built-in apps can't be removed or put back here: Mesura runs as another account than the signed-in user (0.9), and Windows removes and registers
    /// apps for the account that asks (<see cref="WindowsEvaluator.OtherAccountCode"/>).
    /// </summary>
    public const string OtherAccount = WindowsEvaluator.OtherAccountCode;
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

        bool Wanted(ProfileEntry e) => !choices.GroupsOff.Contains(e.GroupId)
            && (choices.Entries.TryGetValue(e.OperationId, out var chosen) ? chosen : e.Role == EntryRole.Default);
        entries = Cover(Overridden(entries, reader, Wanted), reader, Wanted);
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

        // The entry's own hardware condition and the owner's restrictions on its operation (0.9): one that fails leaves it out; one that can't be told leaves
        // it alone. A failed condition wins over one that couldn't be told, so the stated reason is the certain one.
        if (entry.Conditions.Count > 0)
        {
            if (hardware is null)
            {
                return Not(EntryState.CantTell, PlanReason.Unread, "The hardware hasn't been read yet.");
            }

            PlanEntry? unknown = null;
            foreach (var (when, from) in entry.Conditions)
            {
                switch (when.Holds(hardware, out var failed))
                {
                    case false:
                        return Not(EntryState.Unsupported, PlanReason.HardwarePrefix + failed, from is { } restriction
                            ? $"{restriction.Why} ({failed})"
                            : $"This PC doesn't have the hardware it is for ({failed}).");
                    case null:
                        unknown ??= Not(EntryState.CantTell, PlanReason.HardwarePrefix + failed,
                            $"Couldn't tell whether this PC has the hardware it is for ({failed}), so it is left alone.");
                        break;
                }
            }

            if (unknown is not null)
            {
                return unknown;
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
                return Not(EntryState.Unsupported, InapplicableCode(evaluation, reader), detail);
            case OperationState.Blocked:
                return Not(EntryState.CantTell, evaluation.ReasonCode ?? PlanReason.Blocked, detail);
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
    /// <para>
    /// The other way round too (0.8): a power entry is read against the plan that is active <b>now</b>, but the run first switches to the profile's own plan, which
    /// starts from Balanced (or from the copy made earlier). An entry already set in the plan in use now and not kept by the plan switched to (core parking at
    /// 100 % in High performance, at Balanced's default in the copy) would be skipped as "already set" and missing afterwards; it is applied after the switch.
    /// </para>
    /// </summary>
    /// <param name="wanted">Whether the user's choices (group on, entry ticked or default) include an entry that turns out to need applying.</param>
    private static List<PlanEntry> Cover(List<PlanEntry> entries, ISystemReader reader, Func<ProfileEntry, bool> wanted)
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

        // What each setting holds once the run has switched plans: the plan's own value, else what the scheme it switches to holds (an existing copy is kept
        // and only the plan's values are written into it; a new copy starts as Balanced).
        var target = reader.ReadPower().Schemes.Contains(plan.Operation.PowerPlan.SchemeGuid) ? plan.Operation.PowerPlan.SchemeGuid : scheme;
        uint? AfterSwitch(PowerSetting s) => written.TryGetValue((s.Subgroup.ToLowerInvariant(), s.Setting.ToLowerInvariant()), out var v)
            ? v
            : reader.ReadPowerSetting(target, s.Subgroup, s.Setting);

        return [.. entries.Select(e => e.State == EntryState.ToApply && e.Operation is { } op && OnlyPowerSettings(op)
            && op.PowerSettings.Any(Registered) && op.PowerSettings.Where(Registered).All(Covers)
                ? e with { State = EntryState.Covered, Reason = PlanReason.Covered, Detail = $"The {plan.OperationId} plan already writes this value.", Included = false, CoveredBy = plan.OperationId }
            : e.State == EntryState.Applied && e.Operation is { } applied && OnlyPowerSettings(applied)
                && applied.PowerSettings.Where(Registered).Any(s => AfterSwitch(s) is { } after && after != s.AcValue)
                ? e with
                {
                    State = EntryState.ToApply,
                    Reason = null,
                    Detail = $"Set in the power plan in use now, but the {plan.OperationId} plan this profile switches to doesn't keep it, so it is applied after the switch.",
                    Included = wanted(e.Entry),
                }
            : e)];
    }

    /// <summary>
    /// A Mesura plan the profile already applied, whose only differences from its own values are settings the profile's own wanted power entries set on top
    /// of it, is in place (0.9). On a dual-CCD X3D, Maximum's plan is Balanced Mesura (D054 item 4) and Maximum's drive and PCI Express entries then set two
    /// of its values further; read value by value, the plan looks "partly applied", so every run would switch it back and every entry would follow again.
    /// The plan counts as applied only when it is the active scheme, every value it writes that this PC registers is either its own or exactly the value one
    /// of those entries (applied, and still wanted) writes, and no battery value differs.
    /// </summary>
    private static List<PlanEntry> Overridden(List<PlanEntry> entries, ISystemReader reader, Func<ProfileEntry, bool> wanted)
    {
        bool Explained(PlanEntry planEntry, PowerPlanSetting plan)
        {
            if (!string.Equals(reader.ReadPower().ActiveScheme, plan.SchemeGuid, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var differs = false;
            foreach (var value in plan.PlanValues.Where(v => v.Needs.All(need => WindowsEvaluator.RequirementHolds(need, reader) == true)))
            {
                if (value.Dc is { } dc && reader.ReadPowerSettingDc(plan.SchemeGuid, value.Subgroup, value.Setting) is { } currentDc && currentDc != dc)
                {
                    return false;
                }

                if (value.Ac is not { } ac || reader.ReadPowerSetting(plan.SchemeGuid, value.Subgroup, value.Setting) is not { } current || current == ac)
                {
                    continue;
                }

                differs = true;
                var setByEntry = entries.Any(e => !ReferenceEquals(e, planEntry) && e.State is EntryState.Applied or EntryState.Waiting && wanted(e.Entry)
                    && e.Operation is { } op && OnlyPowerSettings(op)
                    && op.PowerSettings.Any(s => s.Setting.Equals(value.Setting, StringComparison.OrdinalIgnoreCase) && s.AcValue == current));
                if (!setByEntry)
                {
                    return false;
                }
            }

            return differs;
        }

        return [.. entries.Select(e => e.State == EntryState.ToApply && e.Operation?.PowerPlan is { IsMesuraPlan: true } plan && Explained(e, plan)
            ? e with { State = EntryState.Applied, Reason = null, Included = false, Detail = "In place: the profile's own power entries set some of its values further, on top of it." }
            : e)];
    }

    private static bool OnlyPowerSettings(WindowsOperation op) =>
        op.PowerSettings.Count > 0 && op.PowerPlan is null && op.Registry.Count == 0 && op.RegistryEachSettings.Count == 0 && op.Services.Count == 0
        && op.Features.Count == 0 && op.Tasks.Count == 0 && op.TcpSettings.Count == 0 && op.SystemSettings.Count == 0 && op.AppxSettings.Count == 0 && op.AdapterSettings.Count == 0;

    /// <summary>
    /// Why an evaluated operation doesn't apply here, as a code: the one the evaluation recorded when it knew better (an adapter or graphics case), else the operation's own
    /// requirements (<see cref="InapplicableCode(WindowsOperation, ISystemReader)"/>). Cheap: it reads nothing the evaluation did not already read, except for that fallback.
    /// </summary>
    public static string InapplicableCode(OperationEvaluation evaluation, ISystemReader reader)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        return evaluation.ReasonCode ?? InapplicableCode(evaluation.Operation, reader);
    }

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
