using System.Diagnostics;
using Mesura.Core.Setup;
using Mesura.Core.Windows;
using Mesura.Setup.Windows;

namespace Mesura.Setup.Tests;

/// <summary>
/// Crash-safe runs on disk (0.8), in a temporary history folder: the journal is written line by line and survives; the run's record replaces it;
/// records and kept files are replaced whole; recovery turns a dead process's journal into an interrupted record once, leaves a live run's journal
/// alone, keeps what it can't read, and is safe to repeat. The PC is never read: where a change stands comes from a stand-in check.
/// </summary>
public sealed class RunRecoveryTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mesura-recovery-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private SetupStore Store() => new(_dir);

    private static readonly DateTimeOffset Started = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);

    private static RecordedChange Reg(string name) =>
        new(ChangeKind.Registry, $@"HKLM\SOFTWARE\Test\{name}", "HKLM", @"SOFTWARE\Test", name, RegistryKind.DWord, "0", "1", RegistryKind.DWord);

    private static readonly Func<int, long, bool> Dead = (_, _) => false;

    private static RecoveryReport Recover(SetupStore store, Func<RecordedChange, ChangeState>? check = null, Func<int, long, bool>? alive = null) =>
        RunRecovery.Recover(store, check ?? (_ => ChangeState.InPlace), alive ?? Dead, () => Started.AddMinutes(5));

    [Fact]
    public void A_journal_is_written_only_from_the_first_change_and_reads_back_line_by_line()
    {
        var store = Store();
        var journal = new RunJournalFile(store, "20261125T120000Z-windows-apply", "windows-apply", Started);
        Assert.Empty(store.ReadJournals()); // a run that changes nothing leaves no journal

        journal.Record("op.a", "A", Activation.Immediate, Reg("A"));
        journal.Record("op.b", "B", Activation.Reboot, Reg("B"));

        var stored = Assert.Single(store.ReadJournals());
        var parsed = RunJournal.Parse(stored.Text)!;
        Assert.Equal(Environment.ProcessId, parsed.Header.ProcessId);
        Assert.Equal(["op.a", "op.b"], parsed.Entries.Select(e => e.OperationId));
        Assert.True(RunRecovery.ProcessIsAlive(parsed.Header.ProcessId, parsed.Header.ProcessStartTicks)); // this test's own process
    }

    [Fact]
    public void Writing_the_runs_record_replaces_its_journal_and_leaves_no_temporary_file()
    {
        var store = Store();
        const string id = "20261125T120000Z-windows-apply";
        new RunJournalFile(store, id, "windows-apply", Started).Record("op.a", "A", Activation.Immediate, Reg("A"));

        store.WriteRunRecord(id, SetupRun.Create(id, "windows-apply", null, "s", "f", [], []).ToJson());
        store.KeepRunFile(id, "kept.bin", [1, 2, 3]);

        Assert.False(store.HasJournal(id));
        Assert.NotNull(store.ReadRunRecord(id));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(store.RunFilePath(id, "kept.bin")!));
        Assert.Empty(Directory.EnumerateFiles(_dir, "*.mesura-tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void Rewriting_a_record_replaces_it_whole()
    {
        var store = Store();
        const string id = "20261125T120000Z-setup";
        store.WriteRunRecord(id, new string('x', 50_000));
        store.WriteRunRecord(id, "short");

        Assert.Equal("short", store.ReadRunRecord(id));
    }

    [Fact]
    public void A_dead_runs_journal_becomes_an_interrupted_record_once_and_its_journal_goes()
    {
        var store = Store();
        const string id = "20261125T120000Z-windows-apply";
        var journal = new RunJournalFile(store, id, "windows-apply", Started);
        journal.Record("op.a", "A", Activation.Immediate, Reg("A"));
        journal.Record("op.a", "A", Activation.Immediate, Reg("B"));

        var report = Recover(store, c => c.Name == "A" ? ChangeState.InPlace : ChangeState.NotMade);

        var recovered = Assert.Single(report.Recovered);
        Assert.Equal(new RunJournalFindings(1, 1, 0, 0), recovered.Findings);
        Assert.False(store.HasJournal(id));
        var record = SetupRun.FromJson(store.ReadRunRecord(id + RunJournal.RecoveredSuffix)!);
        Assert.True(record.WasInterrupted);
        Assert.Equal([Reg("A")], record.WindowsSteps.Single().Changes);
        Assert.Contains(id + RunJournal.RecoveredSuffix, store.ListRuns());

        // Again: nothing left to find, nothing written twice.
        Assert.Empty(Recover(store).Recovered);
        Assert.Single(store.ListRuns());
    }

    [Fact]
    public void A_journal_whose_changes_were_never_made_is_removed_without_a_record()
    {
        var store = Store();
        new RunJournalFile(store, "20261125T120000Z-windows-apply", "windows-apply", Started).Record("op.a", "A", Activation.Immediate, Reg("A"));

        var report = Recover(store, _ => ChangeState.NotMade);

        Assert.Null(Assert.Single(report.Recovered).Record);
        Assert.Empty(store.ReadJournals());
        Assert.Empty(store.ListRuns());
    }

    [Fact]
    public void A_live_runs_journal_is_left_alone()
    {
        var store = Store();
        new RunJournalFile(store, "20261125T120000Z-windows-apply", "windows-apply", Started).Record("op.a", "A", Activation.Immediate, Reg("A"));

        var report = Recover(store, alive: RunRecovery.ProcessIsAlive); // written by this very process, which is running

        Assert.Equal(["20261125T120000Z-windows-apply"], report.StillRunning);
        Assert.Empty(report.Recovered);
        Assert.Single(store.ReadJournals());
    }

    [Fact]
    public void A_journal_that_cannot_be_read_is_kept_and_reported_and_an_empty_one_is_removed()
    {
        var store = Store();
        var damaged = Path.Combine(_dir, "history", "20261125T120000Z-windows-apply");
        var empty = Path.Combine(_dir, "history", "20261125T130000Z-windows-apply");
        Directory.CreateDirectory(damaged);
        Directory.CreateDirectory(empty);
        File.WriteAllText(Path.Combine(damaged, RunJournal.FileName), "{garbage\n{\"operationId\":\"op.a\"}\n");
        File.WriteAllText(Path.Combine(empty, RunJournal.FileName), "");
        var tornHeader = Path.Combine(_dir, "history", "20261125T140000Z-windows-apply");
        Directory.CreateDirectory(tornHeader);
        File.WriteAllText(Path.Combine(tornHeader, RunJournal.FileName), "{\"kind\":\"Mesura.RunJou"); // died while writing the header: nothing was changed

        var report = Recover(store);

        Assert.Contains(report.Problems, p => p.Contains("20261125T120000Z-windows-apply", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(damaged, RunJournal.FileName)));
        Assert.False(File.Exists(Path.Combine(empty, RunJournal.FileName)));
        Assert.False(File.Exists(Path.Combine(tornHeader, RunJournal.FileName)));
        Assert.Single(report.Problems); // the torn header is no problem to report at every start
    }

    [Fact]
    public void A_setup_interrupted_after_its_record_was_written_recovers_only_the_new_changes_beside_it()
    {
        var store = Store();
        const string id = "20261125T120000Z-setup";
        var earlier = new WindowsOpOutcome("op.a", "A", WindowsOpStatus.Applied, "Applied.", Activation.Immediate, [Reg("A")]);
        store.WriteRunRecord(id, SetupRun.Create(id, "setup", null, "s", "f", [], [], [earlier]).ToJson());
        var journal = new RunJournalFile(store, id, "setup", Started);
        journal.Record("op.a", "A", Activation.Immediate, Reg("A")); // already in the record (the journal outlived it)
        journal.Record("op.b", "B", Activation.Immediate, Reg("B")); // the step that was interrupted

        var report = Recover(store);

        Assert.Equal(1, Assert.Single(report.Recovered).Findings.AlreadyRecorded);
        var record = SetupRun.FromJson(store.ReadRunRecord(id + RunJournal.RecoveredSuffix)!);
        Assert.Equal(["op.b"], record.WindowsSteps.Select(s => s.OperationId));
        Assert.Equal([Reg("A")], SetupRun.FromJson(store.ReadRunRecord(id)!).WindowsSteps.Single().Changes); // the setup's own record untouched
    }

    [Fact]
    public void A_check_that_throws_counts_as_cannot_tell_and_keeps_the_change()
    {
        var store = Store();
        new RunJournalFile(store, "20261125T120000Z-windows-apply", "windows-apply", Started).Record("op.a", "A", Activation.Immediate, Reg("A"));

        var report = Recover(store, _ => throw new NullReferenceException("a damaged entry"));

        Assert.Equal(1, Assert.Single(report.Recovered).Findings.Other);
        Assert.NotNull(store.ReadRunRecord("20261125T120000Z-windows-apply" + RunJournal.RecoveredSuffix));
    }

    [Fact]
    public void A_process_that_is_gone_or_started_at_another_time_is_not_alive()
    {
        using var self = Process.GetCurrentProcess();
        var ticks = self.StartTime.ToUniversalTime().Ticks;

        Assert.True(RunRecovery.ProcessIsAlive(self.Id, ticks));
        Assert.False(RunRecovery.ProcessIsAlive(self.Id, ticks - TimeSpan.TicksPerHour)); // the id reused by a later process
        Assert.False(RunRecovery.ProcessIsAlive(int.MaxValue, ticks));
    }
}
