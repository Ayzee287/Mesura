using System.Text.Json;
using Mesura.Core.Facts;
using Mesura.Core.Facts.Serialization;
using Mesura.Core.Normalize;

namespace Mesura.Core.Tests.Normalize;

/// <summary>The rules §4.2 sets for a fact, enforced by the model itself.</summary>
public sealed class FactModelTests
{
    [Fact]
    public void A_fact_is_known_with_a_basis_or_missing_with_a_reason_and_always_names_its_evidence()
    {
        var observed = Fact.Observed(0, Basis.Authoritative, "/os/build");
        var derived = Fact.Derived("gpu-1", Basis.Authoritative, Rules.DisplayGpu, "/a", "/b");
        var missing = Fact.Missing<int>(FactGap.Unmatched, "no candidate", "/a");

        Assert.True(observed.IsKnown);
        Assert.False(observed.IsDerived);
        Assert.Equal(0, observed.Value); // a known 0 is a value, not a gap
        Assert.True(derived.IsDerived);
        Assert.Equal(["/a", "/b"], derived.From);
        Assert.False(missing.IsKnown);
        Assert.Equal(FactGap.Unmatched, missing.Missing);
    }

    [Fact]
    public void A_fact_without_evidence_or_a_missing_fact_without_a_reason_cannot_exist()
    {
        Assert.Throws<ArgumentException>(() => Fact.Derived("x", Basis.Declared, Rules.SmbiosSystem));
        Assert.Throws<ArgumentException>(() => Fact.Missing<int>(FactGap.NotPresent, " ", "/a"));
        Assert.Throws<ArgumentException>(() => Fact.Observed("x", Basis.Declared, ""));
        Assert.Throws<ArgumentException>(() => Fact.Observed<string>(null!, Basis.Declared, "/a"));
    }

    [Fact]
    public void Json_shows_a_direct_fact_a_derived_fact_and_a_missing_fact_distinctly()
    {
        using var facts = JsonDocument.Parse(FactsJson.Serialize(Normalizer.Normalize(SnapshotFixture.Create())));
        var display = facts.RootElement.GetProperty("displays").GetProperty("value")[0];

        var direct = display.GetProperty("connection");
        Assert.Equal(["value", "basis", "from"], direct.EnumerateObject().Select(property => property.Name));

        var derived = display.GetProperty("gpu");
        Assert.Equal(["value", "basis", "rule", "from"], derived.EnumerateObject().Select(property => property.Name));
        Assert.Equal(Rules.DisplayGpu, derived.GetProperty("rule").GetString());

        var missing = facts.RootElement.GetProperty("power").GetProperty("onAcPower");
        Assert.Equal(["missing", "detail", "from"], missing.EnumerateObject().Select(property => property.Name));
        Assert.Equal("timedOut", missing.GetProperty("missing").GetString());
    }
}
