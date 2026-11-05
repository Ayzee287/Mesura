using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mesura.Core.Windows;

/// <summary>One advanced property of an adapter as it stood when the snapshot was taken: the registry keyword and value, and the texts Windows shows.</summary>
public sealed record SnapshotProperty(string Keyword, string DisplayName, string RegistryValue, string DisplayValue);

/// <summary>
/// One physical adapter in a snapshot: who it is (the stable interface GUID is the key), which driver it ran, and every advanced property, protocol binding and the power switch exactly as
/// the driver reported them. What the driver lists as valid is deliberately not kept: a restore asks the driver that is there now.
/// </summary>
public sealed record SnapshotAdapter(
    string InterfaceGuid,
    string Name,
    string Description,
    string Vendor,
    AdapterKind Kind,
    string DriverVersion,
    string DriverDate,
    bool Up,
    IReadOnlyList<SnapshotProperty> Properties,
    IReadOnlyDictionary<string, bool> Bindings,
    bool? AllowTurnOff)
{
    public string Label => string.IsNullOrWhiteSpace(Description) ? Name : $"{Description} ({Name})";
}

/// <summary>
/// The complete state of every physical Ethernet and Wi-Fi adapter's settings at one moment (0.6): the "before" picture that a restore returns to. Today a run records only what it changed;
/// a snapshot is taken first, in full, so that anything done to an adapter by Mesura, by a driver installer or by hand can be seen (<see cref="NetworkRestorer.Plan"/>) and put back. Virtual,
/// VPN and other adapters are not part of it, because Mesura never reads or writes their settings.
/// </summary>
public sealed record NetworkSnapshot(int Version, DateTimeOffset TakenAt, string Reason, IReadOnlyList<SnapshotAdapter> Adapters)
{
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static NetworkSnapshot From(IEnumerable<AdapterReading> readings, DateTimeOffset takenAt, string reason) =>
        new(CurrentVersion, takenAt, reason, [.. readings.Where(Counts).Select(Of).OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ThenBy(a => a.InterfaceGuid, StringComparer.Ordinal)]);

    /// <summary>Whether Mesura reads and writes the settings of this adapter: physical Ethernet and Wi-Fi only.</summary>
    public static bool Counts(AdapterReading a) => a.Physical && a.Kind is AdapterKind.Ethernet or AdapterKind.WiFi;

    private static SnapshotAdapter Of(AdapterReading a) => new(
        a.InterfaceGuid, a.Name, a.Description, a.Vendor, a.Kind, a.DriverVersion, a.DriverDate, a.Up,
        [.. a.Properties.Values.OrderBy(p => p.Keyword, StringComparer.OrdinalIgnoreCase).Select(p => new SnapshotProperty(p.Keyword, p.DisplayName, p.RegistryValue, p.DisplayValue))],
        a.Bindings.OrderBy(b => b.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(b => b.Key, b => b.Value, StringComparer.OrdinalIgnoreCase),
        a.AllowTurnOff);

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    /// <summary>The snapshot in <paramref name="json"/>, or <c>null</c> when it is not one (not JSON, a newer version, or without adapters).</summary>
    public static NetworkSnapshot? FromJson(string json)
    {
        try
        {
            var snapshot = JsonSerializer.Deserialize<NetworkSnapshot>(json, Options);
            if (snapshot is null || snapshot.Version != CurrentVersion || snapshot.Adapters is null)
            {
                return null;
            }

            return snapshot with
            {
                Adapters = [.. snapshot.Adapters.Select(a => a with
                {
                    Properties = a.Properties ?? [],
                    Bindings = new Dictionary<string, bool>(a.Bindings ?? new Dictionary<string, bool>(), StringComparer.OrdinalIgnoreCase),
                })],
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
