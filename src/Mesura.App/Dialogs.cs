using System.IO;
using Mesura.Core.Setup;

namespace Mesura.App;

/// <summary>Recording a run without hiding a failed write, and the one "busy" message.</summary>
public static class Dialogs
{
    /// <summary>
    /// Writes a run to the history. The changes it describes have already happened, so a failed write is reported as
    /// such (they were made, but can't be undone from Activity) rather than as the run failing.
    /// </summary>
    /// <returns>What to tell the user if the record couldn't be written, or <c>null</c>.</returns>
    public static string? TryRecord(Services services, SetupRun run)
    {
        try
        {
            services.Store.WriteRunRecord(run.Id, run.ToJson());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The reason (a file-system message, in English) goes to the session log; the page says what it means.
            services.Log($"The history record {run.Id} couldn't be written: {e.Message}");
            services.NoteSession(run);
            return Loc.Get("Common.RecordFailed");
        }

        services.Recorded(run);
        return null;
    }

    /// <summary>Tells the user Mesura can't quit or restart right now, because a run is changing the PC.</summary>
    public static void Busy() => ConfirmDialog.Tell(Loc.Get("Common.BusyTitle"), Loc.Get("Common.Busy"));
}
