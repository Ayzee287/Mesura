using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Mesura.Core.Checks;
using Mesura.Core.Checks.Serialization;
using Mesura.Core.Facts;
using Mesura.Core.Facts.Serialization;
using Mesura.Core.Normalize;
using Mesura.Core.Raw;
using Mesura.Core.Raw.Serialization;
using Mesura.Core.Tests.Normalize;

namespace Mesura.Core.Tests.Checks;

/// <summary>
/// The pipeline tier (architecture §11, §4.6): raw snapshot → normaliser → checks → JSON. It proves what the semantic
/// tests can't: evidence leads back through the facts to the raw snapshot, the output is deterministic and pinned, and
/// nothing a check writes is a judgement word, an action, prose or a private identifier.
/// </summary>
public sealed class CheckContractTests
{
    private static readonly string GoldenPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "synthetic-results.json");
    private static readonly string RealFixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "scrubbed", "PC-000-20260928T223014Z.json");

    public static TheoryData<string> Snapshots() => ["synthetic", "PC-000", "desktop", "clone"];

    /// <summary>
    /// Pins the results contract: names, order, roles, codes, provenance. If it fails after an intended change, review
    /// the new output and bump <see cref="CheckResults.CurrentSchemaVersion"/> if readers break.
    /// </summary>
    [Fact]
    public void The_results_contract_matches_the_reviewed_golden_file()
    {
        var expected = File.ReadAllText(GoldenPath);

        var actual = Encoding.UTF8.GetString(Results(SnapshotFixture.Create()));

        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(Snapshots))]
    public void The_same_raw_snapshot_always_gives_the_same_bytes(string name)
    {
        var raw = Raw(name);

        Assert.Equal(Results(raw), Results(SnapshotJson.Deserialize(SnapshotJson.Serialize(raw))));
    }

    [Fact]
    public void The_order_items_were_collected_in_changes_nothing()
    {
        Assert.Equal(Results(SnapshotFixture.Create(reversed: false)), Results(SnapshotFixture.Create(reversed: true)));
    }

    [Theory]
    [InlineData("tr-TR")]
    [InlineData("de-DE")]
    [InlineData("ar-SA")]
    public void Results_are_culture_invariant(string culture)
    {
        var raw = Raw("desktop");
        var invariant = Results(raw);

        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            Assert.Equal(invariant, Results(raw));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    /// <summary>
    /// Each piece of evidence leads to its fact, and its copy is that fact's state exactly (value, basis, rule or gap,
    /// raw pointers), minus the English detail; every raw pointer resolves in the raw snapshot.
    /// </summary>
    [Theory]
    [MemberData(nameof(Snapshots))]
    public void Evidence_is_an_exact_copy_of_the_fact_it_points_at_and_leads_back_to_raw(string name)
    {
        var raw = Raw(name);
        var facts = Normalizer.Normalize(raw);
        var factsJson = JsonNode.Parse(FactsJson.Serialize(facts))!;
        var rawJson = JsonNode.Parse(SnapshotJson.Serialize(raw))!;
        using var results = JsonDocument.Parse(CheckResultsJson.Serialize(CheckRunner.Run(facts, FixtureGate.Validated)));

        var evidence = AllResults(results).SelectMany(result => result.GetProperty("evidence").EnumerateArray()).ToList();
        Assert.NotEmpty(evidence);
        foreach (var item in evidence)
        {
            var path = item.GetProperty("factPath").GetString()!;
            var fact = Resolve(factsJson, path) as JsonObject;
            Assert.True(fact is not null, $"{path} doesn't lead to a fact");
            Assert.False(item.TryGetProperty("detail", out _));

            foreach (var key in new[] { "value", "basis", "rule", "missing", "from" })
            {
                var copied = item.TryGetProperty(key, out var value) ? JsonNode.Parse(value.GetRawText()) : null;
                Assert.True(JsonNode.DeepEquals(fact![key], copied), $"{path}: '{key}' differs from the fact");
            }

            Assert.All(item.GetProperty("from").EnumerateArray(), pointer => Assert.True(Resolve(rawJson, pointer.GetString()!) is not null, $"{pointer} isn't in the raw snapshot"));
        }

        var missingFacts = AllResults(results).SelectMany(result => result.GetProperty("missing").EnumerateArray())
            .SelectMany(info => info.GetProperty("facts").EnumerateArray()).Select(pointer => pointer.GetString()!);
        Assert.All(missingFacts, pointer => Assert.True(Resolve(factsJson, pointer) is not null, pointer));
    }

    /// <summary>A parameter is never invented: it equals a value in the result's evidence or one its trace recorded.</summary>
    [Theory]
    [MemberData(nameof(Snapshots))]
    public void Every_parameter_comes_from_the_evidence_or_the_trace(string name)
    {
        using var results = JsonDocument.Parse(Results(Raw(name)));

        foreach (var result in AllResults(results))
        {
            var sources = result.GetProperty("evidence").EnumerateArray().Where(item => item.TryGetProperty("value", out _)).Select(item => item.GetProperty("value"))
                .Concat(result.GetProperty("trace").EnumerateArray().SelectMany(step => step.GetProperty("values").EnumerateObject().Select(value => value.Value)))
                .ToList();

            foreach (var parameter in result.GetProperty("params").EnumerateObject())
            {
                Assert.True(sources.Any(source => JsonElement.DeepEquals(source, parameter.Value)), $"{parameter.Name} has no source");
            }
        }
    }

    /// <summary>Confirmed, Likely, Couldn't check and Not applicable are check results, never the state of a fact.</summary>
    [Theory]
    [MemberData(nameof(Snapshots))]
    public void Verdicts_and_certainties_never_appear_as_fact_states(string name)
    {
        var raw = Raw(name);
        string[] judgements = ["confirmed", "likely", "cannotDetermine", "notApplicable", "couldntCheck", "problem", "ok", "verdict", "certainty"];
        using var facts = JsonDocument.Parse(FactsJson.Serialize(Normalizer.Normalize(raw)));
        using var results = JsonDocument.Parse(Results(raw));

        Assert.DoesNotContain(Tokens(facts.RootElement), token => judgements.Contains(token, StringComparer.OrdinalIgnoreCase));
        foreach (var item in AllResults(results).SelectMany(result => result.GetProperty("evidence").EnumerateArray()))
        {
            Assert.Subset(new HashSet<string> { "factPath", "role", "value", "basis", "rule", "missing", "from" }, item.EnumerateObject().Select(property => property.Name).ToHashSet());
            if (item.TryGetProperty("missing", out var gap))
            {
                Assert.Contains(gap.GetString(), Enum.GetNames<FactGap>().Select(JsonNamingPolicy.CamelCase.ConvertName));
            }
        }
    }

    /// <summary>
    /// No code a check writes judges, advises or claims beyond its evidence (§6.5 "must not claim", D015): no remedy, no
    /// score, no frame-rate or rendering claim.
    /// </summary>
    [Theory]
    [MemberData(nameof(Snapshots))]
    public void Result_codes_hold_no_judgement_advice_or_overclaim(string name)
    {
        HashSet<string> forbidden = ["fix", "repair", "recommend", "action", "should", "must", "score", "bad", "good", "wrong", "slow", "fast", "optimal",
            "optimise", "optimize", "bottleneck", "fps", "performance", "upgrade", "broken", "faulty", "misconfigured", "render", "game", "best", "worst"];
        using var results = JsonDocument.Parse(Results(Raw(name)));

        var codes = AllResults(results).SelectMany(result => new[] { result.GetProperty("findingCode").GetString()!, result.GetProperty("subject").GetString()! }
            .Concat(result.TryGetProperty("condition", out var condition) ? [condition.GetString()!] : [])
            .Concat(result.GetProperty("trace").EnumerateArray().SelectMany(step => step.GetProperty("values").EnumerateObject().Select(value => value.Name).Append(step.GetProperty("code").GetString()!)))
            .Concat(result.GetProperty("missing").EnumerateArray().Select(info => info.GetProperty("code").GetString()!))
            .Concat(result.GetProperty("params").EnumerateObject().Select(parameter => parameter.Name)));

        Assert.All(codes, code => Assert.DoesNotContain(Words(code), forbidden.Contains));
    }

    /// <summary>Decision A: the results carry no action, recommendation or verification plan, and no tallies (§6.3: Couldn't check is never counted with problems).</summary>
    [Theory]
    [MemberData(nameof(Snapshots))]
    public void Results_carry_no_actions_verification_or_counts(string name)
    {
        using var results = JsonDocument.Parse(Results(Raw(name)));

        var properties = Objects(results.RootElement).SelectMany(obj => obj.EnumerateObject()).Where(property => property.Name is not ("params" or "values"))
            .Select(property => property.Name.ToLowerInvariant()).Distinct().ToList();

        Assert.DoesNotContain(properties, property => property.Contains("action", StringComparison.Ordinal) || property.Contains("verif", StringComparison.Ordinal)
            || property.Contains("recommend", StringComparison.Ordinal) || property is "count" or "total" or "summary" or "score" or "detail");
    }

    [Theory]
    [MemberData(nameof(Snapshots))]
    public void Only_problems_and_oks_carry_a_certainty_and_the_products_gate_confirms_nothing(string name)
    {
        var results = CheckRunner.Run(Normalizer.Normalize(Raw(name)), FixtureGate.Validated);
        using var json = JsonDocument.Parse(CheckResultsJson.Serialize(results));

        Assert.DoesNotContain(results.Results, result => result.Certainty == Certainty.Confirmed);
        Assert.Empty(results.UnlockedConditions);
        foreach (var result in AllResults(json))
        {
            var decided = result.GetProperty("verdict").GetString() is "problem" or "ok";
            Assert.Equal(decided, result.TryGetProperty("certainty", out _));
        }
    }

    /// <summary>Opaque platform ids and payloads join facts; a result, like the facts, never carries them (§13).</summary>
    [Theory]
    [MemberData(nameof(Snapshots))]
    public void No_opaque_platform_id_or_raw_payload_reaches_the_results(string name)
    {
        var raw = Raw(name);
        var results = Encoding.UTF8.GetString(Results(raw));
        var sources = raw.Sources;

        string?[] opaque =
        [
            .. sources.Display.Data?.Outputs.SelectMany(output => new[] { output.AdapterId, output.MonitorDevicePath.Value, output.SourceName.Value, output.TargetId }) ?? [],
            .. sources.Adapters.Data?.Adapters.Select(adapter => adapter.AdapterId) ?? [],
            .. sources.Pcie.Data?.Devices.SelectMany(device => new[] { device.InstanceId, device.Parent.Value?.InstanceId }) ?? [],
            .. sources.Storage.Data?.Volumes.Select(volume => volume.Path) ?? [],
        ];

        Assert.All(opaque.Where(id => id is { Length: > 3 }), id => Assert.DoesNotContain(JsonEncodedText.Encode(id!).ToString(), results, StringComparison.Ordinal));
        Assert.DoesNotContain("\"bytes\"", results, StringComparison.Ordinal);
    }

    [Fact]
    public void Coverage_lists_every_check_and_says_which_ran()
    {
        var results = CheckRunner.Run(Normalizer.Normalize(SnapshotFixture.Create()), FixtureGate.Validated);

        Assert.Equal(CheckIds.All, results.Coverage.Select(entry => entry.Check));
        Assert.Equal(
            [CheckIds.RefreshBelowMax, CheckIds.OnIntegratedGpu],
            results.Coverage.Where(entry => entry.Status == CoverageStatus.Ran).Select(entry => entry.Check));
        Assert.Equal((1, NormalizedSnapshot.CurrentSchemaVersion, RawSnapshot.CurrentSchemaVersion), (results.SchemaVersion, results.FactsSchemaVersion, results.RawSchemaVersion));
    }

    [Fact]
    public void Through_the_pipeline_a_desktop_monitor_on_the_integrated_gpu_is_a_likely_problem_and_a_clone_is_lowered()
    {
        var desktop = CheckRunner.Run(Normalizer.Normalize(Raw("desktop")), FixtureGate.Validated).Results;
        var clone = CheckRunner.Run(Normalizer.Normalize(Raw("clone")), TestGates.All).Results;

        Assert.Equal(
            [(CheckIds.OnIntegratedGpu, "display-1", Verdict.Ok), (CheckIds.OnIntegratedGpu, "display-2", Verdict.Problem)],
            desktop.Where(result => result.Check == CheckIds.OnIntegratedGpu).Select(result => (result.Check, result.Subject, result.Verdict)));
        Assert.Contains(desktop.Single(result => result.Check == CheckIds.OnIntegratedGpu && result.Subject == "display-2").Trace, step => step.Code == "discrete-gpu-drives-other-display");
        var first = clone.Single(result => result.Check == CheckIds.RefreshBelowMax && result.Subject == "display-1");
        Assert.Equal((Verdict.Problem, Certainty.Likely), (first.Verdict, first.Certainty));
        Assert.Contains(first.Trace, step => step.Code == "shared-source");
    }

    [Fact]
    public void A_zero_hertz_signal_from_the_platform_is_couldnt_check_never_a_rate()
    {
        var raw = RawBuilder.With(sources =>
        {
            var output = sources.Display.Data!.Outputs[0];
            var zero = new DisplayOutput(output.AdapterId, output.SourceId, output.TargetId, output.SourceName, output.Connection, output.DesktopResolution,
                Reading.Known(new Rational(0, 1000)), output.PathRefresh, output.MonitorName, output.MonitorDevicePath, output.OfferedModes);
            return sources with { Display = RawBuilder.Ok(new DisplayData([zero, .. sources.Display.Data!.Outputs.Skip(1)])) };
        });

        var result = CheckRunner.Run(Normalizer.Normalize(raw), TestGates.All).Results.First(result => result.Check == CheckIds.RefreshBelowMax);

        Assert.Equal(Verdict.CannotDetermine, result.Verdict);
        Assert.Equal(FactGap.Implausible, result.Evidence.Single(item => item.FactPath == "/displays/value/0/signalRefresh").Missing);
    }

    private static byte[] Results(RawSnapshot raw) =>
        CheckResultsJson.Serialize(CheckRunner.Run(Normalizer.Normalize(raw), FixtureGate.Validated));

    /// <summary>
    /// The synthetic snapshot; PC-000's scrubbed fixture; the synthetic machine made a tower on AC with no battery and its
    /// HDMI monitor moved to the integrated GPU; and the synthetic machine with both outputs on one source (clone).
    /// </summary>
    private static RawSnapshot Raw(string name) => name switch
    {
        "synthetic" => SnapshotFixture.Create(),
        "PC-000" => SnapshotJson.Deserialize(File.ReadAllBytes(RealFixture)),
        "desktop" => RawBuilder.With(sources => sources with
        {
            Smbios = RawBuilder.Ok(RawBuilder.Smbios(RawBuilder.Structure(3, [0x01, 0x07], "Test"), RawBuilder.EndOfTable())),
            Power = RawBuilder.Ok(new PowerData(Reading.Known(true, 1), Reading.Known(false, 128))),
            Display = RawBuilder.Ok(new DisplayData([
                sources.Display.Data!.Outputs[0],
                Move(sources.Display.Data!.Outputs[1], adapterId: "00000000000A0002", sourceId: "0", signal: new Rational(60, 1), modes: [new(1920, 1080, 60, false)]),
            ])),
        }),
        "clone" => RawBuilder.With(sources => sources with
        {
            Display = RawBuilder.Ok(new DisplayData([
                sources.Display.Data!.Outputs[0],
                Move(sources.Display.Data!.Outputs[1], adapterId: "00000000000A0001", sourceId: "0", signal: new Rational(60, 1), modes: [new(2560, 1440, 60, false), new(2560, 1440, 165, false)], resolution: new Resolution(2560, 1440)),
            ])),
        }),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    /// <summary>An output moved to another adapter or source, running <paramref name="signal"/>, with its source's modes (and resolution: a clone shares its source's).</summary>
    private static DisplayOutput Move(DisplayOutput output, string adapterId, string sourceId, Rational signal, DisplayMode[] modes, Resolution? resolution = null) => new(
        adapterId, sourceId, output.TargetId, output.SourceName, output.Connection, resolution is null ? output.DesktopResolution : Reading.Known(resolution),
        Reading.Known(signal), Reading.Known(signal), output.MonitorName, output.MonitorDevicePath, Reading.Known<IReadOnlyList<DisplayMode>>(modes));

    private static IEnumerable<JsonElement> AllResults(JsonDocument results) => results.RootElement.GetProperty("results").EnumerateArray();

    /// <summary>RFC 6901: '/'-separated tokens, '~1' is '/', '~0' is '~'; array tokens are indices.</summary>
    private static JsonNode? Resolve(JsonNode root, string pointer)
    {
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
                return null;
            }
        }

        return node;
    }

    /// <summary>Every property name and every string value.</summary>
    private static IEnumerable<string> Tokens(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().SelectMany(property => Tokens(property.Value).Prepend(property.Name)),
        JsonValueKind.Array => element.EnumerateArray().SelectMany(Tokens),
        JsonValueKind.String => [element.GetString()!],
        _ => [],
    };

    private static IEnumerable<JsonElement> Objects(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().SelectMany(property => Objects(property.Value)).Prepend(element),
        JsonValueKind.Array => element.EnumerateArray().SelectMany(Objects),
        _ => [],
    };

    /// <summary>A camelCase, kebab-case or '/'-separated code as lower-case words.</summary>
    private static string[] Words(string code) =>
        [.. Regex.Split(code, "(?<=[a-z0-9])(?=[A-Z])|[-./]").Select(word => word.ToLowerInvariant())];
}
