using Mesura.Core.Windows;
using Mesura.Platform.Windows.Windows;
using Mesura.Setup.Windows;
using Mesura.Setup.Windows.Windows;

namespace Mesura.Cli.Setup;

/// <summary>
/// Before a command that changes the PC or lists the history: turns runs that never finished (a journal left by a crash, a power cut or a closed
/// console) into history records, so they can be undone (0.8). It reads the PC and writes history files only; it never changes a setting.
/// </summary>
internal static class RecoveryCli
{
    public static void Run(SystemStateReader reader, SetupStore? store = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var configurator = new WindowsConfigurator(reader, new WindowsWriter(reader.User, reader.Tools as SystemTools));
        var report = RunRecovery.Recover(store ?? new SetupStore(), configurator.Check, RunRecovery.ProcessIsAlive, () => DateTimeOffset.UtcNow);
        foreach (var run in report.Recovered)
        {
            var f = run.Findings;
            Console.Error.WriteLine(run.Record is { } record
                ? $"Mesura stopped before run {run.RunId} finished. {f.InPlace} change(s) are still in place"
                  + (f.Other > 0 ? $", {f.Other} can't be told" : "") + (f.NotMade > 0 ? $", {f.NotMade} were never made" : "")
                  + $"; recorded as {record.Id}. Undo with 'mesura windows revert --run {record.Id} --apply'."
                : $"Mesura stopped before run {run.RunId} finished; none of its changes is in place, so there is nothing to undo.");
        }

        foreach (var problem in report.Problems)
        {
            Console.Error.WriteLine(problem);
        }
    }
}
