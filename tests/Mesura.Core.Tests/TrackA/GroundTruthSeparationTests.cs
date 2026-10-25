using System.Reflection;
using System.Text;
using Mesura.Core.Facts;
using Mesura.Core.Normalize;
using Mesura.Core.Raw;
using Mesura.Core.TrackA;

namespace Mesura.Core.Tests.TrackA;

/// <summary>
/// Spec §16: an operator observation can never become a normalised fact. Proven three ways: the normaliser's only input
/// is the raw snapshot; nothing in the facts, the normaliser or the checks can even name a Track A type; and captures of
/// one scan with contradicting answers give byte-identical facts and results.
/// </summary>
public sealed class GroundTruthSeparationTests
{
    [Fact]
    public void The_normaliser_takes_the_raw_snapshot_and_nothing_else()
    {
        var normalize = Assert.Single(typeof(Normalizer).GetMethods(BindingFlags.Public | BindingFlags.Static), method => method.Name == nameof(Normalizer.Normalize));

        Assert.Equal([typeof(RawSnapshot)], normalize.GetParameters().Select(parameter => parameter.ParameterType));
    }

    [Fact]
    public void No_fact_normaliser_or_check_type_refers_to_a_track_a_type()
    {
        var product = typeof(Normalizer).Assembly.GetTypes()
            .Where(type => type.Namespace is "Mesura.Core.Facts" or "Mesura.Core.Normalize" or "Mesura.Core.Checks"
                || (type.Namespace?.StartsWith("Mesura.Core.Facts.", StringComparison.Ordinal) ?? false)
                || (type.Namespace?.StartsWith("Mesura.Core.Checks.", StringComparison.Ordinal) ?? false))
            .ToList();
        Assert.NotEmpty(product);

        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (var type in product)
        {
            var referenced = type.GetFields(all).Select(field => field.FieldType)
                .Concat(type.GetProperties(all).Select(property => property.PropertyType))
                .Concat(type.GetMethods(all).SelectMany(method => method.GetParameters().Select(parameter => parameter.ParameterType).Append(method.ReturnType)))
                .Concat(type.GetConstructors(all).SelectMany(constructor => constructor.GetParameters().Select(parameter => parameter.ParameterType)));

            Assert.DoesNotContain(referenced.SelectMany(Unwrap), used => used.Namespace == "Mesura.Core.TrackA");
        }
    }

    [Fact]
    public void Contradicting_answers_leave_the_facts_and_results_byte_identical()
    {
        var prepared = TrackAFixture.Prepare();
        Observation[] answers(string slot, string busInterface, string link) =>
        [
            TrackAFixture.Consent(),
            TrackAFixture.Answer(ObservationCatalog.IdleProcedureFollowed, "yes"),
            TrackAFixture.Answer(ObservationCatalog.PhysicalPcieSlot, slot),
            TrackAFixture.Answer(ObservationCatalog.PcieSlotManualWidth, "x16"),
            TrackAFixture.Answer(ObservationCatalog.GpuZBusInterface, busInterface),
            TrackAFixture.Answer(ObservationCatalog.HwinfoGpuLink, link),
            TrackAFixture.Answer(ObservationCatalog.OperatorNote, "GPU-Z showed Gen4 x16 under load"),
        ];

        var one = TrackAFixture.Package(TrackAFixture.Capture(ScenarioCatalog.PcieIdleLoad, ScenarioCatalog.IdleStep, prepared,
            answers: answers("PCIEX16_1", "PCIe x16 4.0 @ x16 4.0", "current x16 16 GT/s")));
        var other = TrackAFixture.Package(TrackAFixture.Capture(ScenarioCatalog.PcieIdleLoad, ScenarioCatalog.IdleStep, prepared,
            answers: answers("PCIEX4", "PCIe x4 3.0 @ x1 1.1", "current x1 2.5 GT/s")));

        Assert.Equal(one.Files[PackageFiles.Facts], other.Files[PackageFiles.Facts]);
        Assert.Equal(one.Files[PackageFiles.Results], other.Files[PackageFiles.Results]);
        Assert.NotEqual(one.Files[PackageFiles.Observations], other.Files[PackageFiles.Observations]);
    }

    [Fact]
    public void An_external_tool_s_reading_creates_no_link_fact()
    {
        // The synthetic integrated GPU's PCI function reports no link at all. GPU-Z "saying" Gen4 x16 changes nothing.
        var package = TrackAFixture.Package(TrackAFixture.Capture(ScenarioCatalog.PcieIdleLoad, ScenarioCatalog.IdleStep,
            answers: [TrackAFixture.Consent(), TrackAFixture.Answer(ObservationCatalog.GpuZBusInterface, "PCIe x16 4.0 @ x16 4.0")]));
        var facts = Encoding.UTF8.GetString(package.Files[PackageFiles.Facts]);

        Assert.DoesNotContain("x16 4.0", facts, StringComparison.Ordinal);
        Assert.DoesNotContain("gpuZ", facts, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("userProvided", facts, StringComparison.Ordinal);
        Assert.Equal([Basis.Authoritative, Basis.Declared], Enum.GetValues<Basis>()); // no basis a person could supply exists yet
    }

    private static IEnumerable<Type> Unwrap(Type type)
    {
        yield return type;
        if (type.HasElementType)
        {
            foreach (var inner in Unwrap(type.GetElementType()!))
            {
                yield return inner;
            }
        }

        if (type.IsGenericType)
        {
            foreach (var inner in type.GetGenericArguments().SelectMany(Unwrap))
            {
                yield return inner;
            }
        }
    }
}
