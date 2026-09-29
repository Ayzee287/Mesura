using System.Text;
using FpsUtility.Core.Privacy;
using FpsUtility.Core.Raw.Serialization;

namespace FpsUtility.Core.Tests.TrackA;

/// <summary>
/// The two halves of the privacy verification: the leak check now says where (JSON Pointer), with the same matching
/// rules as before; and the identifier-free audit, which the owner's machine can run again at acceptance.
/// </summary>
public sealed class PrivacyVerificationTests
{
    private static readonly LocalIdentifier Serial = new("system serial number", "SN7F3K2Q9X");

    [Fact]
    public void Json_strings_come_with_rfc_6901_pointers_and_their_property()
    {
        var strings = JsonStrings.Of("""{"a":{"b/c":["x",{"d~e":"y"}]},"f":"z"}"""u8);

        Assert.Equal(
            [("/a/b~1c/0", "b/c", "x"), ("/a/b~1c/1/d~0e", "d~e", "y"), ("/f", "f", "z")],
            strings.Select(text => (text.Pointer, text.Property, text.Value)));
    }

    [Fact]
    public void A_leak_is_located_by_pointer_and_never_carries_its_value()
    {
        var json = Encoding.UTF8.GetBytes("""{"notes":["fine","serial SN7F3K2Q9X here"],"other":"SN7F3K2Q9XY"}""");

        var location = Assert.Single(IdentifierLeakCheck.FindLeakLocations(json, [Serial]));

        Assert.Equal(new LeakLocation("system serial number", "/notes/1"), location);
        Assert.DoesNotContain(Serial.Value, location.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Byte_payloads_are_searched_as_bytes_and_as_text_but_never_as_base64()
    {
        var edidSerial = LocalIdentifier.Binary("monitor EDID identifier", [0x12, 0x34, 0x56, 0x78]);
        var payload = Convert.ToBase64String([0x00, 0x12, 0x34, 0x56, 0x78, 0x00, .. "SN7F3K2Q9X"u8.ToArray()]);
        var json = Encoding.UTF8.GetBytes("{\"edid\":{\"bytes\":\"" + payload + "\"},\"plain\":{\"text\":\"" + payload + "\"}}");

        var locations = IdentifierLeakCheck.FindLeakLocations(json, [edidSerial, Serial]);

        // In the payload: both, as bytes and as the Latin-1 text of those bytes. The same base64 outside a payload
        // property is just text, and neither identifier appears in its base64 spelling.
        Assert.Equal(
            [new LeakLocation("monitor EDID identifier", "/edid/bytes"), new LeakLocation("system serial number", "/edid/bytes")],
            locations);
    }

    [Fact]
    public void Find_leaks_keeps_its_contract_categories_once_in_the_order_given()
    {
        var json = Encoding.UTF8.GetBytes("""{"a":"SN7F3K2Q9X","b":"host DESKTOP-ABC123","c":"SN7F3K2Q9X"}""");
        LocalIdentifier[] identifiers = [LocalIdentifier.Personal("computer name", "DESKTOP-ABC123"), Serial, new("disk serial number", "WD-NOTHERE1")];

        Assert.Equal(["computer name", "system serial number"], IdentifierLeakCheck.FindLeaks(json, identifiers));
    }

    [Fact]
    public void Plain_text_is_located_by_line()
    {
        var locations = IdentifierLeakCheck.FindLeakLocationsInText("first\nsecond SN7F3K2Q9X\nthird", [Serial]);

        Assert.Equal([new LeakLocation("system serial number", "line 2")], locations);
    }

    [Theory]
    [InlineData(@"C:\Users\Dave\Documents", ShareableAudit.UserFolder)]
    [InlineData("S-1-5-21-1111111111-2222222222-3333333333-1013", ShareableAudit.AccountSid)]
    [InlineData("00:1A:2B:3C:4D:5E", ShareableAudit.MacAddress)]
    [InlineData("00-1A-2B-3C-4D-5E", ShareableAudit.MacAddress)]
    [InlineData("reach me at dave@example.org", ShareableAudit.EmailAddress)]
    [InlineData("owner 76561197960287930", ShareableAudit.SteamAccountId)]
    [InlineData(@"\\?\Volume{1b2c3d4e-0000-1111-2222-333344445555}\", ShareableAudit.VolumeGuid)]
    [InlineData("\"LastOwner\"\t\t\"76561197960287930\"", ShareableAudit.SteamPersonal)]
    public void Personal_patterns_are_found_wherever_they_are(string value, string category)
    {
        var json = Encoding.UTF8.GetBytes("{\"x\":" + System.Text.Json.JsonSerializer.Serialize(value) + "}");

        Assert.Contains(ShareableAudit.AuditJson("f.json", json), finding => finding.Category == category && finding.Pointer == "/x" && finding.File == "f.json");
        Assert.Contains(ShareableAudit.AuditText("f.md", value), finding => finding.Category == category && finding.Pointer == "line 1");
    }

    [Theory]
    [InlineData(@"C:\Users\Public\Documents")]
    [InlineData(@"%USERPROFILE%\AppData")]
    [InlineData("\"LastOwner\"\t\t\"redacted\"")]
    [InlineData("\"label\"\t\t\"\"")]
    [InlineData(@"\\?\Volume{volume-2}\")]
    [InlineData("32.0.101.7088")]
    [InlineData("S-1-5-18")]
    [InlineData("PCI Slot 8 : Bus 1 : Device 0 : Function 0 : Adapter 0")]
    public void What_the_scrub_leaves_is_not_flagged(string value)
    {
        var json = Encoding.UTF8.GetBytes("{\"x\":" + System.Text.Json.JsonSerializer.Serialize(value) + "}");

        Assert.Empty(ShareableAudit.AuditJson("f.json", json));
    }

    [Fact]
    public void A_scrubbed_snapshot_passes_the_structural_audit_and_an_unscrubbed_one_fails_it()
    {
        var raw = TrackAFixture.Raw();
        var scrubbed = FixturePseudonymizer.Pseudonymize(raw).Snapshot;
        var scrubbedJson = SnapshotJson.Serialize(scrubbed);

        Assert.Empty(ShareableAudit.AuditScrubbedSnapshot("raw.scrubbed.json", scrubbedJson, scrubbed));

        var findings = ShareableAudit.AuditScrubbedSnapshot("raw.json", SnapshotJson.Serialize(raw), raw);
        Assert.Contains(findings, finding => finding.Category == ShareableAudit.OpaqueId && finding.Pointer == "/sources/adapters/data/adapters/0/adapterId");
        Assert.Contains(findings, finding => finding.Category == ShareableAudit.OpaqueId && finding.Pointer.EndsWith("/instanceId", StringComparison.Ordinal));
        Assert.Contains(findings, finding => finding.Category == ShareableAudit.VolumeGuid);
    }

    [Fact]
    public void An_opaque_id_inside_a_reading_is_audited_as_its_holder()
    {
        // The display output's monitor path is a reading ("monitorDevicePath": {"value": ...}); with the EDID source
        // unavailable, nothing else carries the path. An unscrubbed one must still be found.
        var raw = TrackAFixture.Raw(change: sources => sources with { Edid = Normalize.RawBuilder.Unavailable<Raw.EdidData>(Raw.MissingReason.SourceFailed) });

        var findings = ShareableAudit.AuditScrubbedSnapshot("raw.json", SnapshotJson.Serialize(raw), raw);

        Assert.Contains(findings, finding => finding.Category == ShareableAudit.OpaqueId && finding.Pointer == "/sources/display/data/outputs/0/monitorDevicePath/value");
    }

    [Fact]
    public void An_edid_or_smbios_table_with_something_left_to_scrub_fails_the_audit()
    {
        var edid = Normalize.RawBuilder.EdidBaseBlock();
        edid[12] = 0x78; // a serial number the scrub would zero
        var raw = TrackAFixture.Raw(change: sources => sources with
        {
            Edid = Raw.SourceEntry.Ok("test.edid", 1, new Raw.EdidData(
                [new Raw.MonitorEdid(sources.Edid.Data!.Monitors[0].MonitorDevicePath, Raw.Reading.Known(new Raw.EdidPayload(edid, [true], 0, Raw.Reading.Known(false))))])),
            Smbios = Raw.SourceEntry.Ok("test.smbios", 1, Normalize.RawBuilder.Smbios(
                Normalize.RawBuilder.Structure(1, [1, 2, 3, 4], "Maker", "Model", "1.0", "SERIAL-123"), Normalize.RawBuilder.EndOfTable())),
        });
        var scrubbed = FixturePseudonymizer.Pseudonymize(raw).Snapshot;

        var findings = ShareableAudit.AuditScrubbedSnapshot("raw.scrubbed.json", SnapshotJson.Serialize(scrubbed), scrubbed);

        Assert.Contains(findings, finding => finding.Category == ShareableAudit.UnscrubbedEdid && finding.Pointer == "/sources/edid/data/monitors/0/edid/value/bytes");
        Assert.Contains(findings, finding => finding.Category == ShareableAudit.UnscrubbedSmbios && finding.Pointer == "/sources/smbios/data/table");
    }

    [Fact]
    public void The_opaque_ids_of_a_scan_are_its_own_identifiers()
    {
        var identifiers = FixturePseudonymizer.OpaqueIdentifiers(TrackAFixture.Raw());

        Assert.Contains(identifiers, identifier => identifier.Category == "adapter LUID" && identifier.Value == "00000000000A0001");
        Assert.Contains(identifiers, identifier => identifier.Category == "PnP instance id" && identifier.Value == "4&test&0&0008");
        Assert.Contains(identifiers, identifier => identifier.Category == "monitor instance id" && identifier.Value == "1&test&0&UID1");
        Assert.Contains(identifiers, identifier => identifier.Category == "volume GUID" && identifier.Value == "{00000000-0000-0000-0000-000000000002}");
    }
}
