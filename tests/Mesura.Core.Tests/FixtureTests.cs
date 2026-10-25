using System.Text;
using Mesura.Core.Privacy;
using Mesura.Core.Raw;
using Mesura.Core.Raw.Serialization;

namespace Mesura.Core.Tests;

/// <summary>capture-fixture's two pure steps: opaque ids get stand-ins, and no local identifier survives.</summary>
public sealed class FixtureTests
{
    [Fact]
    public void Opaque_ids_are_replaced_and_every_join_still_holds()
    {
        var (fixture, replaced) = FixturePseudonymizer.Pseudonymize(SnapshotFixture.Create());
        var json = Encoding.UTF8.GetString(SnapshotJson.Serialize(fixture));

        Assert.DoesNotContain("00000000000A000", json, StringComparison.Ordinal); // adapter LUIDs
        Assert.DoesNotContain("test\\u00260", json, StringComparison.Ordinal); // PnP instance parts
        Assert.DoesNotContain("00000000-0000-0000-0000-00000000000", json, StringComparison.Ordinal); // volume GUIDs
        Assert.True(replaced > 0);

        var outputs = fixture.Sources.Display.Data!.Outputs;
        var adapters = fixture.Sources.Adapters.Data!.Adapters;
        Assert.All(outputs, output => Assert.Contains(adapters, adapter => adapter.AdapterId == output.AdapterId));
        Assert.All(fixture.Sources.Edid.Data!.Monitors, monitor => Assert.Contains(outputs, output => output.MonitorDevicePath.Value == monitor.MonitorDevicePath));

        var storage = fixture.Sources.Storage.Data!;
        Assert.All(storage.Volumes, volume => Assert.Contains(storage.Partitions, partition => partition.AccessPaths.Contains(volume.Path)));
    }

    [Fact]
    public void Model_data_inside_ids_is_kept()
    {
        var (fixture, _) = FixturePseudonymizer.Pseudonymize(SnapshotFixture.Create());

        var device = fixture.Sources.Pcie.Data!.Devices.Single(d => d.PciIds.Value?.VendorId == 0x10DE);
        Assert.StartsWith(@"PCI\VEN_10DE&DEV_2484&SUBSYS_40291458&REV_A1\instance-", device.InstanceId, StringComparison.Ordinal);

        var path = fixture.Sources.Display.Data!.Outputs[0].MonitorDevicePath.Value!;
        Assert.Matches(@"^\\\\\?\\DISPLAY#TST000\d#instance-\d+#\{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7\}$", path);

        Assert.Equal(SnapshotFixture.Create().Sources.Smbios.Data!.Table, fixture.Sources.Smbios.Data!.Table); // byte payloads untouched
    }

    [Fact]
    public void Pseudonymising_is_deterministic_whatever_the_collection_order()
    {
        var once = SnapshotJson.Serialize(FixturePseudonymizer.Pseudonymize(SnapshotFixture.Create()).Snapshot);
        var again = SnapshotJson.Serialize(FixturePseudonymizer.Pseudonymize(SnapshotFixture.Create()).Snapshot);
        var reversed = SnapshotJson.Serialize(FixturePseudonymizer.Pseudonymize(SnapshotFixture.Create(reversed: true)).Snapshot);

        Assert.Equal(once, again);
        Assert.Equal(once, reversed);
    }

    [Fact]
    public void Leak_check_finds_a_whole_token_identifier_and_reports_only_its_category()
    {
        var json = SnapshotJson.Serialize(SnapshotFixture.Create());

        LocalIdentifier[] identifiers =
        [
            new("monitor name", "TEST MONITOR"), // has a space: treated as a placeholder, not checked
            new("gpu", "Test"), // whole token inside "Test Discrete GPU"
            new("absent", "NOT-IN-THE-SNAPSHOT-42"),
        ];

        Assert.Equal(["gpu"], IdentifierLeakCheck.FindLeaks(json, identifiers));
    }

    [Theory]
    [InlineData("User")] // inside %USERPROFILE% is not a whole token
    [InlineData("NULL")] // a firmware placeholder
    [InlineData("0000000000")] // one repeated character
    [InlineData("abc")] // too short to identify anything
    public void Leak_check_ignores_values_that_cannot_identify_a_machine(string value)
    {
        var json = Encoding.UTF8.GetBytes("""{"text": "%USERPROFILE%\\x NULL 0000000000 abc"}""");

        Assert.Empty(IdentifierLeakCheck.FindLeaks(json, [new LocalIdentifier("candidate", value)]));
    }

    [Fact]
    public void Leak_check_decodes_byte_payloads()
    {
        var payload = Convert.ToBase64String(Encoding.ASCII.GetBytes("\0SN-7F3K2Q9\0\0"));
        var json = Encoding.UTF8.GetBytes($$"""{"table": "{{payload}}"}""");

        Assert.Equal(["serial"], IdentifierLeakCheck.FindLeaks(json, [new LocalIdentifier("serial", "SN-7F3K2Q9")]));
    }

