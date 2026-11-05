using Mesura.Platform.Windows.Gpu;
using Mesura.Platform.Windows.Gpu.Adlx;

namespace Mesura.Platform.Tests.Gpu;

/// <summary>
/// The ADLX layer against <see cref="FakeAdlx"/>, a stand-in with the real ABI (native objects, native vtables, unmanaged calls). Synthetic: these prove that the code calls the slots it should, takes and gives back
/// every reference, never calls a setter, never touches ADLX after it is terminated, and survives every kind of answer the DLL can give; they say nothing about AMD's DLL.
/// </summary>
[Collection("fake-adlx")]
public sealed class AdlxReaderTests
{
    private static AmdAdlxReport Read(Action<AdlxEntryPoints>? _ = null) => AmdGpuReader.Read(FakeAdlx.EntryPoints());

    private static void AssertClean()
    {
        Assert.Equal(0, FakeAdlx.LiveReferences);
        Assert.Equal(0, FakeAdlx.ReleaseUnderflows);
        Assert.Equal(0, FakeAdlx.CallsAfterTerminate);
        Assert.Equal(0, FakeAdlx.UnexpectedCalls); // no setter, no reset, no slot the reader has no business with
        Assert.False(FakeAdlx.Initialised);
    }

    [Fact]
    public void A_radeon_is_read_through_the_vtables_with_every_value_in_the_slot_it_belongs_to()
    {
        FakeAdlx.Reset();

        var report = Read();

        Assert.Equal(AmdAvailability.Available, report.Availability);
        Assert.Equal("1.5.0.124", report.DllVersion);
        var gpu = Assert.Single(report.Gpus);
        Assert.Equal(("AMD Radeon RX 9070 XT", "1002", "7550", "C0"), (gpu.Name, gpu.VendorId, gpu.DeviceId, gpu.RevisionId));
        Assert.Null(gpu.SubSystemId); // a getter that answers success with no string is unknown, not ""
        Assert.Equal((16304u, "GDDR6", "022.001.002.001", 2, false), (gpu.VramMb, gpu.VramType, gpu.BiosVersion, gpu.GpuType, gpu.External));
        Assert.Equal(new AmdFeature(true, true, null), gpu.AntiLag);
        Assert.Equal(2, gpu.AntiLagLevel);
        Assert.Equal(new AmdFeature(true, false, null), gpu.Chill);
        Assert.Equal(new AmdFeature(false, null, null), gpu.Boost); // not supported: no "enabled" is asked for or invented
        Assert.Equal(new AmdFeature(true, true, null), gpu.SmartAccessMemory);
        Assert.Equal(new AmdTuningSupport(true, true, true, true, true, true, true), gpu.Tuning);
        Assert.Equal(new AmdMetrics(12.5, 2400, 1124, 41.5, 52, 33.25, null, 0, 850), gpu.Metrics); // board power answered NotSupported: unknown, not 41
        Assert.Contains(gpu.Problems, p => p.StartsWith("board power", StringComparison.Ordinal) && p.Contains("NotSupported", StringComparison.Ordinal));
        AssertClean();
    }

    [Fact]
    public void Every_reference_taken_is_given_back_adlx_is_terminated_once_and_last_and_no_setter_is_called()
    {
        FakeAdlx.Reset();

        Read();

        Assert.Equal(1, FakeAdlx.InitializeCalls);
        Assert.Equal(1, FakeAdlx.TerminateCalls);
        Assert.True(FakeAdlx.ObjectsEverCreated > 15); // list, GPU, three services, each feature, the level interface, tuning1, metrics
        Assert.DoesNotContain(FakeAdlx.Log, l => l.StartsWith("UNEXPECTED", StringComparison.Ordinal));
        AssertClean();
    }

    [Theory]
    [InlineData(1, 5, 0, 124, "1.5.0.124")]
    [InlineData(2, 0, 0, 125, "1.5.0.124")] // a newer driver is asked for no more than the version the tables were written for
    [InlineData(1, 4, 0, 121, "1.4.0.121")] // an older one is asked for its own version, which it cannot refuse
    [InlineData(1, 0, 0, 1, "1.0.0.1")]
    public void The_version_asked_for_is_the_lower_of_the_drivers_and_the_one_the_tables_were_written_for(int major, int minor, int release, int build, string expected)
    {
        FakeAdlx.Reset();
        FakeAdlx.DllVersion = FakeAdlx.Version(major, minor, release, build);

        var report = Read();

        Assert.Equal(AmdAvailability.Available, report.Availability);
        Assert.Equal(expected, report.AskedVersion);
        Assert.Equal(expected, new AdlxVersion(FakeAdlx.AskedVersion).ToString());
        AssertClean();
    }

