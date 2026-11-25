using System.Text.Json;
using System.Text.Json.Serialization;
using Mesura.Core.Windows;

namespace Mesura.Core.Setup;

/// <summary>
/// The first line of a run's journal: which run it belongs to and which process wrote it, so an unfinished journal can be told from one whose
/// run is still going (the process is alive) when Mesura starts.
/// </summary>
/// <param name="ProcessStartTicks">The writing process's start time (UTC ticks); with the id it tells a live process from a later one that reuses the id.</param>
public sealed record RunJournalHeader(string Kind, int SchemaVersion, string RunId, string Operation, string StartedUtc, int ProcessId, long ProcessStartTicks)
{
    public const string FileKind = "Mesura.RunJournal";
    public const int CurrentSchema = 1;
}

/// <summary>One change of one operation, kept before the change was made.</summary>
public sealed record RunJournalEntry(string OperationId, string Name, Activation Activation, RecordedChange Change);

/// <summary>A journal as read back: its header, every whole entry, and whether the last line was cut off (the process died while writing it).</summary>
public sealed record ParsedRunJournal(RunJournalHeader Header, IReadOnlyList<RunJournalEntry> Entries, bool Torn);

/// <summary>What recovering one journal found: changes in place (kept for undo), never made (dropped), and neither (kept, undo decides).</summary>
public sealed record RunJournalFindings(int InPlace, int NotMade, int Other, int AlreadyRecorded);

/// <summary>
/// The journal of a run in progress (0.8, crash-safe runs): <c>history\&lt;run&gt;\journal.jsonl</c>, one JSON object per line, the header first,
/// then each change before it is made. The run's record replaces it when the run ends. A journal still there when Mesura starts belongs to a run
/// that never finished; <see cref="Recover"/> turns what it holds into an ordinary history record, keeping only what the PC still shows, so Undo
/// in Activity can put it back. Pure: the readings of the PC come in as a function.
/// </summary>
public static class RunJournal
{
    public const string FileName = "journal.jsonl";

    /// <summary>The suffix of the record rebuilt from an interrupted run's journal: <c>&lt;run id&gt;-interrupted</c>.</summary>
    public const string RecoveredSuffix = "-interrupted";

    public static string HeaderLine(string runId, string operation, string startedUtc, int processId, long processStartTicks)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        return JsonSerializer.Serialize(new RunJournalHeader(RunJournalHeader.FileKind, RunJournalHeader.CurrentSchema, runId, operation, startedUtc, processId, processStartTicks),
            RunJournalJsonContext.Default.RunJournalHeader);
    }

    public static string EntryLine(string operationId, string name, Activation activation, RecordedChange change)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentNullException.ThrowIfNull(change);
        return JsonSerializer.Serialize(new RunJournalEntry(operationId, name ?? operationId, activation, change), RunJournalJsonContext.Default.RunJournalEntry);
    }

    /// <summary>
    /// Reads a journal back. A last line cut off by a crash is ignored (and reported as torn): every line before it was flushed whole. Returns
    /// <c>null</c> when the header itself can't be read, so the caller keeps the file rather than guessing what it held. A damaged entry line in the
    /// middle is skipped and also reported as torn.
    /// </summary>
    public static ParsedRunJournal? Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
        if (lines.Count == 0)
        {
            return null;
        }

        RunJournalHeader? header;
        try
        {
            header = JsonSerializer.Deserialize(lines[0], RunJournalJsonContext.Default.RunJournalHeader);
        }
        catch (JsonException)
        {
            return null;
        }

        if (header is null || header.Kind != RunJournalHeader.FileKind || header.SchemaVersion > RunJournalHeader.CurrentSchema || string.IsNullOrWhiteSpace(header.RunId))
        {
            return null;
        }

        var entries = new List<RunJournalEntry>();
        var torn = false;
        foreach (var line in lines.Skip(1))
        {
            try
            {
                if (JsonSerializer.Deserialize(line, RunJournalJsonContext.Default.RunJournalEntry) is { OperationId.Length: > 0, Change: { Target: not null } } entry)
                {
                    entries.Add(entry);
                    continue;
                }
            }
            catch (JsonException)
            {
            }

            torn = true;
        }

        return new ParsedRunJournal(header, entries, torn);
    }

    /// <summary>
    /// The history record for an interrupted run: every journaled change that isn't already in the run's own record (<paramref name="existing"/>: a
    /// crash can come after the record was written and before the journal was removed), read against the PC now through <paramref name="check"/>.
    /// A change that is back at (or never left) its before-image is dropped: there is nothing to undo. One in place is kept, and so is one that is
    /// neither (changed since, or unreadable), because only undo, with a fresh reading, may decide to leave it. Returns <c>null</c> when nothing is
    /// left to undo; the findings say what was found either way.
    /// </summary>
    public static SetupRun? Recover(ParsedRunJournal journal, Func<RecordedChange, ChangeState> check, SetupRun? existing, string recoveredUtc, out RunJournalFindings findings)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(check);
        var recorded = existing?.WindowsSteps.SelectMany(s => s.Changes.Select(c => (s.OperationId, c))).ToHashSet() ?? [];
        int inPlace = 0, notMade = 0, other = 0, already = 0;
        var kept = new List<(RunJournalEntry Entry, ChangeState State)>();
        foreach (var entry in journal.Entries.Distinct())
        {
            if (recorded.Contains((entry.OperationId, entry.Change)))
            {
                already++;
                continue;
            }

            var state = check(entry.Change);
            switch (state)
            {
                case ChangeState.NotMade:
                    notMade++;
                    break;
                case ChangeState.InPlace:
                    inPlace++;
                    kept.Add((entry, state));
                    break;
                default:
                    other++;
                    kept.Add((entry, state));
                    break;
            }
        }

        findings = new RunJournalFindings(inPlace, notMade, other, already);
        if (kept.Count == 0)
        {
            return null;
        }

        var revert = journal.Header.Operation is "windows-revert" or "undo" or "config-restore";
        var steps = kept
            .GroupBy(k => k.Entry.OperationId, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var first = g.First().Entry;
                var found = g.Count(k => k.State == ChangeState.InPlace);
                var detail = $"Interrupted: Mesura stopped before this {(revert ? "undo" : "run")} finished. {found} of {g.Count()} change(s) were found in place"
                    + (found < g.Count() ? "; the others hold another value now or couldn't be read, and undo leaves them alone unless they still hold the value written" : "")
                    + ".";
                return new WindowsOpOutcome(first.OperationId, first.Name, revert ? WindowsOpStatus.Reverted : WindowsOpStatus.Applied, detail, first.Activation,
                    [.. g.Select(k => k.Entry.Change)]);
            })
            .ToList();

        var header = journal.Header;
        return SetupRun.Create(header.RunId + RecoveredSuffix, revert ? "windows-revert" : "windows-apply", null, header.StartedUtc, recoveredUtc, [], [], steps)
            with { Interrupted = true };
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(RunJournalHeader))]
[JsonSerializable(typeof(RunJournalEntry))]
internal sealed partial class RunJournalJsonContext : JsonSerializerContext;
