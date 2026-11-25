using System.Diagnostics;
using Mesura.Core.Setup;
using Mesura.Core.Windows;

namespace Mesura.Setup.Windows;

/// <summary>One interrupted run found and turned into a history record (or found to have nothing left to undo).</summary>
/// <param name="Record">The rebuilt record, or <c>null</c> when every change it began was found not made (nothing to undo).</param>
public sealed record RecoveredRun(string RunId, string Operation, SetupRun? Record, RunJournalFindings Findings, bool Torn);

/// <summary>What one recovery pass did: runs recovered, journals still in use by a live run, and journals it couldn't handle (kept as they are).</summary>
public sealed record RecoveryReport(IReadOnlyList<RecoveredRun> Recovered, IReadOnlyList<string> StillRunning, IReadOnlyList<string> Problems)
{
    public static readonly RecoveryReport None = new([], [], []);

    /// <summary>Runs recovered with something left to undo.</summary>
    public IReadOnlyList<RecoveredRun> WithChanges => [.. Recovered.Where(r => r.Record is not null)];
}

/// <summary>
/// Finds runs that started changing the PC and never finished (0.8): a journal still in the history whose process is gone. For each one it reads
/// the PC first, then decides: changes still in place (or that can't be told) go into an ordinary history record, marked interrupted, so Undo in
/// Activity can put them back; changes back at their before-image are dropped. Nothing on the PC is changed here, and nothing is reverted on the
/// user's behalf. A journal is removed only after its record is written; one that can't be read or written is kept and reported, so a failure
/// never loses the information. Running it again finds nothing new: it is safe to repeat.
/// </summary>
public static class RunRecovery
{
    /// <param name="check">Where a recorded change stands now (<see cref="WindowsConfigurator.Check"/>), read with a fresh reader.</param>
    /// <param name="isAlive">Whether the process that wrote a journal is still running (id, start ticks); <see cref="ProcessIsAlive"/> outside tests.</param>
    public static RecoveryReport Recover(SetupStore store, Func<RecordedChange, ChangeState> check, Func<int, long, bool> isAlive, Func<DateTimeOffset> clock)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(check);
        ArgumentNullException.ThrowIfNull(isAlive);
        ArgumentNullException.ThrowIfNull(clock);

        IReadOnlyList<StoredRunJournal> journals;
        try
        {
            journals = store.ReadJournals();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new RecoveryReport([], [], [$"The history couldn't be searched for unfinished runs: {e.Message}"]);
        }

        var recovered = new List<RecoveredRun>();
        var running = new List<string>();
        var problems = new List<string>();
        foreach (var stored in journals)
        {
            try
            {
                if (RunJournal.Parse(stored.Text) is not { } journal)
                {
                    if (stored.Text.Trim().Length == 0 || !stored.Text.Contains('\n', StringComparison.Ordinal))
                    {
                        // Created, then the process died before or while writing its first line (the header). An entry is appended only after
                        // the whole header line, and a change is made only after its entry: nothing was changed, so there is nothing to keep.
                        store.DeleteJournal(stored.RunId);
                    }
                    else
                    {
                        problems.Add($"The journal of run {stored.RunId} can't be read; it is kept in its history folder.");
                    }

                    continue;
                }

                if (isAlive(journal.Header.ProcessId, journal.Header.ProcessStartTicks))
                {
                    running.Add(stored.RunId);
                    continue;
                }

                SetupRun? existing = null;
                if (store.ReadRunRecord(stored.RunId) is { } json)
                {
                    try
                    {
                        existing = SetupRun.FromJson(json);
                    }
                    catch (FormatException)
                    {
                        existing = null; // a damaged record: everything the journal holds is recovered beside it
                    }
                }

                var record = RunJournal.Recover(journal, SafeCheck(check), existing, SetupRun.Stamp(clock()), out var findings);
                if (record is not null)
                {
                    store.WriteRunRecord(record.Id, record.ToJson());
                }

                store.DeleteJournal(stored.RunId);
                recovered.Add(new RecoveredRun(stored.RunId, journal.Header.Operation, record, findings, journal.Torn));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                problems.Add($"The unfinished run {stored.RunId} couldn't be recovered: {e.Message} Its journal is kept, and Mesura tries again next time.");
            }
        }

        return new RecoveryReport(recovered, running, problems);
    }

    /// <summary>
    /// A check that never throws: a reading that fails in an unexpected way counts as "can't tell", so the change is kept for undo, which reads again.
    /// The journal is a user-writable file: an entry it can't make sense of must not stop the others from being recovered.
    /// </summary>
    private static Func<RecordedChange, ChangeState> SafeCheck(Func<RecordedChange, ChangeState> check) => change =>
    {
        try
        {
            return check(change);
        }
#pragma warning disable CA1031 // Deliberately broad: see the summary.
        catch (Exception)
#pragma warning restore CA1031
        {
            return ChangeState.Other;
        }
    };

    /// <summary>
    /// Whether the process that wrote a journal is still running: the same id <b>and</b> the same start time (a later process can reuse the id). When
    /// the start time can't be read (another account's elevated process), a Mesura process with that id counts as running, anything else as gone.
    /// </summary>
    public static bool ProcessIsAlive(int processId, long startTicks)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return false; // no such process
        }

        using (process)
        {
            try
            {
                if (process.HasExited)
                {
                    return false;
                }

                return Math.Abs(process.StartTime.ToUniversalTime().Ticks - startTicks) < TimeSpan.TicksPerSecond;
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
            {
                try
                {
                    return process.ProcessName.StartsWith("Mesura", StringComparison.OrdinalIgnoreCase);
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            }
        }
    }
}
