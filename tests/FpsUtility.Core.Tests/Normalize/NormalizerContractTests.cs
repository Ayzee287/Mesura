using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FpsUtility.Core.Facts;
using FpsUtility.Core.Facts.Serialization;
using FpsUtility.Core.Normalize;
using FpsUtility.Core.Raw;
using FpsUtility.Core.Raw.Serialization;

namespace FpsUtility.Core.Tests.Normalize;

/// <summary>
/// The contract of the whole normalised output (architecture §4.4 "As built in Milestone 2"): pinned JSON,
/// determinism, provenance that resolves, no private identifier, no verdict.
/// </summary>
public sealed class NormalizerContractTests
{
    private static readonly string GoldenPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "synthetic-facts.json");
    private static readonly string RealFixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "scrubbed", "PC-000-20260928T223014Z.json");

    public static TheoryData<string> Snapshots() => ["synthetic", "PC-000"];

    /// <summary>
    /// Pins the facts contract: names, order, basis, rules, provenance. If it fails after an intended change,
    /// review the new output and bump <see cref="NormalizedSnapshot.CurrentSchemaVersion"/> if readers break.
    /// </summary>
    [Fact]
    public void The_facts_contract_matches_the_reviewed_golden_file()
    {
        var expected = File.ReadAllText(GoldenPath);

        var actual = Encoding.UTF8.GetString(FactsJson.Serialize(Normalizer.Normalize(SnapshotFixture.Create())));

        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(Snapshots))]
    public void The_same_raw_snapshot_always_gives_the_same_bytes(string name)
    {
        var raw = Raw(name);

        var once = FactsJson.Serialize(Normalizer.Normalize(raw));
        var again = FactsJson.Serialize(Normalizer.Normalize(SnapshotJson.Deserialize(SnapshotJson.Serialize(raw))));

        Assert.Equal(once, again);
    }

    [Fact]
    public void The_order_items_were_collected_in_changes_nothing()
    {
        var inOrder = FactsJson.Serialize(Normalizer.Normalize(SnapshotFixture.Create(reversed: false)));
        var reversed = FactsJson.Serialize(Normalizer.Normalize(SnapshotFixture.Create(reversed: true)));

        Assert.Equal(inOrder, reversed);
    }

    [Fact]
    public void Normalising_is_culture_invariant()
    {
        var raw = SnapshotFixture.Create();
        var invariant = FactsJson.Serialize(Normalizer.Normalize(raw));

        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("ar-SA");
            Assert.Equal(invariant, FactsJson.Serialize(Normalizer.Normalize(raw)));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    /// <summary>Every fact says where it comes from, and every pointer names something that exists in the raw snapshot.</summary>
    [Theory]
    [MemberData(nameof(Snapshots))]
    public void Every_fact_names_raw_evidence_that_exists(string name)
    {
        var raw = Raw(name);
        var rawJson = JsonNode.Parse(SnapshotJson.Serialize(raw))!;
        using var facts = JsonDocument.Parse(FactsJson.Serialize(Normalizer.Normalize(raw)));

        var pointers = AllFacts(facts.RootElement).SelectMany(fact => fact.GetProperty("from").EnumerateArray().Select(pointer => pointer.GetString()!)).ToList();

        Assert.NotEmpty(pointers);
        Assert.All(pointers, pointer => Assert.True(Resolves(rawJson, pointer), $"{pointer} doesn't exist in the raw snapshot"));
    }

    [Theory]
    [MemberData(nameof(Snapshots))]
    public void Every_known_fact_has_a_basis_and_every_missing_one_says_why(string name)
    {
        using var facts = JsonDocument.Parse(FactsJson.Serialize(Normalizer.Normalize(Raw(name))));

        Assert.All(AllFacts(facts.RootElement), fact =>
        {
            if (fact.TryGetProperty("value", out _))
            {
                Assert.Contains(fact.GetProperty("basis").GetString(), new[] { "authoritative", "declared" });
                Assert.False(fact.TryGetProperty("missing", out _));
            }
            else
            {
                Assert.False(string.IsNullOrWhiteSpace(fact.GetProperty("detail").GetString()));
                Assert.False(fact.TryGetProperty("basis", out _));
            }
        });
    }

    /// <summary>Opaque platform ids join facts but never appear in them; neither do EDID or SMBIOS bytes (§13).</summary>
    [Theory]
    [MemberData(nameof(Snapshots))]
    public void No_opaque_platform_id_or_raw_payload_reaches_the_facts(string name)
    {
        var raw = Raw(name);
        var facts = Encoding.UTF8.GetString(FactsJson.Serialize(Normalizer.Normalize(raw)));
        var sources = raw.Sources;

        string?[] opaque =
        [
            .. sources.Display.Data?.Outputs.SelectMany(output => new[] { output.AdapterId, output.MonitorDevicePath.Value, output.SourceName.Value }) ?? [],
            .. sources.Adapters.Data?.Adapters.Select(adapter => adapter.AdapterId) ?? [],
            .. sources.Pcie.Data?.Devices.SelectMany(device => new[] { device.InstanceId, device.Parent.Value?.InstanceId }) ?? [],
            .. sources.Storage.Data?.Volumes.Select(volume => volume.Path) ?? [],
        ];

        Assert.All(opaque.Where(id => id is not null), id => Assert.DoesNotContain(JsonEncodedText.Encode(id!).ToString(), facts, StringComparison.Ordinal));
        Assert.DoesNotContain("\"bytes\"", facts, StringComparison.Ordinal);
        Assert.DoesNotContain("\"table\":", facts, StringComparison.Ordinal);
        if (sources.Smbios.Data is { } smbios)
        {
            Assert.DoesNotContain(Convert.ToBase64String(smbios.Table), facts, StringComparison.Ordinal);
        }
    }

    /// <summary>The boundary (§6.3, D015): facts carry no verdict, certainty, score, recommendation or rated RAM speed.</summary>
    [Theory]
    [InlineData("confirmed")]
    [InlineData("likely")]
    [InlineData("couldntCheck")]
    [InlineData("notApplicable")]
    [InlineData("verdict")]
    [InlineData("certainty")]
    [InlineData("score")]
    [InlineData("recommend")]
    [InlineData("rated")]
    [InlineData("xmp")]
    [InlineData("expo")]
    [InlineData("maxRefresh")]
    public void Facts_hold_no_judgement(string word)
    {
        var forbidden = Words(word);
        foreach (var name in new[] { "synthetic", "PC-000" })
        {
            using var facts = JsonDocument.Parse(FactsJson.Serialize(Normalizer.Normalize(Raw(name))));
            Assert.DoesNotContain(PropertyNamesAndRules(facts.RootElement), property => ContainsWords(Words(property), forbidden));
        }
    }

    [Fact]
    public void Subject_ids_are_unique_within_a_snapshot()
    {
        using var facts = JsonDocument.Parse(FactsJson.Serialize(Normalizer.Normalize(Raw("PC-000"))));

        var ids = Objects(facts.RootElement).Where(element => element.TryGetProperty("id", out _)).Select(element => element.GetProperty("id").GetString()).ToList();

        Assert.NotEmpty(ids);
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    private static RawSnapshot Raw(string name) =>
        name == "synthetic" ? SnapshotFixture.Create() : SnapshotJson.Deserialize(File.ReadAllBytes(RealFixture));

    /// <summary>RFC 6901: '/'-separated tokens, '~1' is '/', '~0' is '~'; array tokens are indices.</summary>
    private static bool Resolves(JsonNode root, string pointer)
    {
        if (!pointer.StartsWith('/'))
        {
            return false;
        }

        JsonNode? node = root;
        foreach (var token in pointer[1..].Split('/').Select(part => part.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)))
        {
            node = node switch
            {
                JsonObject obj when obj.ContainsKey(token) => obj[token],
                JsonArray array when int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index < array.Count => array[index],
                _ => null,
            };

            if (node is null)
            {
                return false;
            }
        }

        return true;
    }

    private static IEnumerable<JsonElement> AllFacts(JsonElement element) =>
        Objects(element).Where(obj => obj.TryGetProperty("from", out _));

    private static IEnumerable<JsonElement> Objects(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            yield return element;
            foreach (var child in element.EnumerateObject().SelectMany(property => Objects(property.Value)))
            {
                yield return child;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray().SelectMany(Objects))
            {
                yield return child;
            }
        }
    }

    /// <summary>Every property name, and every rule id: the vocabulary of the facts, not their data.</summary>
    private static IEnumerable<string> PropertyNamesAndRules(JsonElement element) =>
        Objects(element).SelectMany(obj => obj.EnumerateObject())
            .SelectMany(property => property.Name == "rule" ? [property.Name, property.Value.GetString()!] : new[] { property.Name });

    /// <summary>A camelCase, kebab-case or dotted name as lower-case words: "isIntegrated" is "is", "integrated", never "rated".</summary>
    private static string[] Words(string name) =>
        [.. System.Text.RegularExpressions.Regex.Split(name, "(?<=[a-z0-9])(?=[A-Z])|[-.]").Select(word => word.ToLowerInvariant())];

    private static bool ContainsWords(string[] words, string[] sequence) =>
        Enumerable.Range(0, Math.Max(0, words.Length - sequence.Length + 1)).Any(start => words.Skip(start).Take(sequence.Length).SequenceEqual(sequence));
}
