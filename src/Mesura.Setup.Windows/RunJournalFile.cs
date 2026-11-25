using System.Diagnostics;
using Mesura.Core.Setup;
using Mesura.Core.Windows;

namespace Mesura.Setup.Windows;

/// <summary>
/// A run's journal on disk (0.8, crash-safe runs): created with the run's id, handed to <see cref="WindowsConfigurator.Apply"/> or
/// <see cref="WindowsConfigurator.Revert"/>, and removed by <see cref="SetupStore.WriteRunRecord"/> when the run's record is written. Nothing is
/// written until the first change, so a run that changes nothing leaves no journal. Each line is on the disk before the change it describes.
/// </summary>
public sealed class RunJournalFile : IChangeJournal
{
    private readonly SetupStore _store;
    private readonly string _runId;
    private readonly string _operation;
    private readonly string _startedUtc;
    private readonly Lock _gate = new();
    private bool _started;

    /// <param name="runId">The id the run's record will be written under (the journal lives in that run's folder).</param>
    /// <param name="operation">The record's operation (<c>windows-apply</c>, <c>windows-revert</c>, <c>profile</c>, <c>setup</c>...).</param>
    public RunJournalFile(SetupStore store, string runId, string operation, DateTimeOffset startedUtc)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        _runId = runId;
        _operation = operation;
        _startedUtc = SetupRun.Stamp(startedUtc);
    }

    public string RunId => _runId;

    public void Record(string operationId, string operationName, Activation activation, RecordedChange change)
    {
        lock (_gate)
        {
            if (!_started)
            {
                _store.AppendJournal(_runId, RunJournal.HeaderLine(_runId, _operation, _startedUtc, Environment.ProcessId, ThisProcessStart()));
                _started = true;
            }

            _store.AppendJournal(_runId, RunJournal.EntryLine(operationId, operationName, activation, change));
        }
    }

    private static long ThisProcessStart()
    {
        using var process = Process.GetCurrentProcess();
        return process.StartTime.ToUniversalTime().Ticks;
    }
}
