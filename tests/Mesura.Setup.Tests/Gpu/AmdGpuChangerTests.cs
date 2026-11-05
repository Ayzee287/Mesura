using Mesura.Core.GpuControl;
using Mesura.Platform.Tests.Gpu;
using Mesura.Platform.Windows.Gpu.Adlx;
using Mesura.Setup.Windows;
using Mesura.Setup.Windows.Gpu;

namespace Mesura.Setup.Tests.Gpu;

/// <summary>
/// The AMD writer against <see cref="FakeAdlx"/> (native objects with the real ABI): preview writes nothing, the record exists before the write, the result is judged by a NEW session and a driver that
/// answers success but changes nothing is "not confirmed", undo puts back the switch and what the driver changed beside it, Anti-Lag Next is never touched. Synthetic: nothing here touches a real GPU or says
/// anything about AMD's driver.
/// </summary>
[Collection("fake-adlx")]
public sealed class AmdGpuChangerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mesura-gpu-changes-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private SetupStore Store => new(_dir, legacyRoot: null);

    private AmdGpuChanger Changer() => new(Store, static () => FakeAdlx.EntryPoints(), static () => new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero));

    private static FakeGpu Radeon(bool antiLagOn = false, bool chillOn = true, int level = 0)
    {
        var gpu = new FakeGpu { AntiLagLevel = level };
        gpu.Features["antilag"] = new FakeFeature(Enabled: antiLagOn);
        gpu.Features["chill"] = new FakeFeature(Enabled: chillOn);
        return gpu;
    }

    private static AmdChange AntiLag(bool on) => new(GpuFeature.AntiLag, 0, null, on);

    private static void AssertClean()
    {
        Assert.Equal(0, FakeAdlx.LiveReferences);
        Assert.Equal(0, FakeAdlx.ReleaseUnderflows);
        Assert.Equal(0, FakeAdlx.CallsAfterTerminate);
        Assert.Equal(0, FakeAdlx.UnexpectedCalls); // no level setter, no Chill range, nothing the writer has no business with
        Assert.False(FakeAdlx.Initialised);
        Assert.Equal(0, FakeAdlx.ReinitWithoutReload); // the crash seen on the real RX 9070 XT: ADLX initialised again on a DLL that was terminated but not unloaded
        Assert.Equal(0, FakeAdlx.InitializeWhileUnloaded);
        Assert.False(FakeAdlx.LibraryLoaded);
        Assert.Equal(FakeAdlx.LoadCount, FakeAdlx.UnloadCount); // every session unloads the DLL it loaded
    }

    [Fact]
    public void A_preview_writes_nothing_names_what_the_driver_does_beside_it_and_leaves_no_record()
    {
        FakeAdlx.Reset(Radeon());

        var outcome = Changer().Preview(AntiLag(true));

        Assert.NotNull(outcome.Preview);
        Assert.True(outcome.Preview!.CanApply);
        Assert.Contains(outcome.Preview.SideEffects, e => e.Contains("Chill", StringComparison.Ordinal));
        Assert.Equal(new GpuFeatureState(true, false, 0, null), outcome.Preview.Current);
        Assert.Empty(FakeAdlx.Writes);
        Assert.Empty(Store.ListGpuChanges());
        AssertClean();
    }

    [Fact]
    public void An_applied_change_is_recorded_before_the_write_and_confirmed_by_a_second_session()
    {
        FakeAdlx.Reset(Radeon());
        var seenBeforeWrite = new List<GpuChangeStatus>();
        FakeAdlx.BeforeWrite = () => seenBeforeWrite.AddRange(Store.ListGpuChanges().Select(id => GpuChangeRecord.FromJson(Store.ReadGpuChange(id)!)!.Status));

        var outcome = Changer().Apply(AntiLag(true));

        Assert.Equal(GpuChangeStatus.Applied, outcome.Status);
        Assert.Equal([GpuChangeStatus.Written], seenBeforeWrite); // the state from before was on disk when the driver was touched
        Assert.Equal(["antilag=True"], FakeAdlx.Writes);
        Assert.Equal(2, FakeAdlx.InitializeCalls); // the writing session and a new one that reads it again
        Assert.Equal(2, FakeAdlx.LoadCount); // each on its own load of the DLL: a second ADLXInitialize2 on the same load crashed amdadlx64.dll on the real card
        Assert.Equal(2, FakeAdlx.UnloadCount);
        var record = outcome.Record!;
        Assert.Equal(GpuChangeStatus.Applied, GpuChangeRecord.FromJson(Store.ReadGpuChange(record.Id)!)!.Status);
        Assert.Equal(false, record.Before.Enabled);
        Assert.Equal(true, record.After!.Enabled);
        Assert.Equal(true, record.RelatedBefore["Chill"].Enabled);
        Assert.Equal(false, record.RelatedAfter!["Chill"].Enabled); // what the driver did beside it is kept in the record
        AssertClean();
    }

    [Fact]
    public void A_driver_that_says_success_and_changes_nothing_is_not_confirmed_and_never_reported_as_applied()
    {
        var gpu = Radeon();
        gpu.Features["antilag"] = new FakeFeature(Enabled: false, WriteTakes: false);
        FakeAdlx.Reset(gpu);

        var outcome = Changer().Apply(AntiLag(true));

        Assert.Equal(GpuChangeStatus.NotConfirmed, outcome.Status);
        Assert.Contains("Not reported as applied", outcome.Message, StringComparison.Ordinal);
        Assert.Equal(GpuChangeStatus.NotConfirmed, GpuChangeRecord.FromJson(Store.ReadGpuChange(outcome.Record!.Id)!)!.Status);
        AssertClean();
    }

    [Fact]
    public void A_driver_that_refuses_is_failed_and_the_state_stays_as_it_was()
    {
        var gpu = Radeon();
        gpu.Features["antilag"] = new FakeFeature(Enabled: false, WriteResult: AdlxResult.Fail);
        FakeAdlx.Reset(gpu);

        var outcome = Changer().Apply(AntiLag(true));

        Assert.Equal(GpuChangeStatus.Failed, outcome.Status);
        Assert.Equal(false, outcome.Record!.After!.Enabled);
        AssertClean();
    }

    [Fact]
    public void Anti_Lag_Next_being_selected_refuses_the_change_and_nothing_is_written_or_recorded()
    {
        FakeAdlx.Reset(Radeon(level: 1));

        var outcome = Changer().Apply(AntiLag(true));

        Assert.Equal(GpuChangeStatus.NotWritten, outcome.Status);
        Assert.Contains("Anti-Lag Next", outcome.Message, StringComparison.Ordinal);
        Assert.Empty(FakeAdlx.Writes);
        Assert.Empty(Store.ListGpuChanges());
        AssertClean();
    }

    [Fact]
    public void A_switch_already_in_the_requested_state_is_left_alone()
    {
        FakeAdlx.Reset(Radeon(antiLagOn: true));

        var outcome = Changer().Apply(AntiLag(true));

        Assert.Equal(GpuChangeStatus.NotWritten, outcome.Status);
        Assert.Contains("Already", outcome.Message, StringComparison.Ordinal);
        Assert.Empty(FakeAdlx.Writes);
        AssertClean();
    }

    [Fact]
    public void Undo_puts_back_the_switch_and_the_chill_the_driver_turned_off_and_a_new_session_confirms_it()
    {
        FakeAdlx.Reset(Radeon());
        var applied = Changer().Apply(AntiLag(true));
        Assert.Equal(GpuChangeStatus.Applied, applied.Status);

        var undone = Changer().Revert(applied.Record!.Id, apply: true);

        Assert.Equal(GpuChangeStatus.Reverted, undone.Status);
        Assert.Equal(["antilag=True", "antilag=False", "chill=True"], FakeAdlx.Writes); // Chill is put back by Mesura because this driver did not
        Assert.Equal(GpuChangeStatus.Reverted, GpuChangeRecord.FromJson(Store.ReadGpuChange(applied.Record.Id)!)!.Status);
        Assert.Equal(true, undone.Record!.RelatedAfter!["Chill"].Enabled);
        Assert.Equal(false, undone.Record.After!.Enabled);
        AssertClean();
    }

    [Fact]
    public void Undo_does_not_write_chill_when_the_driver_already_restored_it()
    {
        FakeAdlx.Reset(Radeon());
        FakeAdlx.DriverRestoresChill = true;
        var applied = Changer().Apply(AntiLag(true));

        var undone = Changer().Revert(applied.Record!.Id, apply: true);

        Assert.Equal(GpuChangeStatus.Reverted, undone.Status);
        Assert.Equal(["antilag=True", "antilag=False"], FakeAdlx.Writes);
        AssertClean();
    }

    [Fact]
    public void Undo_without_apply_only_says_what_it_would_do()
    {
        FakeAdlx.Reset(Radeon());
        var applied = Changer().Apply(AntiLag(true));
        var writes = FakeAdlx.Writes.Count;

        var plan = Changer().Revert(applied.Record!.Id, apply: false);

        Assert.Equal(GpuChangeStatus.NotWritten, plan.Status);
        Assert.Contains("turn AntiLag off", plan.Message, StringComparison.Ordinal);
        Assert.Contains("put Chill back on", plan.Message, StringComparison.Ordinal);
        Assert.Equal(writes, FakeAdlx.Writes.Count);
        AssertClean();
    }

    [Fact]
    public void Undo_of_a_change_whose_gpu_is_gone_says_so_and_writes_nothing()
    {
        FakeAdlx.Reset(Radeon());
        var applied = Changer().Apply(AntiLag(true));
        var moved = applied.Record! with { GpuUniqueId = 9999 };
        Store.WriteGpuChange(moved.Id, moved.ToJson());
        var writes = FakeAdlx.Writes.Count;

        var outcome = Changer().Revert(moved.Id, apply: true);

        Assert.Equal(GpuChangeStatus.Failed, outcome.Status);
        Assert.Contains("not present", outcome.Message, StringComparison.Ordinal);
        Assert.Equal(writes, FakeAdlx.Writes.Count);
        AssertClean();
    }

    [Fact]
    public void Smart_access_memory_that_the_driver_takes_but_that_needs_a_restart_is_not_confirmed()
    {
        var gpu = Radeon();
        gpu.Features["sam"] = new FakeFeature(Enabled: false, WriteResult: AdlxResult.ResetNeeded, WriteTakes: false);
        FakeAdlx.Reset(gpu);

        var outcome = Changer().Apply(new AmdChange(GpuFeature.SmartAccessMemory, 0, null, true));

        Assert.Equal(GpuChangeStatus.RestartNeeded, outcome.Status);
        Assert.Contains("restart", outcome.Message, StringComparison.OrdinalIgnoreCase);
        AssertClean();
    }

    [Fact]
    public void Smart_access_memory_the_driver_says_is_unsupported_points_at_the_bios_and_writes_nothing()
    {
        var gpu = Radeon();
        gpu.Features["sam"] = new FakeFeature(Supported: false);
        FakeAdlx.Reset(gpu);

        var outcome = Changer().Apply(new AmdChange(GpuFeature.SmartAccessMemory, 0, null, true));

        Assert.Equal(GpuChangeStatus.NotWritten, outcome.Status);
        Assert.Contains("BIOS", outcome.Message, StringComparison.Ordinal);
        Assert.Empty(FakeAdlx.Writes);
        AssertClean();
    }

    [Fact]
    public void Freesync_on_a_display_that_does_not_offer_it_says_to_check_the_monitor_menu_and_writes_nothing()
    {
        FakeAdlx.Reset(Radeon());
        FakeAdlx.Displays[0].Features["freesync"] = new FakeFeature(Supported: false);

        var outcome = Changer().Apply(new AmdChange(GpuFeature.FreeSync, 0, 0, true));

        Assert.Equal(GpuChangeStatus.NotWritten, outcome.Status);
        Assert.Contains("monitor", outcome.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(FakeAdlx.Writes);
        AssertClean();
    }

    [Fact]
    public void Freesync_on_one_display_is_written_confirmed_and_undone_for_that_display_only()
    {
        FakeAdlx.Reset(Radeon());
        FakeAdlx.Displays[0].Features["freesync"] = new FakeFeature(Enabled: false);

        var applied = Changer().Apply(new AmdChange(GpuFeature.FreeSync, 0, 0, true));
        Assert.Equal(GpuChangeStatus.Applied, applied.Status);
        Assert.Equal("Test Monitor 27", applied.Record!.DisplayName);

        var undone = Changer().Revert(applied.Record.Id, apply: true);

        Assert.Equal(GpuChangeStatus.Reverted, undone.Status);
        Assert.Equal(["freesync=True", "freesync=False"], FakeAdlx.Writes);
        AssertClean();
    }

    [Fact]
    public void Without_adlx_a_preview_and_an_apply_say_so()
    {
        var changer = new AmdGpuChanger(Store, static () => null, null);

        Assert.Equal(GpuChangeStatus.Failed, changer.Preview(AntiLag(true)).Status);
        Assert.Equal(GpuChangeStatus.Failed, changer.Apply(AntiLag(true)).Status);
    }
}
