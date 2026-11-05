using Mesura.Platform.Windows.Gpu;

namespace Mesura.Platform.Tests.Gpu;

/// <summary>
/// The NVAPI reader against <see cref="FakeNvapi"/>, which checks structure versions like the real library, records every interface id asked for and counts sessions. Synthetic: nothing here talks to NVIDIA's
/// driver. (The layouts and ids were also confirmed once by the C++ probe on a real RTX 3080 on 2026-11-03, which is not a test that runs.)
/// </summary>
[Collection("fake-nvapi")]
public sealed class NvidiaReaderTests
{
    private static NvidiaReport Read() => NvidiaGpuReader.Read(FakeNvapi.EntryPoints());

    private static void AssertClean()
    {
        Assert.Equal(1, FakeNvapi.UnloadCalls);
        Assert.Equal(FakeNvapi.SessionsOpened, FakeNvapi.SessionsDestroyed); // every driver-settings session that was opened was closed
        Assert.Equal(0, FakeNvapi.CallsAfterUnload);
        Assert.False(FakeNvapi.Initialised);
    }

    [Fact]
    public void The_driver_the_gpu_and_each_display_with_its_variable_refresh_state_are_read_and_tied_to_the_windows_output()
    {
        FakeNvapi.Reset();
        FakeNvapi.Displays[0] = new FakeNvDisplay(0x80061088, Flags: (1u << 2) | (1u << 6), VrrFlags: 2 | 4, Gdi: @"\\.\DISPLAY1"); // possible and requested, not enabled

        var report = Read();

        Assert.Equal(NvidiaAvailability.Available, report.Availability);
        Assert.Equal(("610.88", "r610_85"), (report.DriverVersion, report.Branch));
        var gpu = Assert.Single(report.Gpus);
        Assert.Equal("NVIDIA GeForce RTX 3080", gpu.Name);
        var display = Assert.Single(gpu.Displays);
        Assert.Equal(@"\\.\DISPLAY1", display.GdiName);
        Assert.Equal((true, true, false), (display.Active, display.Connected, display.Mst));
        Assert.Equal((true, true, false, false), (display.VrrPossible, display.VrrRequested, display.VrrEnabled, display.InVrrMode));
        Assert.Null(display.Problem);
        AssertClean();
    }

    [Fact]
    public void A_display_the_driver_says_cannot_do_variable_refresh_is_false_not_unknown()
    {
        FakeNvapi.Reset();

        var display = Assert.Single(Assert.Single(Read().Gpus).Displays);

        Assert.False(display.VrrPossible); // the call answered: "not possible" is an answer
        Assert.False(display.VrrEnabled);
        AssertClean();
    }

    [Fact]
    public void A_display_whose_vrr_call_fails_is_unknown_with_the_status_and_the_others_are_still_read()
    {
        FakeNvapi.Reset();
        FakeNvapi.Displays[0] = new FakeNvDisplay(0x80061088, Flags: 1 << 2, VrrFlags: 0, VrrStatus: -5, Gdi: @"\\.\DISPLAY1");
        FakeNvapi.Displays.Add(new FakeNvDisplay(0x80061099, Flags: 1 << 2, VrrFlags: 2 | 1, Gdi: @"\\.\DISPLAY2"));

        var displays = Assert.Single(Read().Gpus).Displays;

        Assert.Null(displays[0].VrrPossible);
        Assert.Contains("INVALID_ARGUMENT", displays[0].Problem, StringComparison.Ordinal);
        Assert.Equal((true, true), (displays[1].VrrPossible, displays[1].VrrEnabled));
        AssertClean();
    }

    [Fact]
    public void An_empty_base_profile_reads_every_key_setting_as_not_set_the_driver_default_and_nothing_is_invented()
    {
        FakeNvapi.Reset();

        var report = Read();

        var baseProfile = Assert.Single(report.Profiles, p => p.Kind == "base");
        Assert.NotEmpty(baseProfile.Settings);
        Assert.All(baseProfile.Settings, s =>
        {
            Assert.False(s.Set);
            Assert.Null(s.Value);
            Assert.Equal("SETTING_NOT_FOUND", s.Status);
        });
        Assert.True(Assert.Single(report.Profiles, p => p.Kind == "currentGlobal").SameAsBase);
        AssertClean();
    }

    [Fact]
    public void A_setting_the_base_profile_holds_is_read_with_its_value_and_where_it_comes_from()
    {
        FakeNvapi.Reset();
        FakeNvapi.BaseSettings[0x00A879CF] = new FakeNvSetting(0x08416747);            // V-Sync forced off
        FakeNvapi.BaseSettings[0x1057EB71] = new FakeNvSetting(1, Location: 2, Predefined: true);

        var settings = Assert.Single(Read().Profiles, p => p.Kind == "base").Settings;

        var vsync = Assert.Single(settings, s => s.Id == "0x00A879CF");
        Assert.Equal((true, 0x08416747u, "Base Profile"), (vsync.Set, vsync.Value, vsync.Source));
        var power = Assert.Single(settings, s => s.Label == "Power management mode");
        Assert.Equal("Base Profile (predefined)", power.Source);
        Assert.Contains(settings, s => s.Id == "0x10835002" && s.Set == false); // the others are still "not set"
        AssertClean();
    }

    [Fact]
    public void A_global_profile_that_is_not_the_base_profile_is_read_too()
    {
        FakeNvapi.Reset();
        FakeNvapi.GlobalIsBase = false;

        var global = Assert.Single(Read().Profiles, p => p.Kind == "currentGlobal");

        Assert.False(global.SameAsBase);
        Assert.NotEmpty(global.Settings);
        AssertClean();
    }

    [Fact]
    public void A_driver_that_will_not_initialise_is_not_started_and_nothing_is_unloaded_that_never_started()
    {
        FakeNvapi.Reset();
        FakeNvapi.InitializeStatus = -4;

        var report = Read();

        Assert.Equal(NvidiaAvailability.NotStarted, report.Availability);
        Assert.Contains("API_NOT_INITIALIZED", report.Failure, StringComparison.Ordinal);
        Assert.Equal(0, FakeNvapi.UnloadCalls);
    }

    [Fact]
    public void A_function_the_driver_lacks_is_unknown_for_that_part_only()
    {
        FakeNvapi.Reset();
        FakeNvapi.Missing.Add(0xdf8fda57); // NvAPI_Disp_GetVRRInfo: drivers before 525

        var display = Assert.Single(Assert.Single(Read().Gpus).Displays);

        Assert.Null(display.VrrPossible);
        Assert.Contains("not in this driver", display.Problem, StringComparison.Ordinal);
        AssertClean();
    }

    [Fact]
    public void Only_read_functions_are_ever_asked_for_no_setter_no_save_no_restore()
    {
        FakeNvapi.Reset();

        Read();

        // Initialize, Unload, driver version, GPUs, GPU name, connected ids, VRR info, display id by name, and the DRS create, destroy, load, base, global, get-setting.
        uint[] allowed = [0x0150e828, 0xd22bdd7e, 0x2926aaad, 0xe5ac921f, 0xceee8e9f, 0x0078dba2, 0xdf8fda57, 0xae457190, 0x0694d52e, 0xdad9cff8, 0x375dbd6b, 0xda8466a0, 0x617bff9f, 0x73bf8338];
        Assert.Empty(FakeNvapi.Requested.Except(allowed));
        AssertClean();
    }
}