    [Fact]
    public void Leak_check_finds_a_binary_identifier_anywhere_in_a_byte_payload()
    {
        // A tiled monitor repeats its base serial as the tile serial: the check must find the copy, wherever it is.
        byte[] serial = [0x12, 0x34, 0x56, 0x78];
        var payload = Convert.ToBase64String([0x70, 0x20, 0x16, 0x00, .. serial, 0x00]);
        var json = Encoding.UTF8.GetBytes($$"""{"bytes": "{{payload}}", "text": "12345678"}""");

        Assert.Equal(["monitor EDID identifier"], IdentifierLeakCheck.FindLeaks(json, [LocalIdentifier.Binary("monitor EDID identifier", serial)]));
    }

    [Fact]
    public void Leak_check_matches_binary_identifiers_only_in_byte_payloads_and_skips_trivial_ones()
    {
        var json = Encoding.UTF8.GetBytes($$"""{"bytes": "{{Convert.ToBase64String([0, 0, 0, 0, 1, 1, 1])}}", "text": "\u00124Vx"}""");

        Assert.Empty(IdentifierLeakCheck.FindLeaks(json, [LocalIdentifier.Binary("text only", [0x12, 0x34, 0x56, 0x78])]));
        Assert.Empty(IdentifierLeakCheck.FindLeaks(json, [LocalIdentifier.Binary("one repeated byte", [0, 0, 0, 0])]));
        Assert.Empty(IdentifierLeakCheck.FindLeaks(json, [LocalIdentifier.Binary("too short", [0, 1, 1])]));
    }

    [Fact]
    public void A_personal_identifier_with_a_space_is_still_checked_as_a_whole_phrase()
    {
        var json = Encoding.UTF8.GetBytes("""{"detail": "IOException reading C:\\Users\\John Smith\\Disks\\dev.vhdx", "owner": "Contoso Ltd"}""");

        LocalIdentifier[] identifiers =
        [
            LocalIdentifier.Personal("user profile path", @"C:\Users\John Smith"),
            LocalIdentifier.Personal("registered organisation", "Contoso Ltd"),
            new("board serial number", "To Be Filled By O.E.M."), // a firmware placeholder: skipped
        ];

        Assert.Equal(["user profile path", "registered organisation"], IdentifierLeakCheck.FindLeaks(json, identifiers));
        Assert.False(IdentifierLeakCheck.IsCheckable(identifiers[2]));
    }

    [Fact]
    public void Display_paths_get_stand_ins_even_when_the_edid_source_is_unavailable()
    {
        // The display section wraps the path in a reading; without the EDID section's copy it must still be found.
        var complete = SnapshotFixture.Create();
        var withoutEdid = new RawSnapshot(
            complete.SchemaVersion,
            complete.CapturedAtUtc,
            complete.App,
            complete.Os,
            complete.Elevated,
            complete.Sources with { Edid = SourceEntry.Unavailable<EdidData>("test.edid", 10000, MissingReason.TimedOut, "No answer within 10 s") });

        var (fixture, _) = FixturePseudonymizer.Pseudonymize(withoutEdid);

        Assert.All(fixture.Sources.Display.Data!.Outputs, output => Assert.Matches("#instance-[0-9]+#", output.MonitorDevicePath.Value!));
    }

    [Fact]
    public void Stand_ins_are_numbered_by_the_original_ids_so_run_dependent_order_does_not_renumber_them()
    {
        // Swap the disk numbers, as a restart may: partitions reorder, and volume GUIDs first appear in another order.
        var original = SnapshotFixture.Create();
        var storage = original.Sources.Storage.Data!;
        var renumbered = new StorageData(
            storage.PhysicalDisks,
            storage.Disks,
            [.. storage.Partitions.Select(p => new DiskPartition(1 - p.DiskNumber, p.PartitionNumber, p.SizeBytes, p.AccessPaths))],
            storage.Volumes);
        var restarted = new RawSnapshot(original.SchemaVersion, original.CapturedAtUtc, original.App, original.Os, original.Elevated,
            original.Sources with { Storage = SourceEntry.Ok("test.storage", 400, renumbered) });

        string VolumeOf(RawSnapshot snapshot, string letter) =>
            FixturePseudonymizer.Pseudonymize(snapshot).Snapshot.Sources.Storage.Data!.Volumes.Single(v => v.DriveLetter.Value == letter).Path;

        Assert.Equal(VolumeOf(original, "C"), VolumeOf(restarted, "C"));
        Assert.Equal(VolumeOf(original, "D"), VolumeOf(restarted, "D"));
    }

    [Fact]
    public void An_identifier_never_prints_its_value()
    {
        Assert.Equal("system serial number", new LocalIdentifier("system serial number", "SN-7F3K2Q9").ToString());
    }
}
