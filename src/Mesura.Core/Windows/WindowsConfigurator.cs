namespace Mesura.Core.Windows;

/// <summary>
/// Makes the changes an operation needs. The one implementation (<c>WindowsWriter</c> in the setup component) owns
/// the registry writes and the <c>dism</c>/<c>powercfg</c> launches; failures are thrown, and the configurator turns
/// them into an outcome.
/// </summary>
public interface IWindowsWriter
{
    void SetRegistry(string hive, string path, string name, RegistryKind kind, string value);

    void DeleteRegistry(string hive, string path, string name);

    /// <summary>Removes a key if it holds no values and no subkeys; does nothing otherwise.</summary>
    void DeleteRegistryKeyIfEmpty(string hive, string path);

    void SetServiceStart(string name, int start);

    /// <summary>Turns an optional feature on or off; returns whether a restart is needed to finish.</summary>
    bool SetFeature(string name, bool enabled);

    /// <summary>Installs a power scheme from a built-in template, keeping the template's GUID.</summary>
    void CreatePowerScheme(string schemeGuid);

    void ActivatePowerScheme(string schemeGuid);

    /// <summary>Deletes a power scheme this product installed (never the active one).</summary>
    void DeletePowerScheme(string schemeGuid);

    /// <summary>Sets a power setting's AC value in a scheme and reapplies the scheme if it is active.</summary>
    void SetPowerSetting(string scheme, string subgroup, string setting, uint acValue);

    /// <summary>Turns one of Windows' own scheduled tasks on or off (the task itself is kept).</summary>
    void SetTaskEnabled(string path, bool enabled);

    /// <summary>Sets one global TCP/IP setting (<see cref="TcpGlobalSetting"/>: an allowed netsh name and value only).</summary>
    void SetTcpGlobal(string name, string value);

    /// <summary>Changes one <see cref="SystemSetting"/> through its Windows tool (an allowed kind, name and value only).</summary>
    void SetSystemSetting(string kind, string name, string value) =>
        throw new NotSupportedException($"This writer can't change {kind} {name}.");

    /// <summary>Removes one packaged app for the signed-in user, by its full package name.</summary>
    void RemoveAppx(string packageFullName) =>
        throw new NotSupportedException("This writer can't remove apps.");

    /// <summary>Registers a removed app again from Windows' staged copy, by its package family name.</summary>
    void RestoreAppx(string packageFamilyName) =>
        throw new NotSupportedException("This writer can't restore apps.");
}

/// <summary>
/// Applies and reverts catalog operations: inspect, skip what is inapplicable or already done, change only the settings
/// that differ, read each one back, and record its exact before-image. An operation is all-or-nothing: if one of its
/// settings fails, the ones already changed are put back. Reverting restores before-images only where the setting still
/// holds the value the run wrote, so a later choice the user made is never overwritten.
/// </summary>
public sealed class WindowsConfigurator
{
    private readonly ISystemReader _reader;
    private readonly IWindowsWriter _writer;
    private readonly WindowsCatalog? _trusted;

    /// <param name="trustedCatalog">
    /// When given, an undo only restores targets that the recorded operation declares in this catalog (<see cref="RevertTrust"/>): a history
    /// record is a user-writable file and must not be able to point an elevated undo at a registry value, service or task the operation
    /// never touches. The app and the command line always pass it; tests with synthetic records leave it out.
    /// </param>
    public WindowsConfigurator(ISystemReader reader, IWindowsWriter writer, WindowsCatalog? trustedCatalog = null)
    {
        _trusted = trustedCatalog;
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
    }

    public IReadOnlyList<OperationEvaluation> Preview(IEnumerable<WindowsOperation> operations) =>
        [.. operations.DistinctBy(o => o.Id, StringComparer.OrdinalIgnoreCase).Select(o => WindowsEvaluator.Evaluate(o, _reader))];

