using Mesura.Core.GpuControl;
using Mesura.Core.Windows;
using Mesura.Platform.Tests.Gpu;
using Mesura.Platform.Windows.Gpu.Adlx;
using Mesura.Setup.Windows;
using Mesura.Setup.Windows.Gpu;
using Mesura.Setup.Windows.Windows;

namespace Mesura.Setup.Tests.Gpu;

/// <summary>
/// The Windows writer's GPU switch (0.6) against the fake ADLX with the real ABI: the engine's writer goes through the AMD writer whose four steps (record first, write, a new session reads it, rollback)
/// were proven on a real RX 9070 XT; here the hand-over is covered: a confirmed change returns the id of the writer's record, anything else throws after putting back what the driver changed, and the undo
/// by that id restores the switch and Chill. Synthetic: nothing here touches a real GPU.
/// </summary>
[Collection("fake-adlx")]
public sealed class WindowsWriterGpuTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mesura-writer-gpu-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private SetupStore Store => new(_dir, legacyRoot: null);

    private WindowsWriter Writer() => new(new UserContext("S-1-5-21-1-1-1-1001", "PC-owner", ""))
    {
        GpuChanger = () => new AmdGpuChanger(Store, static () => FakeAdlx.EntryPoints(), static () => new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero)),
    };

    private static FakeGpu Radeon(bool antiLagOn = false, bool chillOn = true, FakeFeature? antiLag = null)
    {
        var gpu = new FakeGpu { AntiLagLevel = 0 };
        gpu.Features["antilag"] = antiLag ?? new FakeFeature(Enabled: antiLagOn);
        gpu.Features["chill"] = new FakeFeature(Enabled: chillOn);
        return gpu;
    }

    private static void AssertClean()
    {
        Assert.Equal(0, FakeAdlx.LiveReferences);
        Assert.Equal(0, FakeAdlx.ReinitWithoutReload);
        Assert.Equal(FakeAdlx.LoadCount, FakeAdlx.UnloadCount);
    }

    [Fact]
    public void A_confirmed_change_returns_the_id_of_the_writers_record_and_the_undo_by_that_id_restores_the_switch_and_chill()
    {
        FakeAdlx.Reset(Radeon());

        var result = Writer().SetGpuFeature("amd", "antilag", 0, enabled: true);

        Assert.EndsWith("-gpu-antilag", result.ChangeId, StringComparison.Ordinal);
        Assert.Equal(GpuChangeStatus.Applied, GpuChangeRecord.FromJson(Store.ReadGpuChange(result.ChangeId)!)!.Status);
        Assert.Equal(["antilag=True"], FakeAdlx.Writes);

        Writer().UndoGpuChange(result.ChangeId);

        Assert.Equal(["antilag=True", "antilag=False", "chill=True"], FakeAdlx.Writes);   // Chill is put back because this driver did not
        Assert.Equal(GpuChangeStatus.Reverted, GpuChangeRecord.FromJson(Store.ReadGpuChange(result.ChangeId)!)!.Status);
        AssertClean();
    }

    [Fact]
    public void A_driver_that_answers_success_and_changes_nothing_makes_the_writer_throw_and_leaves_the_state_as_it_was()
    {
        FakeAdlx.Reset(Radeon(antiLag: new FakeFeature(Enabled: false, WriteTakes: false)));

        var error = Assert.Throws<InvalidOperationException>(() => Writer().SetGpuFeature("amd", "antilag", 0, enabled: true));

        Assert.Contains("Not reported as applied", error.Message, StringComparison.Ordinal);
        AssertClean();
    }

    [Fact]
    public void A_driver_that_refuses_makes_the_writer_throw_with_the_drivers_word()
    {
        FakeAdlx.Reset(Radeon(antiLag: new FakeFeature(Enabled: false, WriteResult: AdlxResult.Fail)));

        var error = Assert.Throws<InvalidOperationException>(() => Writer().SetGpuFeature("amd", "antilag", 0, enabled: true));

        Assert.Contains("refused", error.Message, StringComparison.Ordinal);
        AssertClean();
    }

    [Fact]
    public void Anti_Lag_Next_is_refused_before_anything_is_written()
    {
        var gpu = new FakeGpu { AntiLagLevel = 1 };
        gpu.Features["antilag"] = new FakeFeature(Enabled: false);
        gpu.Features["chill"] = new FakeFeature(Enabled: true);
        FakeAdlx.Reset(gpu);

        var error = Assert.Throws<InvalidOperationException>(() => Writer().SetGpuFeature("amd", "antilag", 0, enabled: true));

        Assert.Contains("Anti-Lag Next", error.Message, StringComparison.Ordinal);
        Assert.Empty(FakeAdlx.Writes);
        AssertClean();
    }

    [Theory]
    [InlineData("amd", "freesync")]
    [InlineData("amd", "sam")]
    [InlineData("nvidia", "antilag")]
    public void Only_the_original_anti_lag_is_a_switch_this_writer_writes(string vendor, string feature)
    {
        FakeAdlx.Reset(Radeon());

        Assert.Throws<ArgumentException>(() => Writer().SetGpuFeature(vendor, feature, 0, enabled: true));

        Assert.Empty(FakeAdlx.Writes);
    }

    [Fact]
    public void An_undo_of_a_change_already_put_back_is_not_an_error()
    {
        FakeAdlx.Reset(Radeon());
        var result = Writer().SetGpuFeature("amd", "antilag", 0, enabled: true);
        Writer().UndoGpuChange(result.ChangeId);

        Writer().UndoGpuChange(result.ChangeId);   // the second time says "nothing to undo"

        Assert.Equal(["antilag=True", "antilag=False", "chill=True"], FakeAdlx.Writes);
        AssertClean();
    }

    [Fact]
    public void An_undo_of_a_record_that_does_not_exist_throws()
    {
        FakeAdlx.Reset(Radeon());

        Assert.Throws<InvalidOperationException>(() => Writer().UndoGpuChange("20261104T000000000Z-gpu-antilag"));
    }
}
