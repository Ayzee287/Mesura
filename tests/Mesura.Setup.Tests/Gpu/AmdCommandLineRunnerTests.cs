using Mesura.Core.GpuControl;
using Mesura.Core.Windows;
using Mesura.Platform.Tests.Gpu;
using Mesura.Platform.Windows.Gpu;
using Mesura.Setup.Windows;
using Mesura.Setup.Windows.Gpu;
using Mesura.Setup.Windows.Windows;

namespace Mesura.Setup.Tests.Gpu;

/// <summary>
/// The window's way to the AMD driver (0.6): it never calls <c>amdadlx64.dll</c> in its own process, it asks Mesura's own command line. Here the command line is a stand-in that does what <c>GpuCli</c> does,
/// over the fake ADLX with the real ABI (the same reader, the same AMD writer, the same printed words): the reading comes back through the printed report, a write returns the id of the writer's record,
/// a command line that dies after the write is found by its record and put back, and a command line that is missing or prints nonsense is "could not be run", never a guess. Synthetic: no real GPU.
/// </summary>
[Collection("fake-adlx")]
public sealed class AmdCommandLineRunnerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mesura-cli-gpu-").FullName;
    private bool _crashAfterWrite;

    public AmdCommandLineRunnerTests() => AmdCommandLineRunner.RunOverride = Cli;

    public void Dispose()
    {
        AmdCommandLineRunner.RunOverride = null;
        Directory.Delete(_dir, recursive: true);
    }

    private SetupStore Store => new(_dir, legacyRoot: null);

    private AmdGpuChanger Changer() => new(Store, static () => FakeAdlx.EntryPoints(), static () => new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero));

    private static FakeGpu Radeon(bool antiLagOn = false, FakeFeature? antiLag = null)
    {
        var gpu = new FakeGpu { AntiLagLevel = 0 };
        gpu.Features["antilag"] = antiLag ?? new FakeFeature(Enabled: antiLagOn);
        gpu.Features["chill"] = new FakeFeature(Enabled: true);
        return gpu;
    }

    private WindowsWriter Writer() => new(new UserContext("S-1-5-21-1-1-1-1001", "PC-owner", ""));   // no AMD writer handed in, and this host is not the command line: the window's route

    /// <summary>What <c>mesura gpu amd ...</c> prints, from the same reader and writer.</summary>
    private (int Exit, string Text)? Cli(IReadOnlyList<string> args)
    {
        if (args is not ["gpu", "amd", ..])
        {
            return (2, "Usage");
        }

        switch (args.Skip(2).ToArray())
        {
            case ["--json"]:
                return (0, "Windows catalog: built-in.\n" + AmdCommandLine.ToJson(AmdGpuReader.Read(FakeAdlx.EntryPoints())));
            case ["changes"]:
                return (0, Store.ListGpuChanges().Count == 0 ? "No GPU changes recorded yet." : string.Join("\n", Store.ListGpuChanges().Select(id => $"{id}  Written  AntiLag on  AMD")));
            case ["set", "antilag", var word, "--gpu", var gpu, "--apply"]:
                var outcome = Changer().Apply(new AmdChange(GpuFeature.AntiLag, int.Parse(gpu, System.Globalization.CultureInfo.InvariantCulture), null, word == "on"));
                if (_crashAfterWrite)
                {
                    return (-1073741819, "Fatal error.\n0xC0000005");   // the process died: no "Change" line was printed
                }

                return (outcome.Status is GpuChangeStatus.Failed or GpuChangeStatus.NotConfirmed ? 1 : 0, Print(outcome));
            case ["undo", var id, "--apply"]:
                return (0, Print(Changer().Revert(id, apply: true)));
            default:
                return (2, "Usage");
        }
    }

    private static string Print(AmdChangeOutcome outcome) => outcome.Message + (outcome.Record is { } r ? $"\nChange {r.Id}: {r.Status}." : string.Empty);

    [Fact]
    public void The_reading_comes_back_through_the_printed_report_and_equals_the_direct_one()
    {
        FakeAdlx.Reset(Radeon(antiLagOn: true));

        var viaCommandLine = AmdCommandLineRunner.ReadAntiLag();

        Assert.Equal(GpuAvailability.Available, viaCommandLine!.Availability);
        Assert.True(viaCommandLine.Enabled);
        Assert.Equal(0, viaCommandLine.Level);
        Assert.Equal(Mesura.Platform.Windows.Windows.GpuFeatureReader.FromReport(AmdGpuReader.Read(FakeAdlx.EntryPoints())), viaCommandLine);
    }

    [Fact]
    public void A_write_through_the_command_line_returns_the_writers_record_and_the_undo_puts_it_back_with_chill()
    {
        FakeAdlx.Reset(Radeon());

        var result = Writer().SetGpuFeature("amd", "antilag", 0, enabled: true);

        Assert.EndsWith("-gpu-antilag", result.ChangeId, StringComparison.Ordinal);
        Assert.Equal(["antilag=True"], FakeAdlx.Writes);

        Writer().UndoGpuChange(result.ChangeId);

        Assert.Equal(["antilag=True", "antilag=False", "chill=True"], FakeAdlx.Writes);
        Assert.Equal(GpuChangeStatus.Reverted, GpuChangeRecord.FromJson(Store.ReadGpuChange(result.ChangeId)!)!.Status);
    }

    [Fact]
    public void A_command_line_that_dies_after_the_write_is_found_by_its_record_and_the_change_is_put_back()
    {
        FakeAdlx.Reset(Radeon());
        _crashAfterWrite = true;

        var error = Assert.Throws<InvalidOperationException>(() => Writer().SetGpuFeature("amd", "antilag", 0, enabled: true));

        Assert.Contains("Fatal error", error.Message, StringComparison.Ordinal);
        Assert.Contains("put back", error.Message, StringComparison.Ordinal);
        Assert.Equal(["antilag=True", "antilag=False", "chill=True"], FakeAdlx.Writes);   // written by the dead process, put back by the next one
        var id = Assert.Single(Store.ListGpuChanges());
        Assert.Equal(GpuChangeStatus.Reverted, GpuChangeRecord.FromJson(Store.ReadGpuChange(id)!)!.Status);
    }

    [Fact]
    public void A_refusing_driver_is_reported_with_its_words_and_leaves_nothing_changed()
    {
        FakeAdlx.Reset(Radeon(antiLag: new FakeFeature(Enabled: false, WriteResult: Mesura.Platform.Windows.Gpu.Adlx.AdlxResult.Fail)));

        var error = Assert.Throws<InvalidOperationException>(() => Writer().SetGpuFeature("amd", "antilag", 0, enabled: true));

        Assert.Contains("refused", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_command_line_that_cannot_be_run_is_could_not_be_run_and_never_a_state()
    {
        AmdCommandLineRunner.RunOverride = static _ => null;

        var reading = AmdCommandLineRunner.ReadAntiLag();
        Assert.Equal(GpuAvailability.Unreadable, reading!.Availability);
        Assert.Null(reading.Enabled);
        Assert.Throws<InvalidOperationException>(() => Writer().SetGpuFeature("amd", "antilag", 0, enabled: true));
        Assert.Throws<InvalidOperationException>(() => Writer().UndoGpuChange("20261104T000000000Z-gpu-antilag"));
    }

    [Fact]
    public void Text_that_is_not_the_report_is_unreadable()
    {
        AmdCommandLineRunner.RunOverride = static _ => (0, "something else entirely");

        Assert.Equal(GpuAvailability.Unreadable, AmdCommandLineRunner.ReadAntiLag()!.Availability);
    }

    [Fact]
    public void Only_an_id_the_writer_made_is_passed_to_the_command_line()
    {
        Assert.Throws<ArgumentException>(() => AmdCommandLineRunner.UndoChange("../../x --apply"));
        Assert.Throws<ArgumentException>(() => AmdCommandLineRunner.UndoChange("20261104T000000000Z-gpu-antilag --gpu 1"));
    }
}
