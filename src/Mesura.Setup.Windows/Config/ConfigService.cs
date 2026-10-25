using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Mesura.Core.Config;
using Mesura.Core.Setup;

namespace Mesura.Setup.Windows.Config;

/// <summary>What an export produced for one app: the captured bundle and the preview of included/absent/excluded items.</summary>
public sealed record AppExport(ConfigPortDefinition Port, IReadOnlyList<LocatedConfigFile> Files, AppConfigBundle Bundle);

/// <summary>The whole export: the portable bundle file text and the per-app detail for display.</summary>
public sealed record ExportResult(string BundleJson, IReadOnlyList<AppExport> Apps);

/// <summary>
/// Runs configuration export and import against this machine's real files: it locates each app's files
/// (<see cref="ConfigLocator"/>), reads them, applies the pure <see cref="ConfigEngine"/>, and — for import — writes
/// the edited files back through <see cref="SetupStore"/>, which backs each one up first. It carries no setting
/// knowledge of its own; the allowlists live in <see cref="ConfigCatalog"/>.
/// </summary>
public static class ConfigService
{
    private static IConfigDocument MakeDocument(Format format, string text) => format switch
    {
        Format.Vdf => new VdfConfigDocument(text),
        Format.Json => new JsonConfigDocument(text),
        Format.Prefs => new PrefsConfigDocument(text),
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown config format."),
    };

    /// <summary>Reads the allowlisted settings of each port into one portable bundle file (read-only).</summary>
    /// <param name="createdUtc">The time stamp to stamp into the bundle (the caller supplies the clock).</param>
    public static ExportResult Export(IEnumerable<ConfigPortDefinition> ports, string createdUtc)
    {
        ArgumentNullException.ThrowIfNull(ports);
        var apps = new List<AppExport>();
        foreach (var port in ports)
        {
            var files = ConfigLocator.Locate(port);
            var bySource = files.ToDictionary(f => f.SourceId, StringComparer.Ordinal);
            var cache = new Dictionary<string, IConfigDocument?>(StringComparer.Ordinal);

            IConfigDocument? DocumentFor(string sourceId)
            {
                if (cache.TryGetValue(sourceId, out var cached))
                {
                    return cached;
                }

                IConfigDocument? doc = null;
                if (bySource.TryGetValue(sourceId, out var file) && file.Exists)
                {
                    try
                    {
                        doc = MakeDocument(file.Format, File.ReadAllText(file.Path));
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or System.Text.Json.JsonException)
                    {
                        doc = null; // unreadable or malformed: its settings report as not present, never a crash
                    }
                }

                cache[sourceId] = doc;
                return doc;
            }

            apps.Add(new AppExport(port, files, ConfigEngine.Export(port, DocumentFor)));
        }

        var json = ConfigBundleFile.Save(apps.Select(a => a.Bundle), createdUtc);
        return new ExportResult(json, apps);
    }

    /// <summary>The desktop process whose config a port writes; import skips the app while it runs.</summary>
    public static string ProcessName(string portId) => portId switch
    {
        "steam" => "steam",
        "brave" => "brave",
        "vencord" or "discord" => "Discord",
        "spotify" => "Spotify",
        _ => portId,
    };


    /// <summary>
    /// Applies a bundle's settings for each port to this machine's files. With <paramref name="store"/> null it is a
    /// preview: nothing is written and the outcome reports what would be applied. Otherwise a port is skipped while its
    /// app runs (the app would overwrite the change on exit), and only files whose text actually changes are replaced,
    /// each backed up first under <paramref name="runId"/> — so re-running an import writes nothing.
    /// </summary>
    /// <param name="locate">Finds a port's files; tests pass temporary ones. Defaults to <see cref="ConfigLocator.Locate"/>.</param>
    /// <param name="isRunning">Whether a process runs; tests pass a stand-in. Defaults to <see cref="IsRunning"/>.</param>
    public static IReadOnlyList<ConfigImportOutcome> Import(
        ConfigBundleFile bundleFile,
        IEnumerable<ConfigPortDefinition> ports,
        SetupStore? store,
        string? runId,
        Func<ConfigPortDefinition, IReadOnlyList<LocatedConfigFile>>? locate = null,
        Func<string, bool>? isRunning = null)
    {
        ArgumentNullException.ThrowIfNull(bundleFile);
        ArgumentNullException.ThrowIfNull(ports);
        if (store is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        }

        var outcomes = new List<ConfigImportOutcome>();
        foreach (var port in ports)
        {
            var bundle = bundleFile.ForApp(port.Id);
            if (bundle is not null)
            {
                outcomes.Add(ImportOne(port, bundle, store, runId, locate ?? ConfigLocator.Locate, isRunning ?? IsRunning));
            }
        }

        return outcomes;
    }

