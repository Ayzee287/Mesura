using System.Text;
using Mesura.Core.Setup;

namespace Mesura.Setup.Windows;

/// <summary>A run journal found in the history: the run it belongs to and its text.</summary>
public sealed record StoredRunJournal(string RunId, string Text);

/// <summary>
/// The run journals (0.8, crash-safe runs) and the durable writes the history depends on. A journal line is appended and flushed to the disk
/// before the change it describes is made; a record or kept file is written to a temporary file, flushed, then moved over the old one, so a power
/// cut leaves either the old file or the new one, never half of one.
/// </summary>
public sealed partial class SetupStore
{
    /// <summary>
    /// Appends one line to a run's journal (<c>history\&lt;run&gt;\journal.jsonl</c>) and has Windows put it on the disk before returning (write-through
    /// and a flush of the file's buffers), so the line survives a crash or a power cut that comes right after.
    /// </summary>
    public void AppendJournal(string runId, string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.Contains('\n', StringComparison.Ordinal))
        {
            throw new ArgumentException("A journal entry is one line.", nameof(line));
        }

        var path = Path.Combine(RunFolder(runId), RunJournal.FileName);
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(line + "\n");
#pragma warning disable RS0030 // The setup component's one file writer: its own run journal (class remarks).
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
#pragma warning restore RS0030
    }

    /// <summary>The journals in the Mesura history (runs that started and haven't been recorded yet, or never finished), oldest first.</summary>
    public IReadOnlyList<StoredRunJournal> ReadJournals()
    {
        if (!Directory.Exists(HistoryFolder))
        {
            return [];
        }

        var found = new List<StoredRunJournal>();
        foreach (var folder in Directory.EnumerateDirectories(HistoryFolder).Order(StringComparer.Ordinal))
        {
            var path = Path.Combine(folder, RunJournal.FileName);
            if (File.Exists(path) && Path.GetFileName(folder) is { Length: > 0 } runId)
            {
                found.Add(new StoredRunJournal(runId, File.ReadAllText(path)));
            }
        }

        return found;
    }

    /// <summary>Removes a run's journal once its record holds what the journal held (or it held nothing still in place); nothing when there is none.</summary>
    public void DeleteJournal(string runId)
    {
        var path = Path.Combine(HistoryFolder, PlainName(runId), RunJournal.FileName);
#pragma warning disable RS0030 // The setup component's one file writer: its own run journal (class remarks).
        File.Delete(path);
#pragma warning restore RS0030
    }

    /// <summary>Whether a run's journal is still there.</summary>
    public bool HasJournal(string runId) => File.Exists(Path.Combine(HistoryFolder, PlainName(runId), RunJournal.FileName));

    /// <summary>
    /// Replaces a file whole without ever leaving half of it: the bytes go to a temporary file beside it, which is flushed to the disk and then
    /// moved over the old file. A failure leaves the old file as it was and removes the temporary one.
    /// </summary>
    private static void WriteDurably(string path, byte[] content)
    {
        var temp = path + TempSuffix;
#pragma warning disable RS0030 // The setup component's one file writer: its own history and progress files (class remarks).
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }

            throw;
        }
#pragma warning restore RS0030
    }
}
