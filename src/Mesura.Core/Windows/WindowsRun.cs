namespace Mesura.Core.Windows;

/// <summary>Which kind of setting a recorded change touched.</summary>
public enum ChangeKind
{
    Registry,
    Service,
    Feature,
    PowerScheme,

    /// <summary>A power scheme the run installed (from Windows' template); undoing it deletes the scheme.</summary>
    PowerSchemeCreated,

    /// <summary>A power setting's AC value in a scheme (<see cref="RecordedChange.Scheme"/>, subgroup in Path, setting in Name).</summary>
    PowerSetting,

    /// <summary>A Windows scheduled task turned on or off (its full path in Path; <c>Enabled</c>/<c>Disabled</c>).</summary>
    Task,

    /// <summary>A global TCP/IP setting (its netsh name in Name; netsh's words, such as <c>normal</c> or <c>enabled</c>).</summary>
    TcpGlobal,

    /// <summary>A setting changed through a Windows tool (<see cref="SystemSetting"/>: kind in Path, name in Name, its allowed words in Before/After).</summary>
    SystemSetting,

    /// <summary>
    /// A packaged app removed for the user (package name in Name, full name in Path). Undoing it registers the app again
    /// from Windows' staged copy; where none is left, the Store has to reinstall it.
    /// </summary>
    Appx,
}

/// <summary>
/// One setting a run changed, with its exact value before and after, so a revert restores the before-image instead of
/// an assumed default. <see cref="Before"/> is <c>null</c> when a registry value didn't exist (a revert deletes it).
/// For a service it is the <c>Start</c> number, for a feature or a scheduled task <c>Enabled</c>/<c>Disabled</c>, for
/// power the scheme GUID.
/// <see cref="RegistryKind"/> is the before value's type and <see cref="WrittenKind"/> the type written;
/// <see cref="KeyCreated"/> says the run created the registry key, so undoing it removes the key if it is left empty.
/// </summary>
public sealed record RecordedChange(
    ChangeKind Kind,
    string Target,
    string? Hive,
    string? Path,
    string? Name,
    RegistryKind? RegistryKind,
    string? Before,
    string? After,
    RegistryKind? WrittenKind = null,
    bool KeyCreated = false,
    string? UserSid = null,
    string? Scheme = null)
{
    /// <summary>Whether restoring this change needs elevation, by the same rule applying it did.</summary>
    public bool NeedsElevation => Kind switch
    {
        ChangeKind.Registry => RegistrySetting.NeedsElevationFor(Hive ?? "", Path ?? ""),
        ChangeKind.PowerScheme => false,
        _ => true,
    };
}

/// <summary>What happened to one operation in an apply or revert run.</summary>
public enum WindowsOpStatus
{
    Applied,
    AlreadyApplied,
    Inapplicable,
    NeedsElevation,

    /// <summary>Per-user settings whose user couldn't be established, or a revert for a different user: not touched.</summary>
    Blocked,
    Failed,
    Unknown,
    Manual,
    Observation,
    Reverted,

    /// <summary>A revert left the setting alone because it changed again since the run (the user's newer choice wins).</summary>
    RevertSkipped,
}

/// <summary>The result of one operation, with the changes it made.</summary>
public sealed record WindowsOpOutcome(
    string OperationId,
    string Name,
    WindowsOpStatus Status,
    string Detail,
    Activation Activation,
    IReadOnlyList<RecordedChange> Changes)
{
    public bool Ok => Status is not (WindowsOpStatus.Failed or WindowsOpStatus.NeedsElevation or WindowsOpStatus.Unknown or WindowsOpStatus.Blocked);
}
