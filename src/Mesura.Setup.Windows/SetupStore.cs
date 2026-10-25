using System.Text;
using System.Text.RegularExpressions;
using Mesura.Core.Support;

namespace Mesura.Setup.Windows;

/// <summary>
/// The only class in the setup component that writes files. It writes these: an application's own config file
/// (replaced atomically, after the original is backed up), and, under its root, the backups, the run history (with the
/// files a run keeps, such as an NVIDIA profile backup), the unfinished setup's progress and Mesura's own app preferences.
/// The root defaults to <c>%LOCALAPPDATA%\Mesura\Setup</c>; tests pass a temporary folder.
/// <para>
/// Runs recorded before the rename to Mesura (D022) live under <c>%LOCALAPPDATA%\FpsUtility\Setup</c>. They are read
/// where they are — listed, shown, their backups found — and never moved, copied or deleted, so nothing is duplicated
/// and nothing is lost. Every new run is written under the Mesura root.
/// </para>
/// </summary>
public sealed partial class SetupStore
{
    private const string TempSuffix = ".mesura-tmp";

    /// <param name="root">Where new runs go; defaults to <c>%LOCALAPPDATA%\Mesura\Setup</c>.</param>
    /// <param name="legacyRoot">
    /// Where pre-rename runs are read from; defaults to <c>%LOCALAPPDATA%\FpsUtility\Setup</c> when
    /// <paramref name="root"/> is the default, and to none otherwise.
    /// </param>
    public SetupStore(string? root = null, string? legacyRoot = null)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Root = Path.GetFullPath(root ?? Path.Combine(localAppData, "Mesura", "Setup"));
        var legacy = legacyRoot ?? (root is null ? Path.Combine(localAppData, LegacyIdentity.CodeName, "Setup") : null);
        LegacyRoot = legacy is null ? null : Path.GetFullPath(legacy);
    }

    public string Root { get; }

    /// <summary>The pre-rename root that is read but never written, or <c>null</c>.</summary>
    public string? LegacyRoot { get; }

    public string HistoryFolder => Path.Combine(Root, "history");

    private IEnumerable<string> HistoryFolders =>
        LegacyRoot is null ? [HistoryFolder] : [HistoryFolder, Path.Combine(LegacyRoot, "history")];

    /// <summary>
    /// Replaces an application's config file with <paramref name="text"/> (UTF-8, no BOM). The current file is first
    /// copied into the run's backup folder under <paramref name="backupName"/>, then the new text is written to a
    /// temporary file beside it and moved over the original, so the app never sees a half-written file.
    /// </summary>
    /// <returns>The full path of the backup copy.</returns>
    public string ReplaceAppConfig(string path, string text, string runId, string backupName)
    {
        ArgumentNullException.ThrowIfNull(text);
        return ReplaceAppConfig(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text), runId, backupName);
    }

    /// <summary>
    /// Replaces an application's config file with exactly these bytes (a backup put back as it was, BOM and line
    /// endings included), after copying the current file into the run's backup folder, as the text overload does.
    /// </summary>
    /// <returns>The full path of the backup copy.</returns>
    public string ReplaceAppConfig(string path, byte[] content, string runId, string backupName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);

        var backupFolder = RunFolder(runId, "backups");
        var backup = Path.Combine(backupFolder, PlainName(backupName));
        var temp = path + TempSuffix;

#pragma warning disable RS0030 // The setup component's one file writer: a config the user chose to import (class remarks).
        File.Copy(path, backup, overwrite: true);
        File.WriteAllBytes(temp, content);
        File.Move(temp, path, overwrite: true);
