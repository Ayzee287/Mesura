namespace Mesura.Core.Config;

/// <summary>A category of data a port deliberately never reads, shown in the export preview so exclusions are visible.</summary>
public sealed record ConfigExclusion(string Name, string Detail);

/// <summary>One config file a port reads/writes: an id the settings reference, its on-disk format, and a human note.</summary>
public sealed record ConfigSource(string Id, Format Format, string Note);

/// <summary>
/// One application's transfer definition: the files it uses, its allowlist of settings and the categories it
/// deliberately excludes. This is the data the UI and engine work from; adding a setting is a data change here.
/// </summary>
public sealed record ConfigPortDefinition(
    string Id,
    string AppName,
    IReadOnlyList<ConfigSource> Sources,
    IReadOnlyList<ConfigSetting> Settings,
    IReadOnlyList<ConfigExclusion> Exclusions);

/// <summary>The on-disk format of a config file, which picks the matching <see cref="IConfigDocument"/>.</summary>
public enum Format
{
    Vdf,
    Json,
    Prefs,
}

/// <summary>
/// The pure export and import logic, independent of the file system: it reads from and writes to an
/// <see cref="IConfigDocument"/> the caller supplies. Locating the real files, deciding whether an account's config
/// already exists, and confirming the app is closed are the Platform layer's job and reach this code as the document
/// and the <paramref name="scopeSatisfied"/> predicate.
/// </summary>
public static class ConfigEngine
{
    /// <summary>A single-file port's document, for the common case and for tests.</summary>
    public static AppConfigBundle Export(ConfigPortDefinition port, IConfigDocument doc) =>
        Export(port, _ => doc);

    /// <summary>
    /// Reads each allowlisted setting into a portable bundle. <paramref name="documentFor"/> returns the editable
    /// document for a source id, or <c>null</c> if that file is not present on this machine (its settings are then
    /// reported as not present, which is not an error).
    /// </summary>
    public static AppConfigBundle Export(ConfigPortDefinition port, Func<string, IConfigDocument?> documentFor)
    {
        ArgumentNullException.ThrowIfNull(port);
        ArgumentNullException.ThrowIfNull(documentFor);

        var docs = new Dictionary<string, IConfigDocument?>(StringComparer.Ordinal);
        IConfigDocument? Doc(string source) => docs.TryGetValue(source, out var d) ? d : docs[source] = documentFor(source);

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var preview = new List<ConfigPreviewItem>();
        foreach (var setting in port.Settings)
        {
            var captured = Doc(setting.Source)?.TryGet(setting.Path, setting.Subtree);
            if (captured is not null && setting.Subtree)
            {
                captured = SensitiveValues.Strip(captured); // a block never carries a plugin's keys or tokens
            }

            if (captured is not null)
            {
                values[setting.Id] = captured;
                preview.Add(new ConfigPreviewItem(setting.Id, setting.DisplayName, CaptureStatus.Captured, setting.Note));
            }
            else
            {
                preview.Add(new ConfigPreviewItem(setting.Id, setting.DisplayName, CaptureStatus.NotPresent,
                    "Not set on this machine; nothing to carry."));
            }
        }

        foreach (var exclusion in port.Exclusions)
        {
            preview.Add(new ConfigPreviewItem($"{port.Id}.excluded.{exclusion.Name}", exclusion.Name, CaptureStatus.Excluded, exclusion.Detail));
        }

        return new AppConfigBundle(port.Id, port.AppName, values, preview);
    }

    /// <summary>
    /// Writes a bundle's values into <paramref name="doc"/>, one allowlisted key at a time. A setting whose scope is
    /// not yet satisfied (e.g. an account preference before that account's config exists) is skipped, not forced.
    /// </summary>
    public static ConfigApplyResult Import(
        ConfigPortDefinition port,
        IConfigDocument doc,
        AppConfigBundle bundle,
        Func<ConfigScope, bool> scopeSatisfied) =>
        Import(port, _ => doc, bundle, scopeSatisfied);

    /// <summary>
    /// Writes a bundle's values into the destination documents, one allowlisted key at a time. A setting whose scope
    /// is not yet satisfied, or whose destination file is absent (<paramref name="documentFor"/> returns <c>null</c>),
    /// is skipped, never forced. A setting that already holds the value is left alone and isn't reported as applied,
    /// so <see cref="ConfigApplyResult.Applied"/> lists only what changes. A block (<see cref="ConfigSetting.Subtree"/>)
    /// is written without secrets, keeping the destination's own (<see cref="SensitiveValues"/>). The caller writes
    /// each modified document back after this returns.
    /// </summary>
    public static ConfigApplyResult Import(
        ConfigPortDefinition port,
        Func<string, IConfigDocument?> documentFor,
        AppConfigBundle bundle,
        Func<ConfigScope, bool> scopeSatisfied)
    {
        ArgumentNullException.ThrowIfNull(port);
        ArgumentNullException.ThrowIfNull(documentFor);
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(scopeSatisfied);

        var docs = new Dictionary<string, IConfigDocument?>(StringComparer.Ordinal);
        IConfigDocument? Doc(string source) => docs.TryGetValue(source, out var d) ? d : docs[source] = documentFor(source);

        var applied = new List<string>();
        var notApplicable = new List<string>();
        var unavailable = new List<string>();
        var byId = port.Settings.ToDictionary(s => s.Id, StringComparer.Ordinal);

        foreach (var (id, value) in bundle.Values)
        {
            if (!byId.TryGetValue(id, out var setting))
            {
                continue; // a value with no matching allowlist entry is ignored, never blindly written
            }

            if (!scopeSatisfied(setting.Scope))
            {
                notApplicable.Add(id);
                continue;
            }

            if (Doc(setting.Source) is not { } d)
            {
                unavailable.Add(id);
                continue;
            }

            var current = d.TryGet(setting.Path, setting.Subtree);
            if (setting.Subtree && current is not null
                && string.Equals(SensitiveValues.Strip(current), SensitiveValues.Strip(value), StringComparison.Ordinal))
            {
                continue; // the same block apart from secrets, which stay the destination's: nothing to write
            }

            var wanted = setting.Subtree ? SensitiveValues.Merge(value, current) : value;
            if (string.Equals(current, wanted, StringComparison.Ordinal))
            {
                continue; // already so: nothing to write, nothing to report as changed
            }

            if (d.Set(setting.Path, wanted))
            {
                applied.Add(id);
            }
            else
            {
                unavailable.Add(id);
            }
        }

        return new ConfigApplyResult(port.Id, applied, notApplicable, unavailable);
    }
}
