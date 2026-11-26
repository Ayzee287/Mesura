using Mesura.Core.Hardware;
using Mesura.Core.Windows;

namespace Mesura.Core.Profiles;

/// <summary>
/// What planning a level reads (0.9): the catalog in use, this PC's hardware profile (<c>null</c> when it hasn't been or couldn't be read), the system
/// reader, and each operation's evaluation on this PC. Pure: the readings come in, nothing is changed.
/// </summary>
/// <param name="Evaluate">An operation's evaluation on this PC now, or <c>null</c> when it hasn't been read.</param>
public sealed record PlanningContext(WindowsCatalog Catalog, HardwareProfile? Hardware, ISystemReader Reader, Func<string, OperationEvaluation?> Evaluate)
{
    /// <summary>What each applied operation still waits for (a restart, a sign-out), when the caller knows it.</summary>
    public IReadOnlyDictionary<string, Activation>? Waiting { get; init; }

    /// <summary>The level definitions; the built-in <c>profiles.json</c> unless a test passes its own.</summary>
    public OptimizationProfiles Profiles { get; init; } = OptimizationProfiles.Default;

    /// <summary>A context that evaluates each operation of <paramref name="catalog"/> through <paramref name="reader"/> the first time it is asked for, once.</summary>
    public static PlanningContext Reading(WindowsCatalog catalog, HardwareProfile? hardware, ISystemReader reader)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(reader);
        var cache = new Dictionary<string, OperationEvaluation?>(StringComparer.OrdinalIgnoreCase);
        return new PlanningContext(catalog, hardware, reader, id =>
        {
            if (!cache.TryGetValue(id, out var evaluation))
            {
                evaluation = catalog.ById(id) is { } op ? WindowsEvaluator.Evaluate(op, reader) : null;
                cache[id] = evaluation;
            }

            return evaluation;
        });
    }
}

/// <summary>How the operations a selection names one by one are treated.</summary>
public enum ExplicitOperations
{
    /// <summary>Applied as named, each still checked by the engine on this PC (the command line's <c>--id</c>, All settings).</summary>
    AsNamed,

    /// <summary>
    /// A saved PC setup (0.9, owner 2026-11-25): an operation that is an entry of a level with a hardware condition obeys that condition too, so a setup
    /// saved on one PC never applies on another what the level leaves out for that hardware.
    /// </summary>
    LevelConditions,
}

/// <summary>
/// What a selection of presets and operations resolves to on this PC (0.9): the operations to apply, in order, the plan of each level it names, the
/// operations it named one by one that are left out here (with the reason), and the levels it named that were set aside for another.
/// </summary>
/// <param name="Operations">What to apply: each level's planned operations at the level's place, other presets' lists, then the single operations; each once.</param>
/// <param name="Levels">The plan of each level that was planned (none, or one for the built-in levels).</param>
/// <param name="Skipped">Operations named one by one that this PC's hardware excludes or that couldn't be told, each as a plan entry with its reason.</param>
/// <param name="SetAside">Level ids named but not planned because another level named with them supersedes them (Reference when Maximum is named too).</param>
public sealed record WindowsResolution(
    IReadOnlyList<WindowsOperation> Operations, IReadOnlyList<ProfilePlan> Levels, IReadOnlyList<PlanEntry> Skipped, IReadOnlyList<string> SetAside)
{
    /// <summary>
    /// Everything left out on this PC with a reason, each operation once: the levels' entries that are not for it or couldn't be told, then the skipped single
    /// operations a level doesn't already name.
    /// </summary>
    public IReadOnlyList<PlanEntry> NotHere => [.. Levels.SelectMany(l => l.NotHere).Concat(Skipped).DistinctBy(e => e.OperationId, StringComparer.OrdinalIgnoreCase)];
}

/// <summary>
/// The one place a level is planned (0.9, "Planner Everywhere", vault D054 and the owner's Phase 1 decisions of 2026-11-25, superseding decision O1 of
/// 2026-11-03). Optimize, <c>windows apply --profile</c> and <c>--preset</c>, saved PC setups in the app and the command line, the "level is in place"
/// check when a setup is saved and the tray all come here, so a hardware condition in <c>profiles.json</c> holds on every path: a preset id that names
/// a level is planned for this PC with the level's defaults and is never its whole list applied blindly. Presets that are not levels (collections,
/// the Ghost Spectre preset) carry no hardware conditions and resolve to their lists as before.
/// </summary>
public static class LevelPlanning
{
    public const string ReferenceId = "recommended-gaming";
    public const string MaximumId = "maximum-performance";

    /// <summary>Whether a preset is a level: one of the built-in profiles, or a primary preset of an edited catalog (planned as one group).</summary>
    public static bool IsLevel(WindowsPreset preset, OptimizationProfiles profiles)
    {
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentNullException.ThrowIfNull(profiles);
        return preset.Tier == PresetTier.Primary || profiles.ById(preset.Id) is not null;
    }

