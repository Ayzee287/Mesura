using FpsUtility.Core.Facts;
using FpsUtility.Core.Facts.Serialization;
using FpsUtility.Core.Normalize;
using FpsUtility.Core.Raw;

namespace FpsUtility.Core.Tests.Normalize;

/// <summary>
/// M8, the shared source (<see cref="Rules.DisplaySharedSource"/>): which active outputs show one desktop source
/// (duplicate or "clone" mode), from the raw adapter and source ids, which never enter the facts themselves.
/// </summary>
public sealed class SharedSourceNormalizationTests
{
    [Fact]
    public void Outputs_on_different_sources_are_independent_a_known_empty_list()
    {
        var displays = Normalizer.Normalize(SnapshotFixture.Create()).Displays.Value!; // sources 0 and 1 on one adapter

        Assert.All(displays, display =>
        {
            Assert.Equal([], display.SharesSourceWith.Value!);
            Assert.Equal(Rules.DisplaySharedSource, display.SharesSourceWith.Rule);
            Assert.Equal(Basis.Authoritative, display.SharesSourceWith.Basis);
        });
        Assert.Equal(
            ["/sources/display/data/outputs/0/adapterId", "/sources/display/data/outputs/0/sourceId", "/sources/display/data/outputs"],
            displays[0].SharesSourceWith.From);
    }

    [Fact]
    public void Two_outputs_on_one_source_each_name_the_other_with_the_ids_they_were_joined_by()
    {
        var displays = Normalize(("A", "0", "1"), ("A", "0", "2"));

        Assert.Equal(["display-2"], displays[0].SharesSourceWith.Value!);
        Assert.Equal(["display-1"], displays[1].SharesSourceWith.Value!);
        Assert.Equal(
            [
                "/sources/display/data/outputs/0/adapterId", "/sources/display/data/outputs/0/sourceId",
                "/sources/display/data/outputs/1/adapterId", "/sources/display/data/outputs/1/sourceId",
            ],
            displays[0].SharesSourceWith.From);
    }

    [Fact]
    public void A_source_id_is_scoped_to_its_adapter()
    {
        var displays = Normalize(("A", "0", "1"), ("B", "0", "1"));

        Assert.All(displays, display => Assert.Equal([], display.SharesSourceWith.Value!));
    }

    [Fact]
    public void A_three_way_clone_lists_the_others_in_output_order_whatever_order_they_were_collected_in()
    {
        var inOrder = Normalize(("A", "0", "1"), ("A", "0", "2"), ("A", "0", "3"), ("A", "1", "4"));
        var reversed = Normalize(("A", "1", "4"), ("A", "0", "3"), ("A", "0", "2"), ("A", "0", "1"));

        Assert.Equal(["display-1", "display-3"], inOrder[1].SharesSourceWith.Value!);
        Assert.Equal([], inOrder[3].SharesSourceWith.Value!);
        Assert.Equal(
            inOrder.Select(display => display.SharesSourceWith.Value!.ToList()),
            reversed.Select(display => display.SharesSourceWith.Value!.ToList()));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void An_empty_source_id_leaves_outputs_on_its_adapter_ambiguous_and_other_adapters_unaffected(string sourceId)
    {
        var displays = Normalize(("A", sourceId, "1"), ("A", "0", "2"), ("C", "0", "3"));

        Assert.Equal(FactGap.Implausible, displays[0].SharesSourceWith.Missing);
        Assert.Equal(FactGap.Ambiguous, displays[1].SharesSourceWith.Missing); // it may show the unknown source
        Assert.Equal([], displays[2].SharesSourceWith.Value!);
    }

    [Fact]
    public void An_empty_adapter_id_leaves_every_other_output_ambiguous()
    {
        var displays = Normalize(("", "0", "1"), ("A", "0", "2"), ("C", "0", "3"));

        Assert.Equal(FactGap.Implausible, displays[0].SharesSourceWith.Missing);
        Assert.All(displays.Skip(1), display => Assert.Equal(FactGap.Ambiguous, display.SharesSourceWith.Missing));
    }

    [Fact]
    public void A_target_listed_twice_makes_its_source_ambiguous_not_a_clone()
    {
        var displays = Normalize(("A", "0", "1"), ("A", "0", "1"), ("A", "1", "2"));

        Assert.All(displays.Take(2), display => Assert.Equal(FactGap.Ambiguous, display.SharesSourceWith.Missing));
        Assert.Equal([], displays[2].SharesSourceWith.Value!);
        Assert.Contains("/sources/display/data/outputs/1/targetId", displays[0].SharesSourceWith.From);
    }

    [Fact]
    public void The_opaque_ids_never_reach_the_facts()
    {
        var raw = Raw(("adapter-LUID-0001", "7", "target-1"), ("adapter-LUID-0001", "7", "target-2"));

        var json = System.Text.Encoding.UTF8.GetString(FactsJson.Serialize(Normalizer.Normalize(raw)));

        Assert.DoesNotContain("adapter-LUID-0001", json, StringComparison.Ordinal);
        Assert.DoesNotContain("target-1", json, StringComparison.Ordinal);
    }

    private static IReadOnlyList<DisplayFacts> Normalize(params (string Adapter, string Source, string Target)[] outputs) =>
        Normalizer.Normalize(Raw(outputs)).Displays.Value!;

    /// <summary>
    /// The synthetic snapshot's first output, once per (adapter, source, target). The monitor path follows the target, so
    /// an output keeps its place in the canonical order (§4.4) whatever order it is listed in here.
    /// </summary>
    private static RawSnapshot Raw(params (string Adapter, string Source, string Target)[] outputs) =>
        RawBuilder.With(sources =>
        {
            var template = sources.Display.Data!.Outputs[0];
            return sources with
            {
                Display = RawBuilder.Ok(new DisplayData([.. outputs.Select(ids => new DisplayOutput(
                    ids.Adapter, ids.Source, ids.Target, template.SourceName, template.Connection, template.DesktopResolution,
                    template.SignalRefresh, template.PathRefresh, template.MonitorName,
                    Reading.Known($@"\\?\DISPLAY#TST{ids.Target}#test"), template.OfferedModes))])),
            };
        });
}
