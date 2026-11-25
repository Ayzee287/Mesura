using System.Text;
using Mesura.Core.Hardware;
using Mesura.Core.Profiles;
using Mesura.Core.Setup;
using Mesura.Core.Windows;

namespace Mesura.Core.Tests.Windows;

/// <summary>A journal kept in memory, in the on-disk line format, that can be told to fail at its n-th entry (a full or locked disk).</summary>
internal sealed class MemoryJournal(string runId = "20261125T120000Z-windows-apply", string operation = "windows-apply") : IChangeJournal
{
    private readonly StringBuilder _text = new();

    public int FailAt { get; set; }

    public int Count { get; private set; }

    /// <summary>Called with each change as it is recorded, before the engine makes it.</summary>
    public Action<RecordedChange>? OnRecord { get; set; }

    public string Text => _text.ToString();

    public void Record(string operationId, string operationName, Activation activation, RecordedChange change)
    {
        if (++Count == FailAt)
        {
            throw new IOException("There is not enough space on the disk.");
        }

        OnRecord?.Invoke(change);
        if (_text.Length == 0)
        {
            _text.Append(RunJournal.HeaderLine(runId, operation, "2026-11-25T12:00:00.0000000Z", 4242, 1)).Append('\n');
        }

        _text.Append(RunJournal.EntryLine(operationId, operationName, activation, change)).Append('\n');
    }
}

/// <summary>
/// Crash-safe runs (0.8): every change is in the journal before it is made; a run that dies at any write leaves a journal from which the PC is put
/// back exactly; a journal that can't keep a change stops it; an interrupted undo can be run again; a setting the user changed since is left alone.
/// Everything against the in-memory Windows; the process "dies" through <see cref="FakeWindows.CrashAtWrite"/>.
/// </summary>
public sealed class CrashSafeRunTests
{
    private const string Balanced = "381b4222-f694-41f0-9685-ff5bb260df2e";
    private static readonly WindowsCatalog Catalog = WindowsCatalog.Default;
    private static readonly OptimizationProfiles Profiles = OptimizationProfiles.Default;

    /// <summary>A desktop whose Balanced scheme registers every power setting a profile touches, at values none of them wants.</summary>
    private static FakeWindows Pc()
    {
        var pc = new FakeWindows { HasBattery = false, ModernStandby = false, HybridCpu = true, PolicyEdition = true, PhysicalMemoryBytes = 32UL << 30 };
        var ops = Profiles.Profiles.SelectMany(p => p.Entries).Select(e => Catalog.ById(e.OperationId)!).DistinctBy(o => o.Id).ToList();
        foreach (var setting in ops.SelectMany(o => o.PowerSettings).Select(s => s.Setting)
                     .Concat(ops.Where(o => o.PowerPlan is not null).SelectMany(o => o.PowerPlan!.PlanValues).Select(v => v.Setting)))
        {
            pc.PowerSettings[$"{Balanced}|{setting}"] = 7;
            pc.PowerSettingsDc[$"{Balanced}|{setting}"] = 42;
        }

        pc.System["mmagent:memorycompression"] = "enabled";
        return pc;
    }

    private static HardwareProfile Desktop() => new(
        new WindowsPlatform(26100, "Professional", "24H2", true), DeviceForm.Desktop, new PowerPlatform(false, false, true),
        new CpuProfile("Test CPU", CpuMaker.Intel, 8, 16, true), 32UL << 30,
        [new GpuProfile("Test GPU", GpuMaker.Nvidia, 0x10DE, 1, "1.0", false)],
        [new DiskProfile("NVMe", StorageClass.Nvme, 1, true, Mesura.Core.Raw.BusKind.Nvme)], []);

    /// <summary>The whole Maximum level as the app would run it on this PC: many registry values, a new power plan, power and system settings.</summary>
    private static List<WindowsOperation> Maximum(FakeWindows pc) =>
        [.. ProfilePlanner.Plan(Profiles.ById("maximum-performance")!, Catalog, Desktop(), pc, id => Catalog.ById(id) is { } op ? WindowsEvaluator.Evaluate(op, pc) : null)
            .OperationIds.Select(id => Catalog.ById(id)!)];