    [Fact]
    public void An_older_driver_without_the_newer_interfaces_gives_unknown_for_them_not_an_error_and_not_a_guess()
    {
        FakeAdlx.Reset();
        FakeAdlx.AntiLag1Available = false;
        FakeAdlx.Tuning1Available = false;

        var gpu = Assert.Single(Read().Gpus);

        Assert.Equal(new AmdFeature(true, true, null), gpu.AntiLag); // the base interface still answers
        Assert.Null(gpu.AntiLagLevel); // the level interface isn't there
        Assert.Equal(new AmdFeature(null, null, "UnknownInterface"), gpu.SmartAccessMemory); // SmartAccess Memory needs the newer tuning interface
        Assert.True(gpu.Tuning is { Auto: true }); // the older tuning interface is enough for the support flags
        AssertClean();
    }

    [Fact]
    public void Displays_are_read_with_freesync_scaling_colour_and_the_gpu_they_are_on_and_the_edid_is_never_asked_for()
    {
        FakeAdlx.Reset();

        var report = Read();

        var display = Assert.Single(report.Displays);
        Assert.Equal(("Test Monitor 27", 2560, 1440, 165.0), (display.Name, display.NativeWidth, display.NativeHeight, display.RefreshHz));
        Assert.Equal(("monitor", "DisplayPort", "progressive"), (display.Type, display.Connector, display.ScanType));
        Assert.Equal(new AmdFeature(true, true, null), display.FreeSync);
        Assert.Equal(new AmdFeature(true, false, null), display.VirtualSuperResolution);
        Assert.Equal(new AmdFeature(false, null, null), display.IntegerScaling); // unsupported: no state is asked for or invented
        Assert.Equal(("full panel", "10 bpc", "RGB 4:4:4 full"), (display.ScalingMode, display.ColorDepth, display.PixelFormat));
        Assert.Equal(report.Gpus[0].UniqueId, display.GpuUniqueId); // the display is tied to its GPU by the GPU's own id
        Assert.Equal("none", report.HybridGraphics);
        Assert.DoesNotContain(FakeAdlx.Log, l => l.Contains("EDID", StringComparison.Ordinal));
        AssertClean();
    }

    [Fact]
    public void A_display_whose_freesync_the_driver_will_not_hand_out_is_unknown_with_the_reason_and_the_rest_is_still_read()
    {
        FakeAdlx.Reset();
        FakeAdlx.Displays[0].Features["freesync"] = new FakeFeature(GetResult: AdlxResult.NotSupported);
        FakeAdlx.Displays[0].Features["vsr"] = new FakeFeature(EnabledResult: AdlxResult.Fail);

        var display = Assert.Single(Read().Displays);

        Assert.Equal(new AmdFeature(null, null, "NotSupported"), display.FreeSync);
        Assert.Equal(new AmdFeature(true, null, "IsEnabled: Fail"), display.VirtualSuperResolution); // a failed answer is unknown, never "off"
        Assert.Equal("full panel", display.ScalingMode);
        AssertClean();
    }

    [Fact]
    public void Failed_display_services_are_one_entry_that_says_so_and_a_name_that_fails_is_unknown()
    {
        FakeAdlx.Reset();
        FakeAdlx.DisplayServicesResult = AdlxResult.NotSupported;

        var failed = Assert.Single(Read().Displays);

        Assert.Equal(-1, failed.Index);
        Assert.Contains(failed.Problems, p => p.Contains("NotSupported", StringComparison.Ordinal));
        AssertClean();

        FakeAdlx.Reset();
        FakeAdlx.Displays[0] = new FakeDisplay { NameResult = AdlxResult.Fail };
        Assert.Null(Assert.Single(Read().Displays).Name);
        AssertClean();
    }

    [Fact]
    public void Vsync_with_its_mode_super_resolution_and_tessellation_are_read_and_a_mode_outside_the_names_is_shown_as_unknown_with_its_number()
    {
        FakeAdlx.Reset(new FakeGpu { VsyncMode = 7, TessellationMode = 1, TessellationLevel = 16 });

        var gpu = Assert.Single(Read().Gpus);

        Assert.Equal(new AmdFeature(true, false, null), gpu.VerticalRefresh);
        Assert.Equal("unknown (7)", gpu.VerticalRefreshMode); // never mapped to the nearest name
        Assert.Equal(new AmdFeature(false, null, null), gpu.RadeonSuperResolution);
        Assert.Null(gpu.SuperResolutionSharpness); // not supported: not asked
        Assert.Equal(new AmdTessellation(true, "use application settings", 16), gpu.Tessellation);
        AssertClean();
    }

