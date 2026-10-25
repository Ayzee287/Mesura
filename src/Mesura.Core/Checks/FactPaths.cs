using System.Globalization;
using System.Text.Json;
using Mesura.Core.Facts;
using Mesura.Core.Facts.Serialization;

namespace Mesura.Core.Checks;

/// <summary>
/// JSON Pointers into the normalised facts' JSON (<see cref="FactsJson"/>), built from the model's member names so
/// they can't drift from it: <c>/displays/value/0/signalRefresh</c>. A test proves each one leads to the fact whose
/// state the evidence copied.
/// </summary>
internal static class FactPaths
{
    public static string Session { get; } = Of(nameof(NormalizedSnapshot.Os), nameof(OsFacts.Session));

    public static string OnAcPower { get; } = Of(nameof(NormalizedSnapshot.Power), nameof(PowerFacts.OnAcPower));

    public static string BatteryPresent { get; } = Of(nameof(NormalizedSnapshot.Power), nameof(PowerFacts.BatteryPresent));

    public static string ChassisType { get; } = Of(nameof(NormalizedSnapshot.Machine), nameof(MachineFacts.ChassisType));

    public static string Displays { get; } = Of(nameof(NormalizedSnapshot.Displays));

    public static string Gpus { get; } = Of(nameof(NormalizedSnapshot.Gpus));

    public static string PciDevices { get; } = Of(nameof(NormalizedSnapshot.PciDevices));

    /// <summary>A member of the display at <paramref name="index"/> in <see cref="NormalizedSnapshot.Displays"/>.</summary>
    public static string Display(int index, string member) => Item(Displays, index, member);

    public static string Gpu(int index, string member) => Item(Gpus, index, member);

    public static string PciDevice(int index, string member) => Item(PciDevices, index, member);

    private static string Of(params string[] members) => string.Concat(members.Select(member => "/" + Camel(member)));

    private static string Item(string list, int index, string member) =>
        string.Create(CultureInfo.InvariantCulture, $"{list}/{FactsJson.ValueProperty}/{index}/{Camel(member)}");

    private static string Camel(string member) => JsonNamingPolicy.CamelCase.ConvertName(member);
}
