using Mesura.Core.Firmware;
using Mesura.Core.SystemInfo;

namespace Mesura.Core.Tests.Firmware;

/// <summary>The BIOS &amp; Firmware analysis (0.4, docs/firmware.md): rules over facts, the part-number ratings, the knowledge file and the since-last-time history.</summary>
public sealed class FirmwareTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private static SystemMemoryModule Dimm(string slot, string bank, int configured, string part = "", string type = "DDR5", ulong gb = 16, string form = "DIMM") =>
        new(slot, gb << 30, type, configured, "Maker", form, configured, 4800, part, bank);

    private static FirmwareFacts Facts(
        string kind = "Desktop", string board = "Gigabyte Technology Co., Ltd.", string? cpu = "AMD Ryzen 7 7700 8-Core Processor", string bootMode = "UEFI", bool? secureBoot = true,
        IReadOnlyList<SystemMemoryModule>? memory = null, int? slots = 4, IReadOnlyList<FirmwareGpu>? gpus = null, FirmwareExtras? extras = null, string biosDate = "05/24/2026") =>
        new(Now, 26200, kind, board, "B850 EAGLE ICE", board, "B850 EAGLE ICE", "American Megatrends", "F4", biosDate, bootMode, secureBoot,
            extras ?? new FirmwareExtras { TpmPresent = true, TpmVersion = "2.0", Virtualization = true, CpuVendor = cpu is not null && cpu.Contains("Intel", StringComparison.Ordinal) ? "GenuineIntel" : "AuthenticAMD" },
            cpu, memory ?? [Dimm("DIMM_A2", "P0 CHANNEL A", 6000, "F5-6000J3038F16GX2-FX5"), Dimm("DIMM_B2", "P0 CHANNEL B", 6000, "F5-6000J3038F16GX2-FX5")], slots,
            gpus ?? [new FirmwareGpu("NVIDIA GeForce RTX 4070", "NVIDIA", false, 12UL << 30, 16UL << 30)]);

    private static FirmwareFinding Find(FirmwareFacts f, string id) => FirmwareAnalyzer.Analyze(f).Find(id)!;

    [Theory]
    [InlineData("F5-6000J3038F16GX2-FX5", 6000)]
    [InlineData("F4-3600C16D-32GVK", 3600)]
    [InlineData("KF560C36BBEK2-32", 6000)]
    [InlineData("KF432C16BB1K2/32", 3200)]
    [InlineData("KF426C16BB1/8", 2666)]
    [InlineData("CMK32GX5M2B6000C30", 6000)]
    [InlineData("CMK16GX4M2B3200C16", 3200)]
    [InlineData("CP2K16G60C36U5B", 6000)]
    public void The_kit_rating_comes_only_from_part_numbers_that_say_it(string part, int expected) => Assert.Equal(expected, MemoryRatings.RatedMts(part));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("M323R2GA3BB0-CQKOL")]   // a plain JEDEC module: no profile to be rated by
    [InlineData("CT16G48C40U5.M8A1")]    // Crucial's basic line is JEDEC, not a profile
    [InlineData("F5-0000J3038F16GX2")]   // a pattern match that isn't a real speed
    public void A_part_number_without_a_rating_gives_none_and_never_a_guess(string? part) => Assert.Null(MemoryRatings.RatedMts(part));

    [Fact]
    public void Memory_at_its_rating_is_in_place_and_below_it_is_worth_a_look()
    {
        Assert.Equal(FirmwareStatus.InPlace, Find(Facts(), "memory-profile").Status);

        var slow = Facts(memory: [Dimm("DIMM_A2", "P0 CHANNEL A", 4800, "F5-6000J3038F16GX2-FX5"), Dimm("DIMM_B2", "P0 CHANNEL B", 4800, "F5-6000J3038F16GX2-FX5")]);
        var finding = Find(slow, "memory-profile");
        Assert.Equal(FirmwareStatus.Review, finding.Status);
        Assert.Equal(FirmwareCertainty.Likely, finding.Certainty); // inferred from a part number, never read from Windows
        Assert.Equal(["4800", "6000", "F5-6000J3038F16GX2-FX5", "DDR5"], finding.Seen.Single().Args);
    }

    [Fact]
    public void Memory_at_base_speed_with_no_readable_rating_is_unknown_not_accused()
    {
        var f = Facts(memory: [Dimm("DIMM_A2", "A", 4800, "M323R2GA3BB0-CQKOL"), Dimm("DIMM_B2", "B", 4800, "M323R2GA3BB0-CQKOL")]);
        Assert.Equal(FirmwareStatus.Unknown, Find(f, "memory-profile").Status);

        var above = Facts(memory: [Dimm("DIMM_A2", "A", 6400, "unknown-part"), Dimm("DIMM_B2", "B", 6400, "unknown-part")]);
        Assert.Equal(FirmwareStatus.InPlace, Find(above, "memory-profile").Status); // above JEDEC's top: a profile is evidently on
    }

    [Fact]
    public void A_laptop_and_soldered_memory_have_no_profile_or_channel_advice()
    {
        var laptop = Facts(kind: "Laptop", memory: [Dimm("Controller0-ChannelA", "A", 3200, type: "DDR4", form: "Soldered")]);
        Assert.Equal(FirmwareStatus.NotApplicable, Find(laptop, "memory-profile").Status);
        Assert.Equal(FirmwareStatus.NotApplicable, Find(laptop, "memory-channels").Status);
        Assert.Equal(FirmwareStatus.NotApplicable, Find(laptop, "cpu-boost").Status);
    }

    [Fact]
    public void One_module_in_a_desktop_is_single_channel_and_two_in_one_channel_are_likely_so()
    {
        var single = Facts(memory: [Dimm("DIMM_A2", "P0 CHANNEL A", 6000)], slots: 4);
        Assert.Equal(FirmwareStatus.Review, Find(single, "memory-channels").Status);
        Assert.Equal(FirmwareCertainty.Confirmed, Find(single, "memory-channels").Certainty);

        var sameChannel = Facts(memory: [Dimm("DIMM_A1", "P0 CHANNEL A", 6000), Dimm("DIMM_A2", "P0 CHANNEL A", 6000)], slots: 4);
        Assert.Equal(FirmwareStatus.Review, Find(sameChannel, "memory-channels").Status);
        Assert.Equal(FirmwareCertainty.Likely, Find(sameChannel, "memory-channels").Certainty);

        Assert.Equal(FirmwareStatus.InPlace, Find(Facts(), "memory-channels").Status);
        Assert.Equal(FirmwareStatus.InPlace, Find(Facts(memory: [Dimm("A1", "A", 5600), Dimm("A2", "A", 5600), Dimm("B1", "B", 5600), Dimm("B2", "B", 5600)]), "memory-channels").Status);
    }

    [Fact]
    public void Resizable_bar_reads_the_memory_window_against_the_cards_memory()
    {
        Assert.Equal(FirmwareStatus.InPlace, Find(Facts(), "resizable-bar").Status);

        var off = Facts(gpus: [new FirmwareGpu("NVIDIA GeForce RTX 4070", "NVIDIA", false, 12UL << 30, 256UL << 20)]);
        var finding = Find(off, "resizable-bar");
        Assert.Equal(FirmwareStatus.Review, finding.Status);
        Assert.Equal(FirmwareCertainty.Likely, finding.Certainty);

        Assert.Equal(FirmwareStatus.Unknown, Find(Facts(gpus: [new FirmwareGpu("Radeon", "AMD", false, 16UL << 30, null)]), "resizable-bar").Status);
        Assert.Equal(FirmwareStatus.NotApplicable, Find(Facts(gpus: [new FirmwareGpu("Intel Iris Xe", "Intel", true, 128UL << 20, 256UL << 20)]), "resizable-bar").Status);
        Assert.Equal(FirmwareStatus.NotApplicable, Find(Facts(gpus: []), "resizable-bar").Status);

        // Legacy boot has to be fixed first, and that is the careful one.
        var legacy = Find(Facts(bootMode: "Legacy BIOS", gpus: [new FirmwareGpu("RTX", "NVIDIA", false, 12UL << 30, 256UL << 20)]), "resizable-bar");
        Assert.Equal(FirmwareRisk.Careful, legacy.Risk);
        Assert.Equal("rebar.needs-uefi", legacy.Seen.Single().Key);
    }

    [Fact]
    public void Boot_mode_secure_boot_and_the_tpm_are_read_never_changed()
    {
        Assert.Equal(FirmwareStatus.InPlace, Find(Facts(), "boot-mode").Status);
        Assert.Equal(FirmwareStatus.Review, Find(Facts(bootMode: "Legacy BIOS", secureBoot: null), "boot-mode").Status);
        Assert.Equal(FirmwareStatus.InPlace, Find(Facts(), "secure-boot-tpm").Status);
        Assert.Equal(FirmwareStatus.Optional, Find(Facts(secureBoot: false), "secure-boot-tpm").Status);
        Assert.Equal(FirmwareStatus.Unknown, Find(Facts(secureBoot: null, extras: new FirmwareExtras()), "secure-boot-tpm").Status);
    }

    [Fact]
    public void Virtualization_off_is_optional_and_a_running_hypervisor_means_it_is_on()
    {
        Assert.Equal(FirmwareStatus.InPlace, Find(Facts(), "virtualization").Status);
        Assert.Equal(FirmwareStatus.Optional, Find(Facts(extras: new FirmwareExtras { Virtualization = false }), "virtualization").Status);
        Assert.Equal(FirmwareStatus.InPlace, Find(Facts(extras: new FirmwareExtras { Virtualization = false, HypervisorRunning = true }), "virtualization").Status);
    }

    [Theory]
    [InlineData("13th Gen Intel(R) Core(TM) i9-13900K", true)]
    [InlineData("Intel(R) Core(TM) i7-14700KF", true)]
    [InlineData("13th Gen Intel(R) Core(TM) i5-13600K", true)]
    [InlineData("13th Gen Intel(R) Core(TM) i9-13900HX", false)]
    [InlineData("11th Gen Intel(R) Core(TM) i7-1165G7 @ 2.80GHz", false)]
    [InlineData("12th Gen Intel(R) Core(TM) i9-12900K", false)]
    [InlineData("AMD Ryzen 9 7950X", false)]
    public void Only_the_desktop_13th_and_14th_generation_parts_are_in_intels_instability_guidance(string cpu, bool affected) => Assert.Equal(affected, FirmwareAnalyzer.IntelDesktop13Or14(cpu));

    [Fact]
    public void An_affected_intel_processor_with_old_microcode_is_worth_a_bios_update_and_with_new_microcode_is_not()
    {
        var old = Facts(cpu: "13th Gen Intel(R) Core(TM) i9-13900K", extras: new FirmwareExtras { CpuVendor = "GenuineIntel", Microcode = 0x125 });
        var finding = Find(old, "bios-update");
        Assert.Equal(FirmwareStatus.Review, finding.Status);
        Assert.Contains(finding.Seen, o => o.Key == "bios.intel-microcode-old" && o.Args[0] == "0x125");

        var fixedMicrocode = Facts(cpu: "13th Gen Intel(R) Core(TM) i9-13900K", extras: new FirmwareExtras { CpuVendor = "GenuineIntel", Microcode = 0x12B });
        Assert.NotEqual(FirmwareStatus.Review, Find(fixedMicrocode, "bios-update").Status);

        var unread = Facts(cpu: "13th Gen Intel(R) Core(TM) i9-13900K", extras: new FirmwareExtras { CpuVendor = "GenuineIntel" });
        Assert.Equal(FirmwareStatus.Unknown, Find(unread, "bios-update").Status); // a processor that may be affected whose microcode can't be read is not called fine
    }

    [Fact]
    public void An_old_bios_is_optional_to_check_never_a_demand_to_flash()
    {
        Assert.Equal(FirmwareStatus.InPlace, Find(Facts(biosDate: "05/24/2026"), "bios-update").Status);
        var old = Find(Facts(biosDate: "01/10/2024"), "bios-update");
        Assert.Equal(FirmwareStatus.Optional, old.Status);
        Assert.Equal(FirmwareRisk.Careful, old.Risk);
        Assert.Equal(FirmwareStatus.Unknown, Find(Facts(biosDate: "not a date"), "bios-update").Status);
    }

    [Fact]
    public void Processor_boost_is_reported_as_unreadable_experimental_and_never_as_a_recommendation()
    {
        var amd = Find(Facts(), "cpu-boost");
        Assert.Equal(FirmwareStatus.Optional, amd.Status);
        Assert.Equal(FirmwareRisk.Experimental, amd.Risk);
        Assert.Equal("boost.amd", amd.Seen.Single().Key);
        Assert.Equal("boost.intel", Find(Facts(cpu: "Intel(R) Core(TM) i7-12700K"), "cpu-boost").Seen.Single().Key);
    }

    [Fact]
    public void Every_finding_has_knowledge_in_both_languages_and_every_maker_has_steps()
    {
        var knowledge = FirmwareKnowledge.Default;
        var report = FirmwareAnalyzer.Analyze(Facts());
        Assert.Equal(9, report.Findings.Count);
        foreach (var finding in report.Findings)
        {
            var setting = knowledge.Get(finding.Id);
            Assert.NotNull(setting);
            Assert.All(new[] { setting!.Title, setting.What, setting.Why, setting.Risk, setting.Verify }, t =>
            {
                Assert.False(string.IsNullOrWhiteSpace(t.En), finding.Id);
                Assert.False(string.IsNullOrWhiteSpace(t.Ru), finding.Id);
            });
            Assert.NotEmpty(setting.StepsFor(FirmwareVendor.Unknown));
            Assert.All(setting.Steps.Values.SelectMany(s => s), t =>
            {
                Assert.False(string.IsNullOrWhiteSpace(t.En), finding.Id);
                Assert.False(string.IsNullOrWhiteSpace(t.Ru), finding.Id);
            });
            Assert.All(setting.Links, l => Assert.StartsWith("https://", l.Url, StringComparison.Ordinal));
        }

        // The settings a BIOS menu changes carry the four makers' own steps.
        foreach (var id in new[] { "memory-profile", "resizable-bar", "secure-boot-tpm", "virtualization", "cpu-boost" })
        {
            var setting = knowledge.Get(id)!;
            Assert.All(new[] { FirmwareVendor.Gigabyte, FirmwareVendor.Asus, FirmwareVendor.Msi, FirmwareVendor.AsRock }, v => Assert.True(setting.HasOwnSteps(v), id + " " + v));
        }

        Assert.Equal("Menu names", knowledge.Note.En[..10]);
    }

    [Theory]
    [InlineData("Gigabyte Technology Co., Ltd.", FirmwareVendor.Gigabyte)]
    [InlineData("ASUSTeK COMPUTER INC.", FirmwareVendor.Asus)]
    [InlineData("Micro-Star International Co., Ltd.", FirmwareVendor.Msi)]
    [InlineData("ASRock", FirmwareVendor.AsRock)]
    [InlineData("Dell Inc.", FirmwareVendor.Dell)]
    [InlineData("Some Maker Ltd", FirmwareVendor.Other)]
    [InlineData(null, FirmwareVendor.Unknown)]
    public void The_maker_is_recognised_from_the_board_or_the_machine(string? maker, FirmwareVendor expected) => Assert.Equal(expected, FirmwareVendors.From(maker, null, null));

    [Fact]
    public void The_history_keeps_a_change_until_the_next_real_change_and_says_what_changed()
    {
        var before = Facts(memory: [Dimm("A", "A", 4800), Dimm("B", "B", 4800)]);
        var after = Facts(memory: [Dimm("A", "A", 6000), Dimm("B", "B", 6000)]);
        var first = FirmwareHistory.Advance(null, FirmwareSnapshot.Of(before));
        Assert.Empty(first.Changes);

        var restarted = FirmwareHistory.Advance(first, FirmwareSnapshot.Of(after));
        var change = Assert.Single(restarted.Changes);
        Assert.Equal(("memory.speed", "4800", "6000"), (change.Key, change.Before, change.After));

        // Opening the page again with nothing new leaves the change where it is.
        var again = FirmwareHistory.Advance(restarted, FirmwareSnapshot.Of(after with { ReadAt = Now.AddHours(1) }));
        Assert.Equal(restarted, again);

        var roundTrip = FirmwareHistory.TryParse(restarted.ToJson());
        Assert.Equal(restarted.Changes, roundTrip!.Changes);
        Assert.Null(FirmwareHistory.TryParse("{ not json"));
    }

    [Fact]
    public void A_value_that_could_not_be_read_either_time_is_not_reported_as_a_change()
    {
        var a = FirmwareSnapshot.Of(Facts(extras: new FirmwareExtras()));
        var b = FirmwareSnapshot.Of(Facts());
        Assert.DoesNotContain(FirmwareSnapshot.Diff(a, b), c => c.Key is "tpm" or "virtualization");
    }
}
