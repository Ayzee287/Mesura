using System.Diagnostics;
using Mesura.Core.Windows;
using Mesura.Platform.Windows.Gpu;
using Mesura.Setup.Windows;
using Mesura.Setup.Windows.Gpu;
using Mesura.Setup.Windows.Windows;

namespace Mesura.Setup.Tests.Gpu;

/// <summary>
/// The window's isolation from AMD's library, with a REAL child process instead of a stand-in function: a command line that dies with the access violation <c>0xC0000005</c> and one that never finishes are
/// the two ways AMD's DLL can hurt a caller. Either costs a child process: the call returns (a state is never invented), the hung child is ended, and nothing is written. The "command line" is a batch
/// file in a temporary folder that the launch is pointed at; no AMD driver and no GPU is involved.
/// </summary>
[Collection("fake-adlx")]
public sealed class AmdCommandLineProcessTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mesura-cli-process-").FullName;

    public AmdCommandLineProcessTests()
    {
        AmdCommandLineRunner.RunOverride = null;
        AmdCommandLine.PathOverride = null;
        AmdCommandLineRunner.TimeoutOverride = TimeSpan.FromSeconds(3);
    }

    public void Dispose()
    {
        AmdCommandLine.PathOverride = null;
        AmdCommandLineRunner.TimeoutOverride = null;
        Directory.Delete(_dir, recursive: true);
    }

    private string Script(string name, string body)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "@echo off\r\n" + body + "\r\n");
        AmdCommandLine.PathOverride = path;
        return path;
    }

    private static WindowsWriter Writer() => new(new UserContext("S-1-5-21-1-1-1-1001", "PC-owner", ""));

    [Fact]
    public void A_command_line_that_dies_with_an_access_violation_is_an_unreadable_state_not_a_crash_of_the_caller()
    {
        Script("crash.cmd", "echo Fatal error.\r\necho 0xC0000005\r\nexit /b -1073741819");

        var reading = AmdCommandLineRunner.ReadAntiLag();

        Assert.Equal(GpuAvailability.Unreadable, reading!.Availability);
        Assert.Null(reading.Enabled);
        Assert.Null(reading.Supported);
    }

    [Fact]
    public void A_write_whose_command_line_dies_throws_with_the_exit_code_and_changes_nothing_the_caller_can_see()
    {
        Script("crash.cmd", "echo Fatal error.\r\nexit /b -1073741819");

        var error = Assert.Throws<InvalidOperationException>(() => Writer().SetGpuFeature("amd", "antilag", 0, enabled: true));

        Assert.Contains("Fatal error", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_command_line_that_never_finishes_is_ended_when_the_wait_is_over_and_the_call_returns()
    {
        Script("hang.cmd", "ping -n 120 127.0.0.1 >nul");
        var watch = Stopwatch.StartNew();

        var reading = AmdCommandLineRunner.ReadAntiLag();

        watch.Stop();
        Assert.Equal(GpuAvailability.Unreadable, reading!.Availability);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"the call took {watch.Elapsed.TotalSeconds:0} s");
    }

    [Fact]
    public void A_missing_command_line_is_could_not_be_run()
    {
        AmdCommandLine.PathOverride = Path.Combine(_dir, "not-there.cmd");

        Assert.False(AmdCommandLineRunner.Available);
        Assert.Equal(GpuAvailability.Unreadable, AmdCommandLineRunner.ReadAntiLag()!.Availability);
        Assert.Throws<InvalidOperationException>(() => Writer().UndoGpuChange("20261104T000000000Z-gpu-antilag"));
    }
}