    [Fact]
    public void A_driver_that_refuses_to_start_is_reported_as_not_started_and_nothing_is_terminated_that_never_started()
    {
        FakeAdlx.Reset();
        FakeAdlx.InitializeResult = AdlxResult.AdlInitError;

        var report = Read();

        Assert.Equal(AmdAvailability.NotStarted, report.Availability);
        Assert.Empty(report.Gpus);
        Assert.Contains("AdlInitError", report.Failure, StringComparison.Ordinal);
        Assert.Equal(0, FakeAdlx.TerminateCalls);
        AssertClean();
    }

    [Fact]
    public void A_session_unloads_the_dll_after_terminating_it_and_a_failed_start_unloads_it_too()
    {
        FakeAdlx.Reset();
        using (var session = AdlxSession.TryOpen(FakeAdlx.EntryPoints(), out _, out _))
        {
            Assert.NotNull(session);
            Assert.True(FakeAdlx.LibraryLoaded);
        }

        Assert.False(FakeAdlx.LibraryLoaded); // Terminate, then unload (as AMD's own helper does); the next session loads the DLL again
        Assert.Equal(1, FakeAdlx.UnloadCount);

        FakeAdlx.InitializeResult = AdlxResult.AdlInitError;
        Assert.Null(AdlxSession.TryOpen(FakeAdlx.EntryPoints(), out _, out _));
        Assert.False(FakeAdlx.LibraryLoaded);
        Assert.Equal(2, FakeAdlx.UnloadCount);

        // What the real driver did on 2026-11-04: a second initialisation on a DLL that stayed loaded after Terminate. The fake counts it as the crash it was.
        FakeAdlx.Reset();
        var entry = FakeAdlx.EntryPoints();
        using (AdlxSession.TryOpen(entry, out _, out _))
        {
        }

        Assert.Null(AdlxSession.TryOpen(entry, out _, out _)); // the same load cannot serve a second session
        Assert.Equal(1, FakeAdlx.InitializeWhileUnloaded);
    }

    [Fact]
    public void A_driver_without_initialize2_is_started_through_the_older_entry_point()
    {
        FakeAdlx.Reset();

        var report = AmdGpuReader.Read(FakeAdlx.EntryPoints(withInitialize2: false));

        Assert.Equal(AmdAvailability.Available, report.Availability);
        Assert.Single(report.Gpus);
        AssertClean();
    }

    [Fact]
    public void A_failed_gpu_list_still_ends_cleanly_and_says_so()
    {
        FakeAdlx.Reset();
        FakeAdlx.GetGpusResult = AdlxResult.Fail;

        var report = Read();

        Assert.Equal(AmdAvailability.Available, report.Availability);
        Assert.Empty(report.Gpus);
        Assert.Contains("GPU list", report.Failure, StringComparison.Ordinal);
        AssertClean();
    }

    [Fact]
    public void When_the_services_fail_the_gpu_is_still_identified_and_each_missing_part_is_unknown_with_its_reason()
    {
        FakeAdlx.Reset();
        FakeAdlx.ServicesResult = AdlxResult.NotSupported;

        var gpu = Assert.Single(Read().Gpus);

        Assert.Equal("AMD Radeon RX 9070 XT", gpu.Name);
        Assert.Equal("services unavailable", gpu.AntiLag.Problem);
        Assert.Null(gpu.Tuning);
        Assert.Null(gpu.Metrics);
        AssertClean();
    }

    [Fact]
    public void A_gpu_that_cannot_be_obtained_is_reported_and_the_next_one_is_still_read()
    {
        FakeAdlx.Reset(new FakeGpu { AtResult = AdlxResult.GpuInactive }, new FakeGpu { Name = "AMD Radeon(TM) Graphics", DeviceId = "164E" });

        var report = Read();

        Assert.Equal(2, report.Gpus.Count);
        Assert.Null(report.Gpus[0].Name);
        Assert.Contains("GpuInactive", Assert.Single(report.Gpus[0].Problems), StringComparison.Ordinal);
        Assert.Equal("AMD Radeon(TM) Graphics", report.Gpus[1].Name);
        AssertClean();
    }

