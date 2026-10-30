using Mesura.Core.Facts;
using Mesura.Core.Normalize;
using Mesura.Core.Raw.Serialization;
using Mesura.Core.SystemInfo;
using Mesura.Core.Tests.Checks;

namespace Mesura.Core.Tests.SystemInfo;

/// <summary>The System page's report: what it takes from the facts, what it leaves unknown, and what its text holds.</summary>
public sealed class SystemReportTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    private static readonly NormalizedSnapshot Notebook = Normalizer.Normalize(
        SnapshotJson.Deserialize(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "scrubbed", "PC-000-20260928T223014Z.json"))));

    [Fact]
    public void The_notebook_fixture_gives_a_complete_report_without_extras()
    {
        var report = SystemReportBuilder.Build(Notebook, new SystemExtras(), Now);

        Assert.Equal("Laptop", report.Machine.Kind);
        Assert.NotEmpty(report.Processors);
        Assert.Contains("Core", report.Processors[0].Name);
        Assert.NotEmpty(report.Memory.Modules);
        Assert.NotNull(report.Memory.TotalBytes); // the sum of the installed modules when Windows' own figure isn't supplied
        Assert.NotEmpty(report.Gpus);
        Assert.NotEmpty(report.Disks);
        Assert.NotEmpty(report.Displays);
        Assert.Equal(26200, report.Os.Build);
        Assert.Null(report.Firmware.Version); // nothing was supplied: unknown, not invented
        Assert.Null(report.Power.PlanName);
    }

    [Fact]
    public void Extras_fill_what_the_scan_does_not_hold_and_the_memory_figure_from_windows_wins()
    {
        var extras = new SystemExtras
        {
            OsProductName = "Windows 11 Pro",
            MemoryTotalBytes = 17_000_000_000,
            MemoryAvailableBytes = 4_000_000_000,
            BiosVersion = "3.25",
            BootMode = "UEFI",
            SecureBoot = true,
            PowerPlan = "Balanced",
        };

        var report = SystemReportBuilder.Build(Notebook, extras, Now);

        Assert.Equal("Windows 11 Pro", report.Os.Name);
        Assert.Equal(17_000_000_000UL, report.Memory.TotalBytes);
        Assert.Equal(13_000_000_000UL, report.Memory.UsedBytes);
        Assert.Equal(("3.25", "UEFI", true), (report.Firmware.Version, report.Firmware.BootMode, report.Firmware.SecureBoot));
        Assert.Equal("Balanced", report.Power.PlanName);
    }

    [Fact]
    public void Memory_in_use_is_unknown_when_the_available_figure_is_missing_or_larger_than_the_total()
    {
        Assert.Null(new SystemMemory(1000, null, []).UsedBytes);
        Assert.Null(new SystemMemory(1000, 2000, []).UsedBytes);
        Assert.Equal(400UL, new SystemMemory(1000, 600, []).UsedBytes);
    }

    [Fact]
    public void Facts_with_gaps_become_unknown_values_not_zeroes()
    {
        var facts = FactsBuilder.Desktop() with
        {
            Machine = new MachineFacts(FactsBuilder.Missing<string>(), FactsBuilder.Missing<string>(), FactsBuilder.Missing<string>(), FactsBuilder.Missing<string>(), FactsBuilder.Missing<ChassisType>()),
            Os = FactsBuilder.Desktop().Os with { Build = FactsBuilder.Missing<int>(), Edition = FactsBuilder.Missing<string>() },
            Power = new PowerFacts(FactsBuilder.Missing<bool>(), FactsBuilder.Missing<bool>()),
        };

        var report = SystemReportBuilder.Build(facts, new SystemExtras(), Now);

        Assert.Null(report.Machine.Model);
        Assert.Null(report.Machine.Kind);
        Assert.Null(report.Os.Build);
        Assert.Null(report.Os.Name);
        Assert.Null(report.Power.OnAc);
        Assert.Null(report.Power.HasBattery);
        Assert.Null(report.Memory.TotalBytes);
    }

    [Theory]
    [InlineData("To be filled by O.E.M.")]
    [InlineData("  Default string ")]
    [InlineData("System Product Name")]
    [InlineData("")]
    [InlineData("   ")]
    public void Firmware_placeholders_are_not_values(string text) => Assert.Null(SystemReportBuilder.Clean(text));

    [Fact]
    public void A_real_value_is_kept_and_trimmed() => Assert.Equal("ACME Book 14", SystemReportBuilder.Clean("  ACME Book 14 "));

    [Fact]
    public void The_software_adapter_is_not_listed_as_graphics()
    {
        var report = SystemReportBuilder.Build(FactsBuilder.Desktop(), new SystemExtras(), Now);

        Assert.Equal(2, report.Gpus.Count); // the integrated and the discrete one; the software adapter (gpu-3) is left out
    }

    [Fact]
    public void The_text_says_unavailable_for_what_is_unknown_and_holds_no_identifiers()
    {
        var report = SystemReportBuilder.Build(Notebook, new SystemExtras(), Now);

        var text = SystemReportText.Render(report, "9.9.9");

        Assert.Contains("Made by Mesura 9.9.9 on 2026-10-29 12:00 UTC", text);
        Assert.Contains("- BIOS / UEFI: unavailable", text);
        Assert.Contains("## Storage", text);
        Assert.DoesNotContain("serial", text.Replace("no serial numbers", "", StringComparison.OrdinalIgnoreCase), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("MAC", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "unavailable")]
    [InlineData(0UL, "0 MB")]
    [InlineData(134_217_728UL, "128 MB")]
    [InlineData(8_589_934_592UL, "8 GB")]
    [InlineData(17_179_869_184UL, "16 GB")]
    [InlineData(2_199_023_255_552UL, "2 TB")]
    public void Sizes_are_shown_in_the_unit_that_reads_best(ulong? bytes, string expected) =>
        Assert.Equal(expected, SystemReportText.Bytes(bytes));

    [Fact]
    public void Decimal_units_are_used_for_drives_as_makers_label_them()
    {
        Assert.Equal("512 GB", SystemReportText.Bytes(512_110_190_592UL, decimalUnits: true));
    }

    [Theory]
    [InlineData(30, "30 min")]
    [InlineData(150, "2 h 30 min")]
    [InlineData(3000, "2 d 2 h")]
    public void Uptime_reads_in_the_largest_two_units(int minutes, string expected) =>
        Assert.Equal(expected, SystemReportText.Uptime(TimeSpan.FromMinutes(minutes)));
}