    /// <summary>The profile a level preset is planned from: the built-in one of its id, else the preset as one group.</summary>
    public static OptimizationProfile ProfileFor(WindowsPreset level, OptimizationProfiles profiles)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(profiles);
        return profiles.ById(level.Id) ?? profiles.FromPreset(level);
    }

    /// <summary>A level planned for this PC with the user's choices (none: the level's defaults).</summary>
    public static ProfilePlan Plan(WindowsPreset level, PlanningContext context, ProfileChoices? choices = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Plan(ProfileFor(level, context.Profiles), context, choices);
    }

    /// <summary>A profile planned for this PC with the user's choices (none: its defaults).</summary>
    public static ProfilePlan Plan(OptimizationProfile profile, PlanningContext context, ProfileChoices? choices = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(context);
        return ProfilePlanner.Plan(profile, context.Catalog, context.Hardware, context.Reader, context.Evaluate, context.Waiting, choices);
    }

    /// <summary>
    /// Resolves presets and single operations, in the order given, to what is applied on this PC. A level is planned with its defaults; when the built-in
    /// Maximum is named, Reference named with it is set aside (Maximum holds everything Reference does except its power plan, so planning both would put
    /// two plans in one run). Other presets resolve to their lists. Single operations follow <paramref name="explicitOperations"/>.
    /// </summary>
    /// <exception cref="ArgumentException">A preset or operation id the catalog doesn't have (callers check ids first and say so in their own words).</exception>
    public static WindowsResolution Resolve(IEnumerable<string> presetIds, IEnumerable<string> operationIds, PlanningContext context, ExplicitOperations explicitOperations)
    {
        ArgumentNullException.ThrowIfNull(presetIds);
        ArgumentNullException.ThrowIfNull(operationIds);
        ArgumentNullException.ThrowIfNull(context);
        var catalog = context.Catalog;
        var presets = presetIds.Select(id => catalog.PresetById(id) ?? throw new ArgumentException($"The catalog has no preset '{id}'.", nameof(presetIds)))
            .DistinctBy(p => p.Id, StringComparer.OrdinalIgnoreCase).ToList();
        var levels = presets.Where(p => IsLevel(p, context.Profiles)).ToList();
        var setAside = new List<string>();
        if (levels.Any(l => l.Id.Equals(MaximumId, StringComparison.OrdinalIgnoreCase)))
        {
            setAside.AddRange(levels.Where(l => l.Id.Equals(ReferenceId, StringComparison.OrdinalIgnoreCase)).Select(l => l.Id));
            levels.RemoveAll(l => l.Id.Equals(ReferenceId, StringComparison.OrdinalIgnoreCase));
        }

        var plans = levels.Select(l => Plan(l, context)).ToList();
        var operations = new List<WindowsOperation>();
        var levelsAdded = false;
        var skipped = new List<PlanEntry>();
        foreach (var preset in presets)
        {
            if (!IsLevel(preset, context.Profiles))
            {
#pragma warning disable RS0030 // The one place a preset's whole list is taken: a preset that is not a level has no hardware conditions to plan (see the class remarks).
                var listed = catalog.Resolve(preset);
#pragma warning restore RS0030
                // A collection a saved setup names is held to the same hardware policy as its single operations (0.9: the "gaming" collection holds the Game
                // Bar recording policy, which the dual-CCD X3D policy leaves out).
                foreach (var op in listed)
                {
                    if (explicitOperations == ExplicitOperations.LevelConditions && LeftOutByLevel(op, plans, context) is { } left)
                    {
                        skipped.Add(left);
                    }
                    else
                    {
                        operations.Add(op);
                    }
                }
            }
            else if (!levelsAdded)
            {
                // The levels' operations at the first level's place (power plans first within each, as the planner orders them).
                operations.AddRange(plans.SelectMany(p => p.OperationIds).Select(catalog.ById).OfType<WindowsOperation>());
                levelsAdded = true;
            }
        }

        foreach (var id in operationIds)
        {
            var op = catalog.ById(id) ?? throw new ArgumentException($"The catalog has no operation '{id}'.", nameof(operationIds));
            if (explicitOperations == ExplicitOperations.LevelConditions && LeftOutByLevel(op, plans, context) is { } left)
            {
                skipped.Add(left);
                continue;
            }

            operations.Add(op);
        }

        return new WindowsResolution([.. operations.DistinctBy(o => o.Id, StringComparer.OrdinalIgnoreCase)], plans,
            [.. skipped.DistinctBy(e => e.OperationId, StringComparer.OrdinalIgnoreCase)], setAside);
    }

    /// <summary>
    /// An operation a saved setup names (one by one or through a collection) that this PC's hardware leaves out, as a plan entry with the reason; <c>null</c>
    /// when nothing conditions it or every condition holds. The conditions are the <c>when</c> of the level entries that list it (the levels the selection
    /// plans; when none of them lists it, every built-in level that does: they agree today, and if they ever differ, all must hold) and the owner's
    /// hardware restrictions on it (0.9), which hold even for an operation no level lists. A condition that fails wins over one that can't be told.
    /// </summary>
    private static PlanEntry? LeftOutByLevel(WindowsOperation op, IReadOnlyList<ProfilePlan> plans, PlanningContext context)
    {
        bool Lists(ProfileEntry e) => e.OperationId.Equals(op.Id, StringComparison.OrdinalIgnoreCase);
        var entries = plans.Select(p => p.Profile).SelectMany(p => p.Entries).Where(Lists).ToList();
        if (entries.Count == 0)
        {
            entries = [.. context.Profiles.Profiles.SelectMany(p => p.Entries).Where(Lists)];
        }

        var restrictions = context.Profiles.RestrictionsFor(op.Id);
        var subject = entries.FirstOrDefault() ?? new ProfileEntry(op.Id, "", EntryRole.Default, restrictions.FirstOrDefault()?.Why ?? "", false, null);
        var conditions = entries.Where(e => e.When is not null).Select(e => (When: e.When!, From: (HardwareRestriction?)null))
            .Concat(restrictions.Select(r => (When: r.When, From: (HardwareRestriction?)r))).ToList();
        if (conditions.Count == 0)
        {
            return null;
        }

        if (context.Hardware is null)
        {
            return new PlanEntry(subject, op, EntryState.CantTell, PlanReason.Unread, "The hardware hasn't been read, and this setting is only for some hardware, so it is left alone.", null, false, null);
        }

        PlanEntry? unknown = null;
        foreach (var (when, from) in conditions)
        {
            switch (when.Holds(context.Hardware, out var failed))
            {
                case false:
                    return new PlanEntry(subject, op, EntryState.Unsupported, PlanReason.HardwarePrefix + failed,
                        from is { } restriction ? $"{restriction.Why} ({failed})" : $"This PC doesn't have the hardware it is for ({failed}).", null, false, null);
                case null:
                    unknown ??= new PlanEntry(subject, op, EntryState.CantTell, PlanReason.HardwarePrefix + failed,
                        $"Couldn't tell whether this PC has the hardware it is for ({failed}), so it is left alone.", null, false, null);
                    break;
            }
        }

        return unknown;
    }

    /// <summary>
    /// Whether a level is in place on this PC as its defaults plan it: no default entry is left to apply, covered or unreadable, and at least one is
    /// applied (or applied and waiting for a restart or sign-out). Entries that are not for this PC don't count against it. For a preset that is not a
    /// level, each of its operations is applied or doesn't apply here, and at least one is applied.
    /// </summary>
    public static bool InPlace(WindowsPreset preset, PlanningContext context)
    {
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentNullException.ThrowIfNull(context);
        if (IsLevel(preset, context.Profiles))
        {
            var defaults = Plan(preset, context).Entries.Where(e => e.Entry.Role == EntryRole.Default).ToList();
            return defaults.Count > 0
                && defaults.All(e => e.State is EntryState.Applied or EntryState.Waiting or EntryState.Unsupported)
                && defaults.Any(e => e.State is EntryState.Applied or EntryState.Waiting);
        }

        var states = preset.OperationIds.Select(id => context.Evaluate(id)?.State).ToList();
        return states.Count > 0
            && states.All(s => s is OperationState.Applied or OperationState.Inapplicable)
            && states.Any(s => s is OperationState.Applied);
    }

    /// <summary>
    /// The pairs of a profile's entries that set one setting two ways and can both apply on one PC (0.9): a pair whose hardware conditions exclude each
    /// other (<see cref="HardwareCondition.Disjoint"/>, for example both Mesura plans under <c>x3d-dual-ccd</c> and <c>not-x3d-dual-ccd</c>) never meets in
    /// a plan and is not a conflict. Each as the engine's sentence naming both. Optional entries count too, since the user can tick every one.
    /// </summary>
    public static IReadOnlyList<string> ConditionAwareConflicts(OptimizationProfile profile, WindowsCatalog catalog, bool includeOptional = true)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(catalog);
        var entries = profile.Entries.Where(e => includeOptional || e.Role == EntryRole.Default)
            .Select(e => (Entry: e, Op: catalog.ById(e.OperationId))).Where(x => x.Op is not null).ToList();
        var found = new List<string>();
        for (var i = 0; i < entries.Count; i++)
        {
            for (var j = i + 1; j < entries.Count; j++)
            {
                if (!HardwareCondition.Disjoint(entries[i].Entry.Conditions.Select(c => c.When), entries[j].Entry.Conditions.Select(c => c.When))
                    && WindowsConfigurator.Conflicts([entries[i].Op!, entries[j].Op!]) is { Count: > 0 } conflicts)
                {
                    found.AddRange(conflicts);
                }
            }
        }

        return [.. found.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// A planned level in two numbers, for a status line: how many operations it would apply now, and how many of its entries couldn't be read for a reason
    /// other than hardware (a setting unread, a blocked account). Entries for hardware this PC doesn't have, or whose hardware couldn't be told, are neither.
    /// </summary>
    public static (int Pending, int Unreadable) Status(ProfilePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return (plan.OperationIds.Count,
            plan.Entries.Count(e => e.State == EntryState.CantTell && e.Entry.Role == EntryRole.Default && e.Entry.Conditions.Count == 0));
    }
}