    [Fact]
    public void A_getter_that_fails_is_unknown_and_never_the_default_value()
    {
        var gpu0 = new FakeGpu { NameResult = AdlxResult.Fail };
        gpu0.Features["chill"] = new(SupportedResult: AdlxResult.Fail);
        gpu0.Features["sharpening"] = new(Supported: true, EnabledResult: AdlxResult.NotActive);
        gpu0.Features["enhancedsync"] = new(GetResult: AdlxResult.NotSupported);
        FakeAdlx.Reset(gpu0);

        var gpu = Assert.Single(Read().Gpus);

        Assert.Null(gpu.Name);
        Assert.Contains(gpu.Problems, p => p.StartsWith("Name:", StringComparison.Ordinal));
        Assert.Equal(new AmdFeature(null, null, "IsSupported: Fail"), gpu.Chill);
        Assert.Equal(new AmdFeature(true, null, "IsEnabled: NotActive"), gpu.ImageSharpening);
        Assert.Equal(new AmdFeature(null, null, "NotSupported"), gpu.EnhancedSync);
        AssertClean();
    }

    [Fact]
    public void An_object_used_after_the_session_closed_is_refused_in_managed_code_and_adlx_is_never_called_after_terminate()
    {
        FakeAdlx.Reset();
        var session = AdlxSession.TryOpen(FakeAdlx.EntryPoints(), out _, out _)!;
        var list = new AdlxSystem(session).Gpus().Value!;
        Assert.Equal(1u, list.Size);

        session.Dispose();

        Assert.Throws<ObjectDisposedException>(() => list.Size);
        Assert.Throws<ObjectDisposedException>(() => new AdlxSystem(session).Gpus());
        list.Dispose(); // a second release of what the session already released is a no-op, not a call into ADLX
        Assert.Equal(1, FakeAdlx.TerminateCalls);
        AssertClean();
    }

    [Fact]
    public void Objects_the_caller_forgot_to_release_are_released_by_the_session_and_closing_twice_is_harmless()
    {
        FakeAdlx.Reset();
        var session = AdlxSession.TryOpen(FakeAdlx.EntryPoints(), out _, out _)!;
        var system = new AdlxSystem(session);
        _ = system.Gpus();
        _ = system.Services3D();
        Assert.True(FakeAdlx.LiveReferences >= 2);

        session.Dispose();
        session.Dispose();

        Assert.Equal(1, FakeAdlx.TerminateCalls);
        AssertClean();
    }

    [Fact]
    public void An_object_released_by_its_owner_is_released_once_and_a_double_dispose_does_not_underflow_the_count()
    {
        FakeAdlx.Reset();
        using var session = AdlxSession.TryOpen(FakeAdlx.EntryPoints(), out _, out _)!;
        var list = new AdlxSystem(session).Gpus().Value!;

        list.Dispose();
        list.Dispose();

        Assert.Equal(0, FakeAdlx.LiveReferences);
        Assert.Equal(0, FakeAdlx.ReleaseUnderflows);
    }

    [Fact]
    public void The_version_is_four_sixteen_bit_parts_in_one_number()
    {
        var v = AdlxVersion.Make(1, 5, 0, 124);

        Assert.Equal((1, 5, 0, 124), (v.Major, v.Minor, v.Release, v.Build));
        Assert.Equal("1.5.0.124", v.ToString());
        Assert.Equal(0x0001_0005_0000_007CUL, v.Full);
        Assert.True(AdlxVersion.Make(2, 0, 0, 125).Full > v.Full);
    }

    [Theory]
    [InlineData((int)AdlxResult.Ok, true)]
    [InlineData((int)AdlxResult.AlreadyEnabled, true)]
    [InlineData((int)AdlxResult.AlreadyInitialized, true)]
    [InlineData((int)AdlxResult.Fail, false)]
    [InlineData((int)AdlxResult.NotSupported, false)]
    [InlineData((int)AdlxResult.Terminated, false)]
    public void Success_is_what_adlxs_own_macro_says(int result, bool succeeded) => Assert.Equal(succeeded, ((AdlxResult)result).Succeeded());

    [Fact]
    public void The_result_codes_have_the_values_of_the_adlx_enumeration()
    {
        // ADLX_RESULT in ADLXDefines.h counts up from ADLX_OK = 0; these are the ones Mesura names.
        Assert.Equal(3, (int)AdlxResult.Fail);
        Assert.Equal(5, (int)AdlxResult.BadVersion);
        Assert.Equal(6, (int)AdlxResult.UnknownInterface);
        Assert.Equal(12, (int)AdlxResult.NotSupported);
        Assert.Equal(18, (int)AdlxResult.ResetNeeded);
    }

    [Fact]
    public void On_a_pc_without_the_amd_dll_the_real_reader_says_so_and_never_throws()
    {
        var report = AmdGpuReader.Read();

        var present = File.Exists(Path.Combine(Environment.SystemDirectory, "amdadlx64.dll"));
        if (!present)
        {
            Assert.Equal(AmdAvailability.NoDriver, report.Availability);
            Assert.Empty(report.Gpus);
        }

        Assert.NotNull(report);
    }
}