    private static ConfigImportOutcome ImportOne(
        ConfigPortDefinition port,
        AppConfigBundle bundle,
        SetupStore? store,
        string? runId,
        Func<ConfigPortDefinition, IReadOnlyList<LocatedConfigFile>> locate,
        Func<string, bool> isRunning)
    {
        ConfigImportOutcome Outcome(ConfigImportStatus status, ConfigApplyResult? result, IReadOnlyList<WrittenConfigFile> written, string detail = "") =>
            new(port.Id, port.AppName, status, result?.Applied ?? [], result?.SkippedNotApplicable ?? [],
                result?.SkippedUnavailable ?? [], written, detail);

        if (store is not null && isRunning(ProcessName(port.Id)))
        {
            return Outcome(ConfigImportStatus.SkippedAppRunning, null, [], $"{port.AppName} is running; close it and re-run.");
        }

        var located = locate(port);
        var files = located.ToDictionary(f => f.SourceId, StringComparer.Ordinal);
        var docs = new Dictionary<string, (LocatedConfigFile File, string Baseline, IConfigDocument Doc)>(StringComparer.Ordinal);
        var unreadable = new List<string>();

        IConfigDocument? DocumentFor(string sourceId)
        {
            if (docs.TryGetValue(sourceId, out var open))
            {
                return open.Doc;
            }

            if (!files.TryGetValue(sourceId, out var file) || !file.Exists)
            {
                return null; // destination file absent: those settings are skipped, not forced
            }

            try
            {
                var doc = MakeDocument(file.Format, File.ReadAllText(file.Path));
                // The baseline is the still-unedited document's own output, so a difference later means a value
                // changed, not merely that the app formats its file differently from how this document writes it.
                docs[sourceId] = (file, doc.ToText(), doc);
                return doc;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or System.Text.Json.JsonException)
            {
                // The file is there but can't be read or parsed: a real failure, not "the app hasn't created it yet".
                unreadable.Add($"the {sourceId} file couldn't be read ({e.Message})");
                return null;
            }
        }

        // Steam account settings wait until that account's localconfig.vdf exists (it has signed in once).
        var accountReady = port.Id != "steam" || located.Any(f => f.SourceId == "account" && f.Exists);
        ConfigApplyResult result;
        try
        {
            result = ConfigEngine.Import(port, DocumentFor, bundle, scope => scope != ConfigScope.Account || accountReady);
        }
        catch (Exception e) when (e is FormatException or System.Text.Json.JsonException or ArgumentException)
        {
            // A value in the bundle the file format can't take (a hand-edited bundle): nothing is written for this app.
            return Outcome(ConfigImportStatus.Failed, null, [], $"A value in the settings file can't be written: {e.Message}; nothing was written for {port.AppName}.");
        }

        if (unreadable.Count > 0)
        {
            // Nothing is written for this app when one of its files can't be read, so it is never half-imported.
            return Outcome(ConfigImportStatus.Failed, result with { Applied = [], SkippedUnavailable = [] }, [],
                $"{string.Join("; ", unreadable)}; nothing was written for {port.AppName}.");
        }

        // Only files where a value changed are written: a re-run, or a file already holding every value, is left alone.
        var changed = docs.Where(d => d.Value.Doc.ToText() != d.Value.Baseline).ToList();
        if (changed.Count == 0)
        {
            return Outcome(ConfigImportStatus.Unchanged, result, []);
        }

        if (store is null)
        {
            return Outcome(ConfigImportStatus.Applied, result, [.. changed.Select(d => new WrittenConfigFile(d.Key, ""))], "Preview: nothing written.");
        }

        var written = new List<WrittenConfigFile>();
        foreach (var (sourceId, (file, _, doc)) in changed)
        {
            var backupName = $"{port.Id}-{sourceId}{Path.GetExtension(file.Path)}";
            try
            {
                var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(doc.ToText());
                store.ReplaceAppConfig(file.Path, bytes, runId!, backupName);
                written.Add(new WrittenConfigFile(sourceId, backupName, Fingerprint(bytes)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return Outcome(ConfigImportStatus.Failed, result, written, $"Writing the {sourceId} file failed: {e.Message}");
            }
        }

        return Outcome(ConfigImportStatus.Applied, result, written);
    }

    /// <summary>
    /// Puts back the files an import replaced, from that run's backups (the undo of a config import). A file is put
    /// back only while it is still exactly what the run wrote (its recorded fingerprint): one the app or the user has
    /// changed since, one now resolving to another file (another Steam account's), or one from a record too old to have
    /// a fingerprint is left as it is (<see cref="ConfigImportStatus.ChangedSince"/>, also when another of the app's
    /// files was put back: then its <see cref="ConfigImportOutcome.Written"/> names those), so later changes are never lost.
    /// Each file is itself backed up under <paramref name="runId"/> before it is replaced, so a restore can be undone
    /// the same way. The app is skipped while it runs; a file already equal to its backup is left alone.
    /// </summary>
    /// <param name="importRunId">The config import (or profile) run whose backups are restored.</param>
    /// <param name="written">That run's written files for this port (<see cref="ConfigImportOutcome.Written"/>).</param>
    public static ConfigImportOutcome RestoreBackups(
        ConfigPortDefinition port,
        string importRunId,
        IReadOnlyList<WrittenConfigFile> written,
        SetupStore store,
        string runId,
        Func<ConfigPortDefinition, IReadOnlyList<LocatedConfigFile>>? locate = null,
        Func<string, bool>? isRunning = null)
    {
        ArgumentNullException.ThrowIfNull(port);
        ArgumentNullException.ThrowIfNull(written);
        ArgumentNullException.ThrowIfNull(store);
        ConfigImportOutcome Outcome(ConfigImportStatus status, IReadOnlyList<WrittenConfigFile> files, string detail = "") =>
            new(port.Id, port.AppName, status, [], [], [], files, detail);

        if ((isRunning ?? IsRunning)(ProcessName(port.Id)))
        {
            return Outcome(ConfigImportStatus.SkippedAppRunning, [], $"{port.AppName} is running; close it and try again.");
        }

        var files = (locate ?? ConfigLocator.Locate)(port).ToDictionary(f => f.SourceId, StringComparer.Ordinal);
        var plan = new List<(LocatedConfigFile File, byte[] Content, string BackupName)>();
        var changedSince = new List<string>();
        foreach (var w in written)
        {
            byte[]? backup;
            byte[] current;
            try
            {
                backup = store.ReadBackupBytes(importRunId, w.BackupName);
                if (backup is null)
                {
                    return Outcome(ConfigImportStatus.Failed, [], $"The backup of the {w.SourceId} file from {importRunId} is missing; nothing was restored.");
                }

                if (!files.TryGetValue(w.SourceId, out var located) || !located.Exists)
                {
                    return Outcome(ConfigImportStatus.Failed, [], $"{port.AppName}'s {w.SourceId} file isn't on this PC any more; nothing was restored.");
                }

                current = File.ReadAllBytes(located.Path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return Outcome(ConfigImportStatus.Failed, [], $"The {w.SourceId} file or its backup couldn't be read ({e.Message}); nothing was restored.");
            }

            var file = files[w.SourceId];
            // Compared and put back byte for byte, so the file is exactly as it was (encoding, BOM, line endings).
            if (current.AsSpan().SequenceEqual(backup))
            {
                continue; // already as it was before the run
            }

            if (w.Sha256 is null || !string.Equals(Fingerprint(current), w.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                changedSince.Add(w.SourceId);
                continue;
            }

            plan.Add((file, backup, w.BackupName));
        }

        var left = changedSince.Count == 0 ? ""
            : $"The {string.Join(" and ", changedSince)} file changed after {importRunId} (or that run is too old to check), so it was left as it is; its earlier copy is in that run's backups.";
        if (plan.Count == 0)
        {
            return changedSince.Count > 0 ? Outcome(ConfigImportStatus.ChangedSince, [], left) : Outcome(ConfigImportStatus.Unchanged, []);
        }

        var restored = new List<WrittenConfigFile>();
        foreach (var (file, content, backupName) in plan)
        {
            try
            {
                store.ReplaceAppConfig(file.Path, content, runId, backupName);
                restored.Add(new WrittenConfigFile(file.SourceId, backupName, Fingerprint(content)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return Outcome(ConfigImportStatus.Failed, restored, $"Restoring the {file.SourceId} file failed: {e.Message}");
            }
        }

        return changedSince.Count > 0
            ? Outcome(ConfigImportStatus.ChangedSince, restored, $"Partly restored from before {importRunId}. {left}")
            : Outcome(ConfigImportStatus.Applied, restored, $"Restored from before {importRunId}.");
    }

    /// <summary>The SHA-256 of a file's bytes, in hex, as recorded for each file a run writes.</summary>
    public static string Fingerprint(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    /// <summary>Whether a process with this base name (no extension) is currently running, so writes can wait for it to close.</summary>
    public static bool IsRunning(string processBaseName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processBaseName);
        try
        {
            return Process.GetProcessesByName(processBaseName).Length > 0;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
