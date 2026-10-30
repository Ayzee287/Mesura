namespace Mesura.Core.Windows;

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
    /// Creates a restore point and returns <c>null</c> on success, or a sentence saying why not (System Protection is off, not
    /// elevated, or Windows made another one in the last 24 hours and skipped this one).
    /// </summary>
    string? Create(string description);

    /// <summary>The restore points on this PC, newest first; empty when none exist or they can't be read (administrator rights).</summary>
    IReadOnlyList<RestorePointInfo> List();
}
