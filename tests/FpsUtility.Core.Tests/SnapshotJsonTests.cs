using System.Globalization;
using System.Text;
using System.Text.Json;
using FpsUtility.Core.Raw;
using FpsUtility.Core.Raw.Serialization;

namespace FpsUtility.Core.Tests;

/// <summary>Serialisation and the §4.4 definition of "deterministic".</summary>
public sealed class SnapshotJsonTests
{
    private static readonly string GoldenPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "synthetic-snapshot.json");

    [Fact]
    public void The_same_snapshot_always_serialises_to_the_same_bytes()
    {
        var snapshot = SnapshotFixture.Create();

        Assert.Equal(SnapshotJson.Serialize(snapshot), SnapshotJson.Serialize(snapshot));
        Assert.Equal(SnapshotJson.Serialize(snapshot), SnapshotJson.Serialize(SnapshotFixture.Create()));
    }

    [Fact]
    public void Serialisation_does_not_depend_on_the_order_items_were_collected_in()
    {
        var inOrder = SnapshotJson.Serialize(SnapshotFixture.Create(reversed: false));
        var reversed = SnapshotJson.Serialize(SnapshotFixture.Create(reversed: true));

        Assert.Equal(inOrder, reversed);
    }

    [Fact]
    public void Reading_then_writing_gives_an_equal_object_and_identical_bytes()
    {
        var original = SnapshotFixture.Create();
        var json = SnapshotJson.Serialize(original);

        var roundTripped = SnapshotJson.Deserialize(json);

        Assert.Equivalent(original, roundTripped, strict: true);
        Assert.Equal(json, SnapshotJson.Serialize(roundTripped));
    }

    [Fact]
    public void A_string_with_a_lone_surrogate_still_round_trips_to_identical_bytes()
    {
        // OS strings (paths, registry text, error messages) can legally hold unpaired UTF-16 surrogates.
        var fixture = SnapshotFixture.Create();
        var snapshot = new RawSnapshot(
            fixture.SchemaVersion,
            fixture.CapturedAtUtc,
            new AppInfo("FpsUtility", "a\uD800b", Reading.Known("c\uDC00d")),
            fixture.Os,
            fixture.Elevated,
            fixture.Sources);

        var json = SnapshotJson.Serialize(snapshot);

        Assert.Equal(json, SnapshotJson.Serialize(SnapshotJson.Deserialize(json)));
    }

    [Fact]
    public void Serialisation_is_culture_invariant()
    {
        var snapshot = SnapshotFixture.Create();
        var invariant = SnapshotJson.Serialize(snapshot);

        var previous = CultureInfo.CurrentCulture;
        try
        {
            // Comma decimals, different digit grouping and date order.
            CultureInfo.CurrentCulture = new CultureInfo("ru-RU");
            Assert.Equal(invariant, SnapshotJson.Serialize(snapshot));
            CultureInfo.CurrentCulture = new CultureInfo("ar-SA");
            Assert.Equal(invariant, SnapshotJson.Serialize(snapshot));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    /// <summary>
    /// Pins the JSON contract: names, order, casing, encodings, layout. If this fails after an
    /// intended model change, review the new output and bump <see cref="RawSnapshot.CurrentSchemaVersion"/>
    /// if the change breaks readers.
    /// </summary>
    [Fact]
    public void The_json_contract_matches_the_reviewed_golden_file()
    {
        var expected = File.ReadAllBytes(GoldenPath);

        var actual = SnapshotJson.Serialize(SnapshotFixture.Create());

        Assert.Equal(Encoding.UTF8.GetString(expected), Encoding.UTF8.GetString(actual));
    }

    [Fact]
    public void Output_is_utf8_without_bom_with_lf_line_ends_and_one_trailing_newline()
    {
        var json = SnapshotJson.Serialize(SnapshotFixture.Create());

        Assert.False(json.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]));
        Assert.DoesNotContain((byte)'\r', json);
        Assert.Equal((byte)'\n', json[^1]);
        Assert.NotEqual((byte)'\n', json[^2]);
    }

    [Fact]
    public void Envelope_and_source_entries_keep_their_declared_property_order()
    {
        using var document = JsonDocument.Parse(SnapshotJson.Serialize(SnapshotFixture.Create()));
        var root = document.RootElement;

        Assert.Equal(["schemaVersion", "capturedAtUtc", "app", "os", "elevated", "sources"], Names(root));
        Assert.Equal(["display", "edid", "adapters", "smbios", "pcie", "storage", "steam", "power"], Names(root.GetProperty("sources")));
        Assert.Equal(["status", "source", "durationMs", "data"], Names(root.GetProperty("sources").GetProperty("display")));
        Assert.Equal(["status", "reason", "detail", "source", "durationMs"], Names(root.GetProperty("sources").GetProperty("power")));
    }

    [Fact]
    public void Enums_are_camel_case_strings_and_bytes_are_base64()
    {
        using var document = JsonDocument.Parse(SnapshotJson.Serialize(SnapshotFixture.Create()));
        var sources = document.RootElement.GetProperty("sources");

        Assert.Equal("unavailable", sources.GetProperty("power").GetProperty("status").GetString());
        Assert.Equal("timedOut", sources.GetProperty("power").GetProperty("reason").GetString());
        var output = sources.GetProperty("display").GetProperty("data").GetProperty("outputs")[0];
        Assert.Equal("displayPort", output.GetProperty("connection").GetProperty("value").GetString());
        Assert.Equal(10, output.GetProperty("connection").GetProperty("native").GetInt32());
        Assert.Equal("fwQAAQAA", sources.GetProperty("smbios").GetProperty("data").GetProperty("table").GetString());
    }

    [Fact]
    public void A_missing_reading_has_no_value_even_when_the_type_has_a_default()
    {
        using var document = JsonDocument.Parse(SnapshotJson.Serialize(SnapshotFixture.Create()));
        var link = document.RootElement.GetProperty("sources").GetProperty("pcie").GetProperty("data").GetProperty("devices")[1].GetProperty("link");

        var missingSpeed = link.GetProperty("currentSpeedGtps");
        Assert.False(missingSpeed.TryGetProperty("value", out _));
        Assert.Equal("parseFailed", missingSpeed.GetProperty("missing").GetString());
        Assert.Equal(0, missingSpeed.GetProperty("native").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(missingSpeed.GetProperty("detail").GetString()));
    }

    [Fact]
    public void A_known_zero_or_false_is_still_written()
    {
        using var document = JsonDocument.Parse(SnapshotJson.Serialize(SnapshotFixture.Create()));
        var adapters = document.RootElement.GetProperty("sources").GetProperty("adapters").GetProperty("data").GetProperty("adapters");

        Assert.False(adapters[0].GetProperty("isIntegrated").GetProperty("value").GetBoolean());
        Assert.Equal(0UL, adapters[1].GetProperty("dedicatedMemoryBytes").GetProperty("value").GetUInt64());
        Assert.False(adapters[1].GetProperty("dedicatedMemoryBytes").TryGetProperty("missing", out _));
    }

    [Theory]
    [InlineData("\"schemaVersion\": 1", "\"schemaVersion\": 2")] // a schema this build doesn't read
    [InlineData("\"elevated\": false,", "\"elevated\": false, \"extra\": 1,")] // an unknown property
    [InlineData("\"status\": \"ok\"", "\"status\": 0")] // an enum as an integer
    [InlineData("\"value\": \"TEST MONITOR\"", "\"value\": \"TEST MONITOR\", \"missing\": \"notPresent\", \"detail\": \"x\"")] // known and missing at once
    [InlineData("\"reason\": \"timedOut\",", "")] // an unavailable source without a reason
    [InlineData("\"value\": false", "\"native\": 255")] // a reading with neither value nor missing (would read as a known false)
    [InlineData("\"elevated\": false,", "\"elevated\": false, \"elevated\": true,")] // a duplicated property
    public void Reading_rejects_json_that_breaks_the_contract(string find, string replace)
    {
        var json = Encoding.UTF8.GetString(SnapshotJson.Serialize(SnapshotFixture.Create()));
        Assert.Contains(find, json, StringComparison.Ordinal);

        var broken = json.Replace(find, replace, StringComparison.Ordinal);

        Assert.Throws<SnapshotFormatException>(() => SnapshotJson.Deserialize(Encoding.UTF8.GetBytes(broken)));
    }

    private static string[] Names(JsonElement element) => [.. element.EnumerateObject().Select(property => property.Name)];
}
