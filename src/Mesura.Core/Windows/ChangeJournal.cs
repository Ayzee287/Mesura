namespace Mesura.Core.Windows;

/// <summary>
/// Where the engine puts each change's before-image <b>before</b> it makes the change (0.8, crash-safe runs). A run's history record is written
/// only when the run ends; until then this journal is the only lasting trace of what the run is changing, so a crash, a power cut or a killed
/// process mid-run still leaves enough to find the changes and put them back.
/// <para>
/// <see cref="Record"/> must have the entry on disk when it returns, or throw: the engine then doesn't make that change and fails the operation,
/// putting back what the operation had already changed. A change that is recorded and then not made (the write failed, or the process died
/// first) is harmless: undo restores a setting only while it still holds the value the run wrote, so a setting still at its before-image is left
/// alone.
/// </para>
/// </summary>
public interface IChangeJournal
{
    /// <summary>Keeps one change of an operation before it is made; throws when it can't be kept.</summary>
    void Record(string operationId, string operationName, Activation activation, RecordedChange change);
}

/// <summary>Where a recorded change stands on this PC now, read when an interrupted run is recovered.</summary>
public enum ChangeState
{
    /// <summary>The setting holds the value the run wrote: the change was made and is still there.</summary>
    InPlace,

    /// <summary>The setting holds its before-image: the change was never made, or was already put back.</summary>
    NotMade,

    /// <summary>Neither: changed again since, or it couldn't be read. Kept, so undo can decide with a fresh reading.</summary>
    Other,
}
