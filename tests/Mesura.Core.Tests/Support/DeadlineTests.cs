using Mesura.Core.Support;

namespace Mesura.Core.Tests.Support;

/// <summary>A reading with a time limit (0.8): an answer in time is returned, a late one leaves the caller free and can still be picked up, a failure is reported.</summary>
public sealed class DeadlineTests
{
    [Fact]
    public async Task An_answer_within_the_limit_is_returned()
    {
        var (done, value) = await Deadline.Within(Task.FromResult(42), TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(done);
        Assert.Equal(42, value);
    }

    [Fact]
    public async Task A_reading_that_does_not_answer_in_time_is_left_running_and_can_be_picked_up_later()
    {
        var answer = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        var (done, value) = await Deadline.Within(answer.Task, TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);

        Assert.False(done);
        Assert.Null(value);
        answer.SetResult("late");
        var again = await Deadline.Within(answer.Task, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal((true, "late"), again);
    }

    [Fact]
    public async Task A_reading_that_fails_in_time_reports_its_failure()
    {
        var failed = Task.FromException<int>(new InvalidOperationException("WMI said no"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Deadline.Within(failed, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal("WMI said no", error.Message);
    }

    [Fact]
    public async Task Cancelling_the_wait_stops_waiting_and_a_limit_must_be_positive()
    {
        using var cancel = new CancellationTokenSource();
        var never = new TaskCompletionSource<int>().Task;
        var waiting = Deadline.Within(never, TimeSpan.FromMinutes(5), cancel.Token);
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Deadline.Within(never, TimeSpan.Zero, TestContext.Current.CancellationToken));
    }
}
