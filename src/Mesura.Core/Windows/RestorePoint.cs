namespace Mesura.Core.Windows;

/// <summary>Why no restore point was made.</summary>
public enum RestoreFailure
{
    None,

    /// <summary>System Protection is off for the system drive (or by policy).</summary>
    ProtectionOff,

    /// <summary>This Windows has no working System Restore: the WMI provider or the service is missing or fails to load (stripped images).</summary>
    Unavailable,

    /// <summary>Not elevated, or Windows refused the caller.</summary>
    AccessDenied,

    /// <summary>Windows allows one automatic point per 24 hours and skipped this one.</summary>
    Skipped,

    /// <summary>Anything else; the technical text has Windows' message.</summary>
    Other,
}

/// <summary>The outcome of asking for a restore point. <see cref="Technical"/> is Windows' own message or code, English, for the technical details.</summary>
public sealed record RestoreResult(RestoreFailure Failure, string Technical = "")
{
    public bool Made => Failure == RestoreFailure.None;

    /// <summary>An English sentence for the command line; <c>null</c> when a point was made.</summary>
    public string? Sentence => Failure switch
    {
        RestoreFailure.None => null,
        RestoreFailure.ProtectionOff => "System Protection is turned off for this PC, so Windows can't make a restore point.",
        RestoreFailure.Unavailable => "System Restore isn't available on this Windows (its component is missing or fails to load)." + Suffix,
        RestoreFailure.AccessDenied => "Windows refused: making a restore point needs an elevated session." + Suffix,
        RestoreFailure.Skipped => "Windows skipped the restore point (it makes one automatic point per 24 hours); Undo in Activity still covers this run.",
        _ => "Windows couldn't make a restore point." + Suffix,
    };

    private string Suffix => Technical.Length > 0 ? " (" + Technical + ")" : "";
}

/// <summary>A System Restore point as Windows lists it.</summary>
public sealed record RestorePointInfo(uint Sequence, string Description, DateTimeOffset Created);

/// <summary>
/// Windows System Restore points, the safety net beneath Mesura's own Undo: Mesura makes one before it changes settings for
/// the whole PC, so even a change its record can't put back (a driver, an app removal, something done outside Mesura)
/// can be rolled back from Windows' own recovery tools. The setup component implements it.
/// </summary>
public interface IRestorePoints
{
    /// <summary>
    /// Creates a restore point. The result says whether a point exists afterwards and, when it doesn't, which of the known reasons applies
    /// (the window words each in the user's language) with Windows' own message kept as technical text.
    /// </summary>
    RestoreResult TryCreate(string description);

    /// <summary>The same as <see cref="TryCreate"/> for the command line: <c>null</c> on success, an English sentence otherwise.</summary>
    string? Create(string description) => TryCreate(description).Sentence;

    /// <summary>The restore points on this PC, newest first; empty when none exist or they can't be read (administrator rights).</summary>
    IReadOnlyList<RestorePointInfo> List();
}