    public IReadOnlyList<WindowsOpOutcome> Apply(IEnumerable<WindowsOperation> operations, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operations);
        var outcomes = new List<WindowsOpOutcome>();
        var batch = operations.DistinctBy(o => o.Id, StringComparer.OrdinalIgnoreCase).ToList();
        var conflicts = ConflictsById(ActingHere(batch, _reader));
        // Power settings are written into whichever plan is active, so a plan switch in the same batch goes first;
        // otherwise the order given is kept (the sort is stable).
        foreach (var op in batch.OrderBy(o => o.PowerPlan is null ? 1 : 0))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            // Two operations that set the same thing differently would leave whichever ran last, both reported as
            // applied: neither runs, and the user picks one.
            outcomes.Add(conflicts.TryGetValue(op.Id, out var why)
                ? new WindowsOpOutcome(op.Id, op.Name, WindowsOpStatus.Failed, "Not applied: " + why, op.Activation, [])
                : ApplyOne(op));
        }

        return outcomes;
    }

    /// <summary>
    /// Pairs of operations in one selection that set the same setting to different values (for example taskbar search
    /// as an icon and hidden, or two power plans), each as a sentence naming both and the setting. Empty when they can
    /// run together. With a reader, only operations that can act on this PC count (one that doesn't apply here, is
    /// guided or manual, or can't be read changes nothing, so it conflicts with nothing).
    /// </summary>
    public static IReadOnlyList<string> Conflicts(IEnumerable<WindowsOperation> operations, ISystemReader? reader = null)
    {
        ArgumentNullException.ThrowIfNull(operations);
        var batch = operations.DistinctBy(o => o.Id, StringComparer.OrdinalIgnoreCase).ToList();
        return [.. ConflictsById(reader is null ? batch : ActingHere(batch, reader)).Values.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// The same conflicts as <see cref="Conflicts"/>, as groups of operations (each group sets one setting differently),
    /// so an interface can name them in its own language. Each group is listed once.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<WindowsOperation>> ConflictGroups(IEnumerable<WindowsOperation> operations, ISystemReader? reader = null)
    {
        ArgumentNullException.ThrowIfNull(operations);
        var batch = operations.DistinctBy(o => o.Id, StringComparer.OrdinalIgnoreCase).ToList();
        var acting = reader is null ? batch : ActingHere(batch, reader);
        var groups = new List<IReadOnlyList<WindowsOperation>>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in ConflictingWrites(acting))
        {
            var key = string.Join("|", group.Select(o => o.Id).Order(StringComparer.OrdinalIgnoreCase));
            if (seen.Add(key))
            {
                groups.Add(group);
            }
        }

        return groups;
    }

    private static List<WindowsOperation> ActingHere(IEnumerable<WindowsOperation> operations, ISystemReader reader) =>
        [.. operations.Where(o => WindowsEvaluator.Evaluate(o, reader).State is OperationState.Applied or OperationState.NotApplied or OperationState.Partial)];

    private static IEnumerable<(string Target, string Value)> Targets(WindowsOperation op) =>
        op.Registry.Concat(op.RegistryEachSettings.Select(e => new RegistrySetting(e.Hive, e.PathPattern, e.Name, e.Kind, e.Value))).Select(r => ($@"{r.Hive}\{r.Path}\{r.Name}".ToUpperInvariant(), r.ClearBits != 0 ? $"clear {r.ClearBits}" : r.SetBits != 0 ? $"set {r.SetBits}" : r.Value))
            .Concat(op.Services.Select(s => ($"SERVICE {s.Name}".ToUpperInvariant(), s.Start.ToString())))
            .Concat(op.Features.Select(f => ($"FEATURE {f.Name}".ToUpperInvariant(), f.Enabled.ToString())))
            .Concat(op.Tasks.Select(t => ($"TASK {t.Path}".ToUpperInvariant(), t.Enabled.ToString())))
            .Concat(op.PowerSettings.Select(p => ($"POWER {p.Subgroup} {p.Setting}".ToUpperInvariant(), p.AcValue.ToString(System.Globalization.CultureInfo.InvariantCulture))))
            .Concat(op.TcpSettings.Select(t => ($"TCP {t.Name}".ToUpperInvariant(), t.Value)))
            .Concat(op.SystemSettings.Select(t => ($"SYSTEM {t.Kind} {t.Name}".ToUpperInvariant(), t.Value)))
            .Concat(op.AppxSettings.Select(a => ($"APPX {a.Name}".ToUpperInvariant(), "removed")))
            .Concat(op.PowerPlan is { } plan ? [("ACTIVE POWER SCHEME", plan.SchemeGuid)] : []);

    /// <summary>Each setting written differently by two or more of the operations: the setting and the operations.</summary>
    private static IEnumerable<IReadOnlyList<WindowsOperation>> ConflictingWrites(IReadOnlyList<WindowsOperation> operations) =>
        ConflictingTargets(operations).Select(c => c.Operations);

    private static IEnumerable<(string Target, IReadOnlyList<WindowsOperation> Operations)> ConflictingTargets(IReadOnlyList<WindowsOperation> operations)
    {
        var writes = operations.SelectMany(op => Targets(op).Select(t => (Op: op, t.Target, t.Value))).ToList();
        foreach (var group in writes.GroupBy(w => w.Target).Where(g => g.Select(w => w.Value).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1))
        {
            yield return (group.Key, [.. group.Select(w => w.Op).DistinctBy(o => o.Id, StringComparer.OrdinalIgnoreCase)]);
        }
    }

    private static Dictionary<string, string> ConflictsById(IReadOnlyList<WindowsOperation> operations)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (target, ops) in ConflictingTargets(operations))
        {
            var names = string.Join(" and ", ops.Select(o => $"'{o.Id}'"));
            var setting = ops[0].Registry.FirstOrDefault(r => $@"{r.Hive}\{r.Path}\{r.Name}".Equals(target, StringComparison.OrdinalIgnoreCase))?.Target
                ?? target.ToLowerInvariant();
            foreach (var op in ops)
            {
                result.TryAdd(op.Id, $"{names} set {setting} to different values; choose one of them.");
            }
        }

        return result;
    }

    private WindowsOpOutcome ApplyOne(WindowsOperation op)
    {
        WindowsOpOutcome Outcome(WindowsOpStatus status, string detail, IReadOnlyList<RecordedChange>? changes = null) =>
            new(op.Id, op.Name, status, detail, op.Activation, changes ?? []);

        var evaluation = WindowsEvaluator.Evaluate(op, _reader);
        switch (evaluation.State)
        {
            case OperationState.Manual:
                return Outcome(WindowsOpStatus.Manual, "A procedure carried out by hand; nothing was changed.");
            case OperationState.Observation:
                return Outcome(WindowsOpStatus.Observation, evaluation.Reason);
            case OperationState.Inapplicable:
                return Outcome(WindowsOpStatus.Inapplicable, evaluation.Reason);
            case OperationState.Unknown:
                return Outcome(WindowsOpStatus.Unknown, evaluation.Reason);
            case OperationState.Blocked:
                return Outcome(WindowsOpStatus.Blocked, evaluation.Reason);
            case OperationState.Applied:
                return Outcome(WindowsOpStatus.AlreadyApplied, evaluation.Reason);
        }

        if (op.NeedsElevation && !_reader.IsElevated)
        {
            return Outcome(WindowsOpStatus.NeedsElevation, "Changes machine-wide settings, which needs administrator rights; nothing was changed.");
        }

        var done = new List<RecordedChange>();
        var restartNeeded = false;
        try
        {
            foreach (var setting in op.ResolveRegistry(_reader))
            {
                var before = _reader.ReadRegistry(setting.Hive, setting.Path, setting.Name);
                if (before.Unreadable)
                {
                    throw new InvalidOperationException($"{setting.Target} couldn't be read, so it has no before-image; left alone.");
                }

                if (WindowsEvaluator.IsMet(setting, before))
                {
                    continue;
                }

                if (before.ValueExists && before.Kind is null)
                {
                    throw new InvalidOperationException($"{setting.Target} exists with a value type Mesura doesn't write; left alone.");
                }

                var (kind, value) = WindowsEvaluator.ValueToWrite(setting, before)
                    ?? throw new InvalidOperationException($"{setting.Target} holds '{before.Value}', which isn't a number; left alone.");
                // Recorded before the write: if the write fails half-way, the undo below still puts the value back.
                done.Add(new RecordedChange(ChangeKind.Registry, setting.Target, setting.Hive, setting.Path, setting.Name,
                    before.ValueExists ? before.Kind : kind, before.ValueExists ? before.Value : null, value,
                    kind, KeyCreated: !before.KeyExists, UserSid: setting.Hive == "HKCU" ? _reader.User.Sid : null));
                _writer.SetRegistry(setting.Hive, setting.Path, setting.Name, kind, value);
                if (!WindowsEvaluator.Matches(_reader.ReadRegistry(setting.Hive, setting.Path, setting.Name), kind, value))
                {
                    throw new InvalidOperationException($"Windows didn't keep {setting.Target} (a policy or protection may own it).");
                }
            }

            foreach (var service in op.Services)
            {
                var before = _reader.ReadService(service.Name);
                if (!before.Exists || before.Start == (int)service.Start)
                {
                    continue; // absent here (e.g. removed by the image) or already as wanted
                }

                done.Add(new RecordedChange(ChangeKind.Service, service.Target, null, null, service.Name, null,
                    before.Start?.ToString(System.Globalization.CultureInfo.InvariantCulture), ((int)service.Start).ToString(System.Globalization.CultureInfo.InvariantCulture)));
                _writer.SetServiceStart(service.Name, (int)service.Start);
                if (_reader.ReadService(service.Name).Start != (int)service.Start)
                {
                    throw new InvalidOperationException($"Windows didn't keep the start type of {service.Name} (it may be protected).");
                }
            }

            foreach (var feature in op.Features)
            {
                var before = _reader.ReadFeature(feature.Name);
                if (before is FeatureState.Absent || before == (feature.Enabled ? FeatureState.Enabled : FeatureState.Disabled))
                {
                    continue;
                }

                done.Add(new RecordedChange(ChangeKind.Feature, feature.Target, null, null, feature.Name, null,
                    before.ToString(), feature.Enabled ? nameof(FeatureState.Enabled) : nameof(FeatureState.Disabled)));
                restartNeeded |= _writer.SetFeature(feature.Name, feature.Enabled);
            }

            foreach (var task in op.Tasks)
            {
                var before = _reader.ReadTask(task.Path);
                var wanted = task.Enabled ? FeatureState.Enabled : FeatureState.Disabled;
                if (before is FeatureState.Absent || before == wanted)
                {
                    continue; // not on this PC (removed by the image), or already as wanted
                }

                if (before is FeatureState.Unknown)
                {
                    // Without its current state there is no before-image to restore, so nothing is changed.
                    throw new InvalidOperationException($"The state of {task.Path} couldn't be read.");
                }

                done.Add(new RecordedChange(ChangeKind.Task, task.Target, null, task.Path, null, null, before.ToString(), wanted.ToString()));
                _writer.SetTaskEnabled(task.Path, task.Enabled);
                if (_reader.ReadTask(task.Path) != wanted)
                {
                    throw new InvalidOperationException($"Windows didn't keep {task.Path} {(task.Enabled ? "on" : "off")}.");
                }
            }

            foreach (var tcp in op.TcpSettings)
            {
                var before = _reader.ReadTcpGlobal(tcp.Name)
                    ?? throw new InvalidOperationException($"The TCP setting {tcp.Name} couldn't be read, so it has no before-image; left alone.");
                if (string.Equals(before, tcp.Value, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!TcpGlobalSetting.IsAllowed(tcp.Name, before))
                {
                    // A value netsh can't be given back (an unrecognised level) would make the change impossible to undo.
                    throw new InvalidOperationException($"The TCP setting {tcp.Name} reads '{before}', which Mesura can't restore; left alone.");
                }

                done.Add(new RecordedChange(ChangeKind.TcpGlobal, tcp.Target, null, null, tcp.Name, null, before.ToLowerInvariant(), tcp.Value.ToLowerInvariant()));
                _writer.SetTcpGlobal(tcp.Name, tcp.Value);
                if (!string.Equals(_reader.ReadTcpGlobal(tcp.Name), tcp.Value, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Windows didn't keep the TCP setting {tcp.Name} at {tcp.Value} (a policy or a TCP setting template may hold another value).");
                }
            }

            foreach (var app in op.AppxSettings)
            {
                var installed = _reader.ReadAppxPackages(app.Name)
                    ?? throw new InvalidOperationException($"The installed apps couldn't be read, so {app.Name} has no before-image; left alone.");
                foreach (var fullName in installed)
                {
                    done.Add(new RecordedChange(ChangeKind.Appx, app.Target, null, fullName, app.Name, null, "installed", "removed"));
                    _writer.RemoveAppx(fullName);
                    if (_reader.ReadAppxPackages(app.Name) is not { } after || after.Contains(fullName, StringComparer.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException($"Windows didn't remove {app.Name} (it may be a protected system app).");
                    }
                }
            }

            foreach (var system in op.SystemSettings)
            {
                var before = _reader.ReadSystemSetting(system.Kind, system.Name)
                    ?? throw new InvalidOperationException($"{system.Target} couldn't be read, so it has no before-image; left alone.");
                if (string.Equals(before, system.Value, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!SystemSetting.IsAllowed(system.Kind, system.Name, before))
                {
                    // A reading the tool can't be given back would make the change impossible to undo.
                    throw new InvalidOperationException($"{system.Target} reads '{before}', which Mesura can't restore; left alone.");
                }

                done.Add(new RecordedChange(ChangeKind.SystemSetting, system.Target, null, system.Kind, system.Name, null, before.ToLowerInvariant(), system.Value.ToLowerInvariant()));
                _writer.SetSystemSetting(system.Kind, system.Name, system.Value);
                if (!string.Equals(_reader.ReadSystemSetting(system.Kind, system.Name), system.Value, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Windows didn't keep {system.Target} at {system.Value}.");
                }
            }

            if (op.PowerSettings.Count > 0)
            {
                var active = _reader.ReadPower().ActiveScheme
                    ?? throw new InvalidOperationException("The active power plan couldn't be read.");
                foreach (var setting in op.PowerSettings)
                {
                    var before = _reader.ReadPowerSetting(active, setting.Subgroup, setting.Setting);
                    if (before is null || before == setting.AcValue)
                    {
                        continue; // not on this machine, or already as wanted
                    }

                    done.Add(new RecordedChange(ChangeKind.PowerSetting, setting.Target, null, setting.Subgroup, setting.Setting, null,
                        before.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        setting.AcValue.ToString(System.Globalization.CultureInfo.InvariantCulture), Scheme: active));
                    _writer.SetPowerSetting(active, setting.Subgroup, setting.Setting, setting.AcValue);
                    if (_reader.ReadPowerSetting(active, setting.Subgroup, setting.Setting) != setting.AcValue)
                    {
                        throw new InvalidOperationException($"Windows didn't keep {setting.Name}.");
                    }
                }
            }

            if (op.PowerPlan is { } plan)
            {
                var power = _reader.ReadPower();
                if (!power.Schemes.Contains(plan.SchemeGuid))
                {
                    done.Add(new RecordedChange(ChangeKind.PowerSchemeCreated, $"power scheme {plan.Name}", null, null, null, null,
                        null, plan.SchemeGuid));
                    _writer.CreatePowerScheme(plan.SchemeGuid);
                }

                done.Add(new RecordedChange(ChangeKind.PowerScheme, plan.Target, null, null, null, null, power.ActiveScheme, plan.SchemeGuid));
                _writer.ActivatePowerScheme(plan.SchemeGuid);
                if (!string.Equals(_reader.ReadPower().ActiveScheme, plan.SchemeGuid, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Windows didn't make {plan.Name} active.");
                }
            }
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or InvalidOperationException
            or System.Security.SecurityException or ArgumentException)
        {
            // A write that failed without taking effect leaves nothing to put back: its record is dropped, so the user isn't
            // told a change "couldn't be put back" that never happened. A feature is read from a cache and dism may still be
            // working, so its record is kept.
            if (done.Count > 0 && done[^1].Kind != ChangeKind.Feature && AtBefore(done[^1]))
            {
                done.RemoveAt(done.Count - 1);
            }

            var stuck = Undo(done);
            var note = done.Count == 0 ? ""
                : stuck.Count == 0 ? " The settings it had already changed were put back."
                : $" {stuck.Count} of the settings it had already changed couldn't be put back; they are kept in this record, so 'windows revert' can retry.";
            return Outcome(WindowsOpStatus.Failed, e.Message + note, stuck);
        }

        var activation = restartNeeded ? Activation.Reboot : op.Activation;
        var detail = activation switch
        {
            Activation.ExplorerRestart => "Applied; visible after Explorer restarts or you sign out.",
            Activation.SignOut => "Applied; takes effect after you sign out and back in.",
            Activation.Reboot => "Applied; takes effect after Windows restarts.",
            _ => "Applied.",
        };
        return new WindowsOpOutcome(op.Id, op.Name, WindowsOpStatus.Applied, detail + AbsentNote(evaluation), activation, done);
    }

    private static string AbsentNote(OperationEvaluation evaluation)
    {
        var absent = evaluation.Items.Where(i => !i.Present).Select(i => i.Target).ToList();
        return absent.Count == 0 ? "" : $" Not present here, left out: {string.Join(", ", absent)}.";
    }

    /// <summary>Puts back changes made so far in a failed operation; returns the ones it couldn't put back.</summary>
    private List<RecordedChange> Undo(List<RecordedChange> done)
    {
        var stuck = new List<RecordedChange>();
        foreach (var change in Enumerable.Reverse(done))
        {
            try
            {
                Restore(change);
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException or InvalidOperationException
                or System.Security.SecurityException or ArgumentException)
            {
                stuck.Insert(0, change);
            }
        }

        return stuck;
    }

    /// <summary>Reverts the changes a run recorded, newest first, where each setting still holds what the run wrote.</summary>
    public IReadOnlyList<WindowsOpOutcome> Revert(IEnumerable<WindowsOpOutcome> recorded)
    {
        ArgumentNullException.ThrowIfNull(recorded);
        var outcomes = new List<WindowsOpOutcome>();
        foreach (var step in recorded.Reverse().Where(s => s.Changes.Count > 0))
        {
            if (step.Changes.Any(c => c.Hive == "HKCU" && !string.Equals(c.UserSid, _reader.User.Sid, StringComparison.OrdinalIgnoreCase)))
            {
                outcomes.Add(step with
                {
                    Status = WindowsOpStatus.Blocked,
                    Detail = _reader.User.Resolved
                        ? "These per-user settings were changed for another Windows user; revert them while that user is signed in."
                        : $"Blocked: {_reader.User.Reason}",
                    Changes = [],
                });
                continue;
            }

            if (_trusted is not null && Undeclared(step) is { } undeclared)
            {
                outcomes.Add(step with
                {
                    Status = WindowsOpStatus.Blocked,
                    Detail = $"Not undone: the record names {undeclared}, which this operation does not change in this version of Mesura (the history file was altered, or comes from another version). Nothing was changed.",
                    Changes = [],
                });
                continue;
            }

            if (step.Changes.Any(c => c.NeedsElevation) && !_reader.IsElevated)
            {
                outcomes.Add(step with { Status = WindowsOpStatus.NeedsElevation, Detail = "Reverting machine-wide settings needs administrator rights; nothing was changed.", Changes = [] });
                continue;
            }

            var reverted = new List<RecordedChange>();
            var skipped = new List<string>();
            var failures = new List<string>();
            foreach (var change in step.Changes.Reverse())
            {
                if (!StillAsWritten(change))
                {
                    skipped.Add(change.Target);
                    continue;
                }

                try
                {
                    Restore(change);
                    // Recorded the other way round, so reverting this revert run restores the run's values again.
                    reverted.Add(change with
                    {
                        Before = change.After,
                        After = change.Before,
                        RegistryKind = change.WrittenKind ?? change.RegistryKind,
                        WrittenKind = change.RegistryKind,
                        KeyCreated = false,
                    });
                }
                catch (Exception e) when (e is UnauthorizedAccessException or IOException or InvalidOperationException
                    or System.Security.SecurityException or ArgumentException)
                {
                    failures.Add($"{change.Target}: {e.Message}");
                }
            }

            var status = failures.Count > 0 ? WindowsOpStatus.Failed
                : reverted.Count > 0 ? WindowsOpStatus.Reverted
                : WindowsOpStatus.RevertSkipped;
            var parts = new List<string>();
            if (reverted.Count > 0)
            {
                parts.Add($"{reverted.Count} setting(s) restored");
            }

            if (skipped.Count > 0)
            {
                parts.Add($"no longer at the value this run wrote (already reverted, or changed since), left as they are: {string.Join(", ", skipped)}");
            }

            parts.AddRange(failures);
            outcomes.Add(new WindowsOpOutcome(step.OperationId, step.Name, status, string.Join("; ", parts) + ".", step.Activation, reverted));
        }

        return outcomes;
    }

    private string? Undeclared(WindowsOpOutcome step)
    {
        var operation = _trusted!.ById(step.OperationId) ?? WindowsCatalog.Retired.ById(step.OperationId);
        foreach (var change in step.Changes)
        {
            if (operation is null || !operation.Declares(change))
            {
                return change.Target;
            }
        }

        return null;
    }

    /// <summary>Whether the setting a change targets still holds its before-image (the write didn't take effect).</summary>
    private bool AtBefore(RecordedChange change) => change.Kind switch
    {
        ChangeKind.Registry when change.Before is null => _reader.ReadRegistry(change.Hive!, change.Path!, change.Name!) is { ValueExists: false, Unreadable: false },
        ChangeKind.Registry => WindowsEvaluator.Matches(_reader.ReadRegistry(change.Hive!, change.Path!, change.Name!),
            change.RegistryKind ?? RegistryKind.DWord, change.Before),
        ChangeKind.Service => _reader.ReadService(change.Name!).Start?.ToString(System.Globalization.CultureInfo.InvariantCulture) == change.Before,
        ChangeKind.Task => _reader.ReadTask(change.Path!).ToString() == change.Before,
        ChangeKind.TcpGlobal => string.Equals(_reader.ReadTcpGlobal(change.Name!), change.Before, StringComparison.OrdinalIgnoreCase),
        ChangeKind.SystemSetting => string.Equals(_reader.ReadSystemSetting(change.Path!, change.Name!), change.Before, StringComparison.OrdinalIgnoreCase),
        ChangeKind.Appx => _reader.ReadAppxPackages(change.Name!)?.Contains(change.Path!, StringComparer.OrdinalIgnoreCase) == true,
        ChangeKind.PowerSetting => change.Scheme is not null
            && _reader.ReadPowerSetting(change.Scheme, change.Path!, change.Name!)?.ToString(System.Globalization.CultureInfo.InvariantCulture) == change.Before,
        ChangeKind.PowerScheme => string.Equals(_reader.ReadPower().ActiveScheme, change.Before, StringComparison.OrdinalIgnoreCase),
        ChangeKind.PowerSchemeCreated => change.After is not null && !_reader.ReadPower().Schemes.Contains(change.After),
        _ => false,
    };

    private bool StillAsWritten(RecordedChange change) => change.Kind switch
    {
        ChangeKind.Registry when change.After is null => _reader.ReadRegistry(change.Hive!, change.Path!, change.Name!) is { ValueExists: false, Unreadable: false },
        ChangeKind.Registry => WindowsEvaluator.Matches(_reader.ReadRegistry(change.Hive!, change.Path!, change.Name!),
            change.WrittenKind ?? change.RegistryKind ?? RegistryKind.DWord, change.After),
        ChangeKind.PowerSetting => change.Scheme is not null
            && _reader.ReadPowerSetting(change.Scheme, change.Path!, change.Name!)?.ToString(System.Globalization.CultureInfo.InvariantCulture) == change.After,
        // Recorded the other way round by a revert (the scheme was deleted): still as written while it is absent.
        ChangeKind.PowerSchemeCreated when change.After is null => change.Before is not null && !_reader.ReadPower().Schemes.Contains(change.Before),
        ChangeKind.PowerSchemeCreated => _reader.ReadPower().Schemes.Contains(change.After ?? "")
            && !string.Equals(_reader.ReadPower().ActiveScheme, change.After, StringComparison.OrdinalIgnoreCase),
        ChangeKind.Service => _reader.ReadService(change.Name!).Start?.ToString(System.Globalization.CultureInfo.InvariantCulture) == change.After,
        ChangeKind.Feature => _reader.ReadFeature(change.Name!).ToString() == change.After,
        ChangeKind.Task => _reader.ReadTask(change.Path!).ToString() == change.After,
        ChangeKind.TcpGlobal => string.Equals(_reader.ReadTcpGlobal(change.Name!), change.After, StringComparison.OrdinalIgnoreCase),
        ChangeKind.SystemSetting => string.Equals(_reader.ReadSystemSetting(change.Path!, change.Name!), change.After, StringComparison.OrdinalIgnoreCase),
        // Removed while no package of that name is installed; a revert records it the other way round (After "installed").
        ChangeKind.Appx => (_reader.ReadAppxPackages(change.Name!) is { } apps)
            && (change.After == "removed" ? !apps.Contains(change.Path!, StringComparer.OrdinalIgnoreCase) : apps.Count > 0),
        ChangeKind.PowerScheme => string.Equals(_reader.ReadPower().ActiveScheme, change.After, StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    private void Restore(RecordedChange change)
    {
        switch (change.Kind)
        {
            case ChangeKind.Registry when change.Before is null:
                _writer.DeleteRegistry(change.Hive!, change.Path!, change.Name!);
                if (change.KeyCreated)
                {
                    _writer.DeleteRegistryKeyIfEmpty(change.Hive!, change.Path!);
                }

                break;
            case ChangeKind.Registry:
                _writer.SetRegistry(change.Hive!, change.Path!, change.Name!, change.RegistryKind ?? RegistryKind.DWord, change.Before);
                break;
            case ChangeKind.Service when int.TryParse(change.Before, out var start):
                _writer.SetServiceStart(change.Name!, start);
                break;
            case ChangeKind.Feature:
                _writer.SetFeature(change.Name!, change.Before == nameof(FeatureState.Enabled));
                break;
            case ChangeKind.Task when change.Before is nameof(FeatureState.Enabled) or nameof(FeatureState.Disabled):
                _writer.SetTaskEnabled(change.Path!, change.Before == nameof(FeatureState.Enabled));
                break;
            case ChangeKind.TcpGlobal when change.Name is not null && change.Before is not null && TcpGlobalSetting.IsAllowed(change.Name, change.Before):
                _writer.SetTcpGlobal(change.Name, change.Before);
                break;
            case ChangeKind.SystemSetting when change.Path is not null && change.Name is not null && change.Before is not null
                && SystemSetting.IsAllowed(change.Path, change.Name, change.Before):
                _writer.SetSystemSetting(change.Path, change.Name, change.Before);
                break;
            case ChangeKind.Appx when change.Before == "installed" && change.Path is not null && AppxSetting.FamilyOf(change.Path) is { } family:
                _writer.RestoreAppx(family);
                break;
            case ChangeKind.Appx when change.Before == "removed" && change.Path is not null:
                _writer.RemoveAppx(change.Path); // reverting a revert: removed again
                break;
            case ChangeKind.PowerScheme when change.Before is not null:
                _writer.ActivatePowerScheme(change.Before);
                break;
            case ChangeKind.PowerSchemeCreated when change.After is not null:
                if (_reader.ReadPower().Schemes.Contains(change.After))
                {
                    _writer.DeletePowerScheme(change.After); // absent: the install never happened, nothing to undo
                }

                break;
            case ChangeKind.PowerSchemeCreated when change.Before is not null:
                _writer.CreatePowerScheme(change.Before); // reverting the revert that deleted it
                break;
            case ChangeKind.PowerSetting when change.Scheme is not null && uint.TryParse(change.Before, out var value):
                _writer.SetPowerSetting(change.Scheme, change.Path!, change.Name!, value);
                break;
            default:
                throw new InvalidOperationException($"No before-image recorded for {change.Target}.");
        }
    }
}
