using Mesura.Core.Setup;
using Mesura.Setup.Windows.Nvidia;

namespace Mesura.Setup.Tests;

/// <summary>0.5.5 holds back the automated NVIDIA driver step (it has not run on a real GPU) without deleting it: the setup's driver step is the guided one unless a developer switches the feature on.</summary>
public sealed class ReleaseWithholdingTests
{
    [Fact]
    public void The_automated_driver_route_is_not_attempted_so_the_guided_route_follows()
    {
        var target = new NvidiaTarget("NVIDIA GeForce RTX 4070", "560.94", "621.01", ["display-driver"], CleanInstall: false);

        var outcome = new NvidiaTools().AutomateDriver(target, @"C:\Program Files\NVCleanstall\NVCleanstall.exe", new NvPreflight("made", true, "done", true), _ => true, _ => { }, CancellationToken.None);

        Assert.False(outcome.Attempted);
        Assert.False(outcome.InstallStarted);
    }
}
