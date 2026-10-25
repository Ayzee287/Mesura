namespace Mesura.Core.Config;

/// <summary>Where a setting applies, which decides when it is safe to write on the destination machine.</summary>
public enum ConfigScope
{
    /// <summary>A plain client/app preference, safe to write as soon as the app's config file exists.</summary>
    Application,

    /// <summary>A per-Windows-user preference; written under the destination user, never carrying the source user's identity.</summary>
    User,

    /// <summary>
    /// Scoped to a signed-in app account (e.g. Steam's per-account local config). Applied only after that account's
    /// own configuration exists on the destination, with the app closed. Never includes login or authentication state.
    /// </summary>
    Account,
}

/// <summary>How reliably a setting can be carried to another installation.</summary>
public enum TransferConfidence
{
    /// <summary>A well-understood key with a stable meaning across versions.</summary>
    Reliable,

    /// <summary>Transferable, but version- or layout-dependent; applied if present, skipped otherwise.</summary>
    BestEffort,
}

/// <summary>
/// One allowlisted, transferable setting in an application's configuration. The allowlist is data: new settings are
/// added here, not in code paths. A setting names exactly where it lives, so export reads only it and import writes
/// only it (every other key in the file is preserved).
/// </summary>
/// <param name="Id">Stable identifier, namespaced by app, e.g. <c>steam.overlay.enabled</c>.</param>
/// <param name="DisplayName">Short human label for the preview.</param>
/// <param name="Path">
/// The location inside the file: a <c>/</c>-separated VDF path, a JSON pointer-style <c>/</c> path, or a <c>prefs</c>
/// line key, depending on the port's file format.
/// </param>
/// <param name="Scope">When it is safe to write (<see cref="ConfigScope"/>).</param>
/// <param name="Confidence">How reliably it transfers.</param>
/// <param name="Note">Why it is included or any caveat, shown in the preview.</param>
/// <param name="Subtree">
/// When <c>true</c>, the path is a whole container (a JSON object or array, e.g. Vencord's <c>plugins</c>) captured and
/// restored as one unit. Default <c>false</c> keeps capture to a single scalar leaf, so no large or unvetted subtree
/// is ever grabbed by accident; a subtree is only ever taken where the allowlist opts in explicitly.
/// </param>
/// <param name="Source">
/// Which of the port's files this setting lives in (<see cref="ConfigPortDefinition.Sources"/>). Default <c>"main"</c>
/// suits a single-file app; Steam uses <c>"install"</c> (config.vdf) and <c>"account"</c> (localconfig.vdf).
/// </param>
public sealed record ConfigSetting(
    string Id,
    string DisplayName,
    string Path,
    ConfigScope Scope,
    TransferConfidence Confidence,
    string Note,
    bool Subtree = false,
    string Source = "main");

/// <summary>The outcome of reading one setting during export.</summary>
public enum CaptureStatus
{
    /// <summary>Read and included in the bundle.</summary>
    Captured,

    /// <summary>The key was not present on this machine, so there is nothing to carry (not an error).</summary>
    NotPresent,

    /// <summary>Deliberately excluded (e.g. a value that mixes with sensitive state); never read.</summary>
    Excluded,
}

/// <summary>One line of an export preview: what a setting is and whether it was captured, absent or excluded.</summary>
/// <param name="Setting">The allowlisted setting, or <c>null</c> for an excluded-by-rule file/key.</param>
public sealed record ConfigPreviewItem(string Id, string DisplayName, CaptureStatus Status, string Detail);

/// <summary>
/// One application's exported settings: id/value pairs for the settings that were captured, plus the preview that
/// records what was captured, what was absent and what was excluded. The bundle holds no file paths, usernames,
/// credentials or account identifiers; it is portable to a fresh install under a different Windows user.
/// </summary>
public sealed record AppConfigBundle(
    string PortId,
    string AppName,
    IReadOnlyDictionary<string, string> Values,
    IReadOnlyList<ConfigPreviewItem> Preview)
{
    /// <summary>The settings read from this machine, by <see cref="ConfigSetting.Id"/>; empty when nothing was present.</summary>
    public static AppConfigBundle Empty(string portId, string appName) =>
        new(portId, appName, new Dictionary<string, string>(), []);
}

/// <summary>What happened when a bundle was applied to the destination file.</summary>
/// <param name="Applied">Settings whose value changed (one already holding the value isn't listed).</param>
/// <param name="SkippedNotApplicable">Settings whose scope was not yet satisfied (e.g. account config absent).</param>
/// <param name="SkippedUnavailable">Settings whose destination target could not be reached.</param>
public sealed record ConfigApplyResult(
    string PortId,
    IReadOnlyList<string> Applied,
    IReadOnlyList<string> SkippedNotApplicable,
    IReadOnlyList<string> SkippedUnavailable)
{
    public bool AnythingApplied => Applied.Count > 0;
}