    /// <summary>Everything the engine can change in the fake, in a comparable form (power values only of schemes that exist).</summary>
    private static string State(FakeWindows pc) => string.Join("\n",
        pc.Registry.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => $"reg {p.Key}={p.Value.Kind}:{p.Value.Value}")
            .Concat(pc.Services.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => $"svc {p.Key}={p.Value}"))
            .Concat(pc.PowerSettings.Where(p => pc.Schemes.Contains(p.Key.Split('|')[0])).OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => $"ac {p.Key}={p.Value}"))
            .Concat(pc.PowerSettingsDc.Where(p => pc.Schemes.Contains(p.Key.Split('|')[0])).OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => $"dc {p.Key}={p.Value}"))
            .Concat(pc.Schemes.Order(StringComparer.OrdinalIgnoreCase).Select(s => $"scheme {s}"))
            .Concat([$"active {pc.Active}"])
            .Concat(pc.System.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => $"sys {p.Key}={p.Value}"))
            .Concat(pc.Tasks.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => $"task {p.Key}={p.Value}")));

    /// <summary>What Mesura does at its next start: read the journal, check every change against the PC now, and build the interrupted record.</summary>
    private static SetupRun? Recover(FakeWindows pc, MemoryJournal journal, out RunJournalFindings findings, SetupRun? existing = null)
    {
        var parsed = RunJournal.Parse(journal.Text);
        Assert.NotNull(parsed);
        return RunJournal.Recover(parsed, new WindowsConfigurator(pc, pc).Check, existing, "2026-11-25T12:05:00.0000000Z", out findings);
    }

    [Fact]
    public void Every_change_is_in_the_journal_before_it_is_made_and_the_journal_holds_exactly_the_recorded_changes()
    {
        var pc = Pc();
        var ops = Maximum(pc);
        var engine = new WindowsConfigurator(pc, pc);
        var states = new List<ChangeState>();
        var journal = new MemoryJournal { OnRecord = change => states.Add(engine.Check(change)) };

        var outcomes = engine.Apply(ops, TestContext.Current.CancellationToken, journal);

        Assert.DoesNotContain(outcomes, o => !o.Ok);
        var recorded = outcomes.SelectMany(o => o.Changes).ToList();
        Assert.True(recorded.Count > 20, $"the scenario should change many settings ({recorded.Count})");
        // At the moment each change was journaled the PC didn't show it yet: journal first, write after.
        Assert.DoesNotContain(ChangeState.InPlace, states);
        var parsed = RunJournal.Parse(journal.Text)!;
        Assert.False(parsed.Torn);
        Assert.Equal(recorded, parsed.Entries.Select(e => e.Change));
    }

    [Fact]
    public void A_run_that_dies_at_any_write_leaves_a_journal_from_which_the_pc_is_put_back_exactly()
    {
        var total = Pc();
        new WindowsConfigurator(total, total).Apply(Maximum(total), TestContext.Current.CancellationToken);
        var writes = total.WriteCalls;
        Assert.True(writes > 20);

        for (var k = 1; k <= writes; k++)
        {
            var pc = Pc();
            var start = State(pc);
            var ops = Maximum(pc);
            pc.CrashAtWrite = k;
            var journal = new MemoryJournal();

            Assert.Throws<SimulatedCrash>(() => new WindowsConfigurator(pc, pc).Apply(ops, TestContext.Current.CancellationToken, journal));
            pc.CrashAtWrite = null; // the next start

            var record = Recover(pc, journal, out var findings);
            if (record is not null)
            {
                Assert.True(record.WasInterrupted);
                Assert.EndsWith(RunJournal.RecoveredSuffix, record.Id, StringComparison.Ordinal);
                var undo = new WindowsConfigurator(pc, pc).Revert(record.WindowsSteps);
                Assert.DoesNotContain(undo, u => u.Status is WindowsOpStatus.Failed or WindowsOpStatus.Blocked);
            }
            else
            {
                Assert.Equal(0, findings.InPlace + findings.Other);
            }

            Assert.True(start == State(pc), $"crash at write {k}: the PC was not put back exactly");
        }
    }

    [Fact]
    public void Recovering_the_same_journal_again_after_the_undo_finds_nothing_left_to_undo()
    {
        var pc = Pc();
        var ops = Maximum(pc);
        pc.CrashAtWrite = 15;
        var journal = new MemoryJournal();
        Assert.Throws<SimulatedCrash>(() => new WindowsConfigurator(pc, pc).Apply(ops, TestContext.Current.CancellationToken, journal));
        pc.CrashAtWrite = null;

        var record = Recover(pc, journal, out var first);
        Assert.NotNull(record);
        Assert.True(first.InPlace > 0);
        new WindowsConfigurator(pc, pc).Revert(record.WindowsSteps);

        Assert.Null(Recover(pc, journal, out var again));
        Assert.Equal(0, again.InPlace);
        // And undoing the record a second time changes nothing.
        Assert.All(new WindowsConfigurator(pc, pc).Revert(record.WindowsSteps), o => Assert.Equal(WindowsOpStatus.RevertSkipped, o.Status));
    }

    [Fact]
    public void A_setting_the_user_changed_after_the_crash_is_kept_for_undo_and_then_left_alone()
    {
        var pc = Pc();
        var ops = Maximum(pc);
        var start = State(pc);
        var whole = Pc();
        new WindowsConfigurator(whole, whole).Apply(Maximum(whole), TestContext.Current.CancellationToken);
        pc.CrashAtWrite = whole.WriteCalls - 2; // late in the run, after the registry values
        var journal = new MemoryJournal();
        Assert.Throws<SimulatedCrash>(() => new WindowsConfigurator(pc, pc).Apply(ops, TestContext.Current.CancellationToken, journal));
        pc.CrashAtWrite = null;

        // The user (or Windows) changes one of the values the run wrote before Mesura starts again.
        var changed = RunJournal.Parse(journal.Text)!.Entries.Select(e => e.Change).First(c => c.Kind == ChangeKind.Registry && c.Hive == "HKLM");
        var key = $@"{changed.Hive}\{changed.Path}\{changed.Name}";
        pc.Registry[key] = (RegistryKind.DWord, "12345");

        var record = Recover(pc, journal, out var findings);
        Assert.NotNull(record);
        Assert.True(findings.Other >= 1);
        Assert.Contains(record.WindowsSteps.SelectMany(s => s.Changes), c => c == changed); // kept: undo decides, with a fresh reading

        var undo = new WindowsConfigurator(pc, pc).Revert(record.WindowsSteps);

        Assert.Equal("12345", pc.Registry[key].Value);                                       // the later choice stays
        Assert.Contains(undo, u => u.Detail.Contains("left as they are", StringComparison.Ordinal));
        // Everything else is back as it was before the run.
        if (changed.Before is null)
        {
            pc.Registry.Remove(key);
        }
        else
        {
            pc.Registry[key] = (changed.RegistryKind ?? RegistryKind.DWord, changed.Before);
        }

        Assert.Equal(start, State(pc));
    }

    [Fact]
    public void A_journal_that_cannot_keep_a_change_stops_it_from_being_made_and_the_operation_puts_back_what_it_had_done()
    {
        var pc = new FakeWindows();
        var op = EngineFixtures.Op("appearance.dark-mode"); // two registry values
        Assert.Equal(2, op.Registry.Count);
        var before = State(pc);
        var journal = new MemoryJournal { FailAt = 2 };

        var outcome = new WindowsConfigurator(pc, pc).Apply([op], TestContext.Current.CancellationToken, journal).Single();

        Assert.Equal(WindowsOpStatus.Failed, outcome.Status);
        Assert.Contains("undo information", outcome.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(pc.Writes, w => w.EndsWith(@"\" + op.Registry[1].Name, StringComparison.OrdinalIgnoreCase)); // the second value was never written
        Assert.Equal(before, State(pc));    // and the first was put back
        Assert.Empty(outcome.Changes);
    }

    [Fact]
    public void An_undo_that_dies_part_way_can_be_run_again_and_finishes_and_its_journal_records_what_it_had_put_back()
    {
        var pc = Pc();
        var start = State(pc);
        var applied = new WindowsConfigurator(pc, pc).Apply(Maximum(pc), TestContext.Current.CancellationToken);
        var applyWrites = pc.WriteCalls;

        pc.CrashAtWrite = applyWrites + 5;
        var undoJournal = new MemoryJournal("20261125T121000Z-windows-revert", "windows-revert");
        Assert.Throws<SimulatedCrash>(() => new WindowsConfigurator(pc, pc).Revert(applied, undoJournal));
        pc.CrashAtWrite = null;

        var partial = Recover(pc, undoJournal, out var findings);
        Assert.NotNull(partial);
        Assert.Equal("windows-revert", partial.Operation);
        Assert.All(partial.WindowsSteps, s => Assert.Equal(WindowsOpStatus.Reverted, s.Status));
        Assert.True(findings.InPlace >= 1, "restores made before the write that died are found in place");
        Assert.Equal(1, findings.NotMade); // the restore that was journaled when the process died was never made

        // The original record is untouched: undoing it again restores the rest and skips what is already back.
        var again = new WindowsConfigurator(pc, pc).Revert(applied);
        Assert.DoesNotContain(again, o => o.Status is WindowsOpStatus.Failed or WindowsOpStatus.Blocked);
        Assert.Equal(start, State(pc));
    }

    [Fact]
    public void A_change_already_in_the_runs_own_record_is_not_recovered_twice()
    {
        var pc = Pc();
        var journal = new MemoryJournal();
        var outcomes = new WindowsConfigurator(pc, pc).Apply(Maximum(pc), TestContext.Current.CancellationToken, journal);
        // The record was written, then Mesura died before the journal was removed.
        var existing = SetupRun.Create("20261125T120000Z-windows-apply", "windows-apply", null, "s", "f", [], [], outcomes);

        Assert.Null(Recover(pc, journal, out var findings, existing));
        Assert.Equal(outcomes.Sum(o => o.Changes.Count), findings.AlreadyRecorded);
    }
}
