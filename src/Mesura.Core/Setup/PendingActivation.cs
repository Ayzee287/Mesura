using System.Globalization;
using Mesura.Core.Windows;

namespace Mesura.Core.Setup;

/// <summary>
/// Which Windows settings Mesura has applied that still wait for something before they are in effect (0.5): a restart, a sign-out or an Explorer restart.
/// A setting whose stored value reads back as set is "set", not "applied", until that has happened; the answer comes from the recorded runs and what
/// happened to the PC since (a restart also signs out and restarts Explorer). Windows' fast startup is not a restart: the time the
/// operating system actually started (not the time the PC was last switched on) is what counts.
/// </summary>
public static class PendingActivation
{
    /// <summary>
    /// The activation each operation still waits for, from <paramref name="runs"/> (any order). A run finished before <paramref name="booted"/> no longer
    /// waits for anything; of the runs since, the latest one to touch an operation decides (a later change that takes effect at once clears the wait).
    /// </summary>
    public static IReadOnlyDictionary<string, Activation> For(IEnumerable<SetupRun> runs, DateTimeOffset booted)
    {
        ArgumentNullException.ThrowIfNull(runs);
        var pending = new Dictionary<string, Activation>(StringComparer.OrdinalIgnoreCase);
        var since = runs
            .Select(run => (Run: run, Finished: DateTimeOffset.TryParse(run.FinishedUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : (DateTimeOffset?)null))
            .Where(x => x.Finished is { } at && at >= booted)
            .OrderBy(x => x.Finished);
        foreach (var (run, _) in since)
        {
            foreach (var step in run.WindowsSteps.Where(s => s.Status is WindowsOpStatus.Applied or WindowsOpStatus.Reverted))
            {
                if (step.Activation == Activation.Immediate)
                {
                    pending.Remove(step.OperationId);
                }
                else
                {
                    pending[step.OperationId] = step.Activation;
                }
            }
        }

        return pending;
    }
}
