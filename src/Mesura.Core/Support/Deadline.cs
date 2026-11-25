namespace Mesura.Core.Support;

/// <summary>
/// Waits for a reading with a time limit (0.8): a Windows query that never answers (a stuck WMI provider, a driver call) must not leave a page
/// "reading" for ever. The reading itself isn't cancelled (most of them can't be): it carries on in the background, and the caller can await the
/// same task again later instead of starting a second one beside it.
/// </summary>
public static class Deadline
{
    /// <summary>How long the window waits for a reading of the PC before it says so.</summary>
    public static readonly TimeSpan Reading = TimeSpan.FromSeconds(30);

    /// <summary>The task's result when it finishes within <paramref name="limit"/>; otherwise <c>Done</c> is false and the task is left running.</summary>
    /// <remarks>A task that fails within the limit rethrows its exception here, as awaiting it would.</remarks>
    public static async Task<(bool Done, T? Value)> Within<T>(Task<T> task, TimeSpan limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (limit <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "A time limit must be positive.");
        }

        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delay = Task.Delay(limit, timer.Token);
        var first = await Task.WhenAny(task, delay).ConfigureAwait(false);
        if (first != task)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return (false, default);
        }

        await timer.CancelAsync().ConfigureAwait(false); // stop the timer: the reading answered
        return (true, await task.ConfigureAwait(false));
    }
}
