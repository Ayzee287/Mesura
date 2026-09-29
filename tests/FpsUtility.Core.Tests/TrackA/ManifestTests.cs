using System.Text;
using System.Text.Json.Nodes;
using FpsUtility.Core.TrackA;

namespace FpsUtility.Core.Tests.TrackA;

/// <summary>The capture manifest (spec §4): a strict, versioned document whose claims must agree with each other.</summary>
public sealed class ManifestTests
{
    private static readonly byte[] Valid = TrackAFixture.Package(TrackAFixture.Baseline()).Files[PackageFiles.Manifest];

    [Fact]
    public void A_valid_manifest_reads_back_to_the_same_bytes()
    {
        var manifest = TrackAJson.Deserialize<CaptureManifest>(Valid, CaptureManifest.CurrentSchemaVersion);

        Assert.Equal(Valid, TrackAJson.Serialize(manifest));
        Assert.Equal(CaptureManifest.FormatName, manifest.Format);
    }

    [Fact]
    public void It_records_every_item_the_spec_asks_for_and_no_hardware_identifier()
    {
        var node = JsonNode.Parse(Valid)!.AsObject();

        foreach (var name in new[] { "schemaVersion", "cohortId", "sessionId", "capturedAtUtc", "scenario", "producer", "platform", "sources", "stages", "checks", "determinism", "privacy", "privateMaterial", "annotations", "files", "evidence", "package" })
        {
            Assert.True(node.ContainsKey(name), name);
        }

        Assert.NotNull(node["producer"]!["commit"]);
        Assert.NotNull(node["producer"]!["schemas"]!["facts"]);
        Assert.NotNull(node["platform"]!["build"]);
        Assert.Equal("incoming", node["package"]!["fixture"]!.GetValue<string>());

        var text = Encoding.UTF8.GetString(Valid);
        Assert.DoesNotContain("adapter-", text, StringComparison.Ordinal);
        Assert.DoesNotContain("instance-", text, StringComparison.Ordinal);
        Assert.DoesNotContain("TEST NVME", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"schemaVersion":"1"}""")]
    public void A_malformed_manifest_is_refused(string json) =>
        Assert.Throws<TrackAFormatException>(() => TrackAJson.Deserialize<CaptureManifest>(Encoding.UTF8.GetBytes(json), CaptureManifest.CurrentSchemaVersion));

    [Theory]
    [InlineData("cohortId")]
    [InlineData("files")]
    [InlineData("privacy")]
    [InlineData("package")]
    public void A_missing_required_field_is_refused(string name) => Refused(node => node.AsObject().Remove(name));

    [Fact]
    public void An_unsupported_version_is_refused()
    {
        var error = Assert.Throws<TrackAFormatException>(() => Read(node => node["schemaVersion"] = 2));
        Assert.Contains("schemaVersion 2", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Undefined_values_and_extra_properties_are_refused()
    {
        Refused(node => node["scenario"]!["status"] = "passed");
        Refused(node => node["scenario"]!["id"] = "benchmark");
        Refused(node => node["cohortId"] = "PCB-001");
        Refused(node => node["hostLink"] = "gen4");
        Refused(node => node["files"]![0]!["sha256"] = "ABC");
        Refused(node => node["files"]![0]!["path"] = "normalized.json"); // the role names the file
    }

    [Fact]
    public void Claims_that_contradict_each_other_are_refused()
    {
        Refused(node => node["scenario"]!["status"] = "needsManualEvidence"); // nothing is missing, so it is completed
        Refused(node => node["package"]!["status"] = "notPromotable"); // no reason given
        Refused(node => node["privacy"]!["verdict"] = "passedWithUncheckedKinds"); // no kind listed
        Refused(node => node["checks"]!["statement"] = "All checks passed.");
        Refused(node => node["stages"]![0]!["stage"] = "benchmark");
    }

    [Fact]
    public void A_null_list_item_is_refused_in_every_track_a_document()
    {
        Refused(node => node["evidence"]!["files"] = new JsonArray((JsonNode?)null));
        Refused(node => node["package"]!["reasons"] = new JsonArray((JsonNode?)null));
        Refused(node => node["checks"]!["ran"] = new JsonArray((JsonNode?)null));

        var record = JsonNode.Parse(TrackAJson.Serialize(AcceptanceGateTests.Accept(TrackAFixture.Package(TrackAFixture.Baseline()))))!;
        record["checkPaths"] = new JsonArray((JsonNode?)null);
        Assert.Throws<TrackAFormatException>(() => TrackAJson.Deserialize<AcceptanceRecord>(Encoding.UTF8.GetBytes(record.ToJsonString()), AcceptanceRecord.CurrentSchemaVersion));
    }

    private static CaptureManifest Read(Action<JsonNode> change)
    {
        var node = JsonNode.Parse(Valid)!;
        change(node);
        return TrackAJson.Deserialize<CaptureManifest>(Encoding.UTF8.GetBytes(node.ToJsonString()), CaptureManifest.CurrentSchemaVersion);
    }

    private static void Refused(Action<JsonNode> change) => Assert.Throws<TrackAFormatException>(() => Read(change));
}