#pragma warning restore RS0030
        return backup;
    }

    /// <summary>Writes (or replaces) a run's history record, <c>history\&lt;runId&gt;\run.json</c>.</summary>
    /// <returns>The full path written.</returns>
    public string WriteRunRecord(string runId, string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var path = Path.Combine(RunFolder(runId), "run.json");
#pragma warning disable RS0030 // The setup component's one file writer: its own run history (class remarks).
        File.WriteAllBytes(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(json));
#pragma warning restore RS0030
        return path;
    }

    /// <summary>
    /// The ids of recorded runs, newest first (ids start with a UTC time stamp), from the Mesura history and the
    /// pre-rename one. An id present in both is listed once.
    /// </summary>
    public IReadOnlyList<string> ListRuns() =>
        [.. HistoryFolders
            .Where(Directory.Exists)
            .SelectMany(Directory.EnumerateDirectories)
            .Where(d => File.Exists(Path.Combine(d, "run.json")))
            .Select(Path.GetFileName)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderDescending(StringComparer.Ordinal)];

    /// <summary>The folder holding a run's record and backups (Mesura history first), or <c>null</c> if there is none.</summary>
    public string? FindRunFolder(string runId)
    {
        var name = PlainName(runId);
        return HistoryFolders.Select(h => Path.Combine(h, name)).FirstOrDefault(d => File.Exists(Path.Combine(d, "run.json")));
    }

    /// <summary>The text of a config file a run backed up, or <c>null</c> when the run or the backup isn't there.</summary>
    public string? ReadBackup(string runId, string backupName) => BackupPath(runId, backupName) is { } path ? File.ReadAllText(path) : null;

    /// <summary>The exact bytes of a config file a run backed up, or <c>null</c> when the run or the backup isn't there.</summary>
    public byte[]? ReadBackupBytes(string runId, string backupName) => BackupPath(runId, backupName) is { } path ? File.ReadAllBytes(path) : null;

    private string? BackupPath(string runId, string backupName) =>
        HistoryFolders.Select(h => Path.Combine(h, PlainName(runId), "backups", PlainName(backupName))).FirstOrDefault(File.Exists);

    /// <summary>Mesura's own app preferences (language, start-up), or <c>null</c> before they are first saved.</summary>
    public string? ReadAppSettings()
    {
        var path = Path.Combine(Root, AppSettingsFile);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    /// <summary>Writes Mesura's own app preferences, <c>app-settings.json</c> under the root, replacing them whole.</summary>
    public void WriteAppSettings(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var path = Path.Combine(Root, AppSettingsFile);
        var temp = path + TempSuffix;
#pragma warning disable RS0030 // The setup component's one file writer: Mesura's own preferences (class remarks).
        Directory.CreateDirectory(Root);
        File.WriteAllBytes(temp, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(json));
        File.Move(temp, path, overwrite: true);
#pragma warning restore RS0030
    }

    private const string AppSettingsFile = "app-settings.json";

    private const string PendingSetupFile = "pending-setup.json";

    /// <summary>The unfinished setup (<c>pending-setup.json</c> under the root), or <c>null</c> when there is none.</summary>
    public string? ReadPendingSetup()
    {
        var path = Path.Combine(Root, PendingSetupFile);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    /// <summary>Writes the unfinished setup's progress, replacing it whole (written after every step).</summary>
    public void WritePendingSetup(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var path = Path.Combine(Root, PendingSetupFile);
        var temp = path + TempSuffix;
#pragma warning disable RS0030 // The setup component's one file writer: its own unfinished-setup record (class remarks).
        Directory.CreateDirectory(Root);
        File.WriteAllBytes(temp, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(json));
        File.Move(temp, path, overwrite: true);
#pragma warning restore RS0030
    }

    /// <summary>Removes the unfinished-setup record once every step is finished, or when the user discards the rest.</summary>
    public void ClearPendingSetup()
    {
        var path = Path.Combine(Root, PendingSetupFile);
#pragma warning disable RS0030 // The setup component's one file writer: its own unfinished-setup record (class remarks).
        File.Delete(path);
#pragma warning restore RS0030
    }

    /// <summary>
    /// Keeps a file a run produced in the run's folder, <c>history\&lt;runId&gt;\files\&lt;name&gt;</c> (the NVIDIA profile
    /// backup), so it is there to put back and to read later.
    /// </summary>
    /// <returns>The full path written.</returns>
    public string KeepRunFile(string runId, string name, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var path = Path.Combine(RunFolder(runId, "files"), PlainName(name));
#pragma warning disable RS0030 // The setup component's one file writer: a file its own run produced (class remarks).
        File.WriteAllBytes(path, content);
#pragma warning restore RS0030
        return path;
    }

    /// <summary>The path of a file a run kept, or <c>null</c> when the run or the file isn't there.</summary>
    public string? RunFilePath(string runId, string name) =>
        HistoryFolders.Select(h => Path.Combine(h, PlainName(runId), "files", PlainName(name))).FirstOrDefault(File.Exists);

    /// <summary>The text of a run's record, or <c>null</c> if there is no such run.</summary>
    public string? ReadRunRecord(string runId) =>
        FindRunFolder(runId) is { } folder ? File.ReadAllText(Path.Combine(folder, "run.json")) : null;

    private string RunFolder(string runId, string? sub = null)
    {
        var folder = Path.Combine(HistoryFolder, PlainName(runId));
        if (sub is not null)
        {
            folder = Path.Combine(folder, sub);
        }

#pragma warning disable RS0030 // Folders for the setup component's own history (class remarks).
        Directory.CreateDirectory(folder);
#pragma warning restore RS0030
        return folder;
    }

    /// <summary>One plain file or folder name, so nothing can be written outside the root.</summary>
    private static string PlainName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || !PlainNamePattern().IsMatch(name) || name.Trim('.').Length == 0)
        {
            throw new ArgumentException($"'{name}' is not a plain file name.", nameof(name));
        }

        return name;
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]*\\z", RegexOptions.CultureInvariant)]
    private static partial Regex PlainNamePattern();
}
