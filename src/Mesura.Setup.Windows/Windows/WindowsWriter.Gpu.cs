using Mesura.Core.GpuControl;
using Mesura.Core.Windows;
using Mesura.Setup.Windows.Gpu;

namespace Mesura.Setup.Windows.Windows;

public sealed partial class WindowsWriter
{
    /// <summary>The AMD writer the GPU switches go through in this process; set in tests with one over a fake driver. Without it the command line itself writes directly and every other host (the window) asks the command line.</summary>
    internal Func<AmdGpuChanger>? GpuChanger { get; init; }

    private bool WritesInProcess => GpuChanger is not null || Mesura.Platform.Windows.Gpu.AmdCommandLine.IsCommandLine;

    private AmdGpuChanger Changer() => GpuChanger?.Invoke() ?? new AmdGpuChanger(new SetupStore());

    /// <summary>
    /// Turns the original AMD Anti-Lag on or off through <see cref="AmdGpuChanger"/>, which has proven the four steps on a real RX 9070 XT (record first, write, an independent session reads it,
    /// rollback). Anything but a confirmed result throws, after putting back whatever the driver may have changed, so the engine records no change it can't stand behind.
    /// </summary>
    public GpuWriteResult SetGpuFeature(string vendor, string feature, int gpuIndex, bool enabled)
    {
        if (!GpuSetting.IsAllowed(vendor, feature))
        {
            throw new ArgumentException($"'{vendor} {feature}' is not a GPU switch Mesura writes.", nameof(feature));
        }

        if (!WritesInProcess)
        {
            // The window never calls AMD's library in its own process: the command line writes and the change is read back and undone through it (AmdCommandLineRunner).
            return AmdCommandLineRunner.SetAntiLag(gpuIndex, enabled);
        }

        var changer = Changer();
        var outcome = changer.Apply(new AmdChange(Mesura.Core.GpuControl.GpuFeature.AntiLag, gpuIndex, null, enabled));
        if (outcome.Status == GpuChangeStatus.Applied && outcome.Record is { } record)
        {
            return new GpuWriteResult(record.Id, outcome.Message);
        }

        var putBack = string.Empty;
        if (outcome.Record is { } kept && outcome.Status != GpuChangeStatus.NotWritten)
        {
            var undo = changer.Revert(kept.Id, apply: true);
            putBack = undo.Status == GpuChangeStatus.Reverted ? " What the driver changed was put back." : $" Putting it back did not finish ({undo.Message}); mesura gpu amd undo {kept.Id} --apply tries again.";
        }

        throw new InvalidOperationException($"{outcome.Message}{putBack}");
    }

    public void UndoGpuChange(string changeId)
    {
        if (!WritesInProcess)
        {
            AmdCommandLineRunner.UndoChange(changeId);
            return;
        }

        var outcome = Changer().Revert(changeId, apply: true);
        if (outcome.Status is not (GpuChangeStatus.Reverted or GpuChangeStatus.NotWritten))
        {
            throw new InvalidOperationException($"The GPU change {changeId} was not put back: {outcome.Message}");
        }
    }
}
