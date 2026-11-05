using Mesura.Core.Windows;
using Mesura.Platform.Windows.Gpu;
using Mesura.Platform.Windows.Gpu.Adlx;
using Mesura.Platform.Windows.Windows;

namespace Mesura.Platform.Tests.Gpu;

/// <summary>What the engine is told about Anti-Lag from an ADLX report (0.6): which GPU is asked, what each failure to read is called. The reports come from the fake ADLX with the real ABI.</summary>
[Collection("fake-adlx")]
public sealed class GpuFeatureReaderTests
{
    private static FakeGpu Radeon(string name, bool supported, bool on, int level = 0)
    {
        var gpu = new FakeGpu { Name = name, AntiLagLevel = level };
        gpu.Features["antilag"] = new FakeFeature(Enabled: on, Supported: supported);
        return gpu;
    }

    private static GpuFeatureReading Read(params FakeGpu[] gpus)
    {
        FakeAdlx.Reset(gpus);
        return GpuFeatureReader.FromReport(AmdGpuReader.Read(FakeAdlx.EntryPoints()));
    }

    [Fact]
    public void The_reading_carries_the_switch_the_level_and_the_gpu_index_the_writer_needs()
    {
        var reading = Read(Radeon("AMD Radeon RX 9070 XT", supported: true, on: true));

        Assert.Equal(GpuAvailability.Available, reading.Availability);
        Assert.Equal("AMD Radeon RX 9070 XT", reading.GpuName);
        Assert.Equal(0, reading.GpuIndex);
        Assert.True(reading.Supported);
        Assert.True(reading.Enabled);
        Assert.Equal(0, reading.Level);
    }

    [Fact]
    public void A_gpu_that_offers_anti_lag_is_preferred_over_one_that_does_not()
    {
        var reading = Read(Radeon("AMD Radeon(TM) Graphics", supported: false, on: false), Radeon("AMD Radeon RX 7800 XT", supported: true, on: false));

        Assert.Equal("AMD Radeon RX 7800 XT", reading.GpuName);
        Assert.Equal(1, reading.GpuIndex);
        Assert.True(reading.Supported);
    }

    [Fact]
    public void When_no_gpu_offers_it_the_first_one_answers_so_the_plan_can_say_it_is_not_offered()
    {
        var reading = Read(Radeon("AMD Radeon(TM) Graphics", supported: false, on: false));

        Assert.Equal(GpuAvailability.Available, reading.Availability);
        Assert.False(reading.Supported);
    }

    private sealed class Source(GpuFeatureReading? reading) : ISystemSettingSource
    {
        public int Asked { get; private set; }

        public string? Read(string kind, string name) => null;

        public GpuFeatureReading? ReadGpuFeature(string vendor, string feature)
        {
            Asked++;
            return reading;
        }

        public void Refresh()
        {
        }
    }

    [Fact]
    public void Outside_the_command_line_the_driver_is_asked_through_the_source_and_only_where_windows_lists_an_amd_adapter()
    {
        var radeon = new GpuFeatureReading(GpuAvailability.Available, "AMD Radeon RX 9070 XT", 0, true, false, 0, null);
        var source = new Source(radeon);

        Assert.Same(radeon, GpuFeatureReader.Read("amd", "antilag", source, () => true));
        Assert.Equal(1, source.Asked);

        // No AMD adapter: the driver library is not asked at all, so a PC with only AMD's software installed never loads it.
        Assert.Equal(GpuAvailability.NoAdapter, GpuFeatureReader.Read("amd", "antilag", source, () => false)!.Availability);
        Assert.Equal(1, source.Asked);

        // Windows' adapter list unreadable, or no way to ask the command line: unreadable, never a state.
        Assert.Equal(GpuAvailability.Unreadable, GpuFeatureReader.Read("amd", "antilag", source, () => null)!.Availability);
        Assert.Equal(GpuAvailability.Unreadable, GpuFeatureReader.Read("amd", "antilag", null, () => true)!.Availability);

        // Only the one switch exists.
        Assert.Null(GpuFeatureReader.Read("amd", "freesync", source, () => true));
        Assert.Null(GpuFeatureReader.Read("nvidia", "antilag", source, () => true));
    }

    [Fact]
    public void A_report_printed_by_the_command_line_reads_back_into_the_same_reading()
    {
        FakeAdlx.Reset(Radeon("AMD Radeon RX 9070 XT", supported: true, on: true));
        var report = AmdGpuReader.Read(FakeAdlx.EntryPoints());

        var parsed = AmdCommandLine.ParseReport("Windows catalog: built-in (154 operations).\n" + AmdCommandLine.ToJson(report));

        Assert.NotNull(parsed);
        Assert.Equal(GpuFeatureReader.FromReport(report), GpuFeatureReader.FromReport(parsed!));
        Assert.Null(AmdCommandLine.ParseReport("not json at all"));
        Assert.Null(AmdCommandLine.ParseReport("{ \"broken\": "));
    }

    [Fact]
    public void A_driver_without_adlx_or_one_that_would_not_start_is_not_read_as_a_state()
    {
        FakeAdlx.Reset();
        FakeAdlx.InitializeResult = AdlxResult.AdlInitError;
        var notStarted = GpuFeatureReader.FromReport(AmdGpuReader.Read(FakeAdlx.EntryPoints()));
        Assert.Equal(GpuAvailability.Unreadable, notStarted.Availability);
        Assert.Null(notStarted.Enabled);

        var noDriver = GpuFeatureReader.FromReport(new AmdAdlxReport(AmdAvailability.NoDriver, null, null, [], "DllMissing", null, []));
        Assert.Equal(GpuAvailability.NoDriver, noDriver.Availability);
    }
}
