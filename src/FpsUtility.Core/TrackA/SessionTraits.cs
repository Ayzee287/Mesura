using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using FpsUtility.Core.Checks;
using FpsUtility.Core.Facts;
using FpsUtility.Core.Raw;
using FpsUtility.Core.Raw.Serialization;

namespace FpsUtility.Core.TrackA;

[JsonConverter(typeof(CamelCaseEnumConverter<TraitState>))]
public enum TraitState
{
    Yes,
    No,

    /// <summary>The fact is missing: never read as yes or no.</summary>
    Unknown,
}

/// <summary>The form factor the firmware declares, in the check layer's classes (architecture §4.6 "Shared policies").</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<DeclaredFormFactor>))]
public enum DeclaredFormFactor
{
    FullDesktop,
    Compact,
    Portable,
    Unknown,
}

/// <summary>A graphics adapter's vendor by its PCI vendor ID (PCI-SIG assigned): model data, never a unit identity.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<GpuVendor>))]
public enum GpuVendor
{
    /// <summary>0x10DE.</summary>
    Nvidia,

    /// <summary>0x1002.</summary>
    Amd,

    /// <summary>0x8086.</summary>
    Intel,

    /// <summary>0x1414 (the Basic Render Driver and other software adapters).</summary>
    Microsoft,

    Other,

    /// <summary>The PCI identity is missing.</summary>
    Unknown,
}

[JsonConverter(typeof(CamelCaseEnumConverter<DisplayClass>))]
public enum DisplayClass
{
    Internal,
    External,
    Virtual,
    Unrecognised,

    /// <summary>The connection is missing.</summary>
    Unknown,
}

[JsonConverter(typeof(CamelCaseEnumConverter<SteamState>))]
public enum SteamState
{
    Registered,
    NotRegistered,

    /// <summary>The Steam source couldn't be read.</summary>
    Unknown,
}

public sealed record GpuTrait(GpuVendor Vendor, TraitState Integrated, TraitState Hardware);

/// <param name="HighestOfferedHz">The highest progressive refresh Windows offers the output, at any resolution.</param>
/// <param name="SignalRefreshHz">The current signal refresh, rounded to whole Hz for this inventory only (the facts keep the exact rational).</param>
public sealed record DisplayTrait(
    DisplayClass Kind,
    TraitState SharesSource,
    TraitState OnIntegratedGpu,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? HighestOfferedHz = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? SignalRefreshHz = null);

/// <param name="Media">Absent when the fact is missing.</param>
/// <param name="Bus">Absent when the fact is missing.</param>
public sealed record DiskTrait(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] MediaKind? Media = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BusKind? Bus = null);

/// <param name="Libraries">Absent when the list couldn't be read.</param>
/// <param name="Apps">How many app manifests the libraries hold (counts only: no names). Absent when unknown.</param>
public sealed record SteamTrait(
    SteamState Registration,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Libraries = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Apps = null);

/// <summary>
/// What one accepted session shows about its machine, reduced to a closed vocabulary for the cohort inventory
/// (architecture §4.7): enums, counts and whole Hz, never a model string, a name or an id. It is derived from the
/// session's normalised facts when the session is accepted and stored in its acceptance record, so the inventory doesn't
/// depend on the build that later rebuilds it.
/// </summary>
/// <param name="ModelSignature">
/// A SHA-256 of the machine's model identity (system and board model, processor models, graphics PCI identities). It
/// only tells whether two sessions of one cohort id show the same kind of machine; it identifies no unit, and every
/// PC of the same models has the same signature.
/// </param>
/// <param name="DiscreteGpu">
/// Yes when a hardware adapter that isn't integrated is present; no only when the adapter list is known and every
/// adapter is known to be software or integrated; otherwise unknown. Missing data never reads as "none".
/// </param>
public sealed record SessionTraits(
    DeclaredFormFactor FormFactor,
    TraitState Battery,
    IReadOnlyList<GpuTrait> Gpus,
    TraitState DiscreteGpu,
    IReadOnlyList<DisplayTrait> Displays,
    IReadOnlyList<DiskTrait> Disks,
    SteamTrait Steam,
    string ModelSignature,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WindowsFamily? Windows = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Build = null)
{
    private readonly bool _lists = Guard.NoNulls(Gpus, nameof(Gpus)) && Guard.NoNulls(Displays, nameof(Displays)) && Guard.NoNulls(Disks, nameof(Disks))
        && Steam is not null && ModelSignature is not null;

    public static SessionTraits From(NormalizedSnapshot facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var gpus = facts.Gpus.Value ?? [];
        var displays = facts.Displays.Value ?? [];

        return new SessionTraits(
            Windows: facts.Os.Build.IsKnown ? ManifestPlatform.FamilyOf(facts.Os.Build.Value) : null,
            Build: facts.Os.Build.IsKnown ? facts.Os.Build.Value : null,
            FormFactor: Policies.FormFactorOf(facts.Machine.ChassisType) switch
            {
                Checks.FormFactor.FullDesktop => DeclaredFormFactor.FullDesktop,
                Checks.FormFactor.Compact => DeclaredFormFactor.Compact,
                Checks.FormFactor.Portable => DeclaredFormFactor.Portable,
                _ => DeclaredFormFactor.Unknown,
            },
            Battery: State(facts.Power.BatteryPresent),
            Gpus: [.. gpus.Select(gpu => new GpuTrait(VendorOf(gpu.PciIdentity), State(gpu.IsIntegrated), State(gpu.IsHardware)))],
            DiscreteGpu: gpus.Any(gpu => gpu.IsHardware is { IsKnown: true, Value: true } && gpu.IsIntegrated is { IsKnown: true, Value: false })
                ? TraitState.Yes
                : facts.Gpus.IsKnown && gpus.All(gpu => gpu.IsHardware is { IsKnown: true, Value: false } || gpu.IsIntegrated is { IsKnown: true, Value: true })
                    ? TraitState.No
                    : TraitState.Unknown,
            Displays: [.. displays.Select(display => new DisplayTrait(
                KindOf(display.Connection),
                display.SharesSourceWith.IsKnown ? (display.SharesSourceWith.Value!.Count > 0 ? TraitState.Yes : TraitState.No) : TraitState.Unknown,
                OnIntegrated(display, gpus),
                HighestOfferedHz: display.OfferedModes.Value?.Where(mode => !mode.Interlaced).Select(mode => (int?)mode.RefreshHz).Max(),
                SignalRefreshHz: display.SignalRefresh.Value is { } rate ? (int)Math.Round((decimal)rate.Numerator / rate.Denominator, MidpointRounding.AwayFromZero) : null))],
            Disks: [.. (facts.Disks.Value ?? []).Select(disk => new DiskTrait(
                disk.MediaType.IsKnown ? disk.MediaType.Value : null,
                disk.BusType.IsKnown ? disk.BusType.Value : null))],
            Steam: new SteamTrait(
                facts.Steam.Registration.IsKnown
                    ? facts.Steam.Registration.Value == SteamRegistration.Registered ? SteamState.Registered : SteamState.NotRegistered
                    : SteamState.Unknown,
                facts.Steam.Libraries.Value?.Count,
                facts.Steam.Libraries.Value is { } libraries && libraries.All(library => library.Apps.IsKnown)
                    ? libraries.Sum(library => library.Apps.Value!.Count)
                    : null),
            ModelSignature: SignatureOf(facts));
    }

    public static GpuVendor VendorOf(Fact<PciFunctionId> identity) => identity.Value?.VendorId switch
    {
        null => GpuVendor.Unknown,
        0x10DE => GpuVendor.Nvidia,
        0x1002 => GpuVendor.Amd,
        0x8086 => GpuVendor.Intel,
        0x1414 => GpuVendor.Microsoft,
        _ => GpuVendor.Other,
    };

    private static DisplayClass KindOf(Fact<ConnectionKind> connection) => !connection.IsKnown
        ? DisplayClass.Unknown
        : Policies.KindOf(connection.Value) switch
        {
            DisplayKind.Internal => DisplayClass.Internal,
            DisplayKind.External => DisplayClass.External,
            DisplayKind.Virtual => DisplayClass.Virtual,
            _ => DisplayClass.Unrecognised,
        };

    private static TraitState OnIntegrated(DisplayFacts display, IReadOnlyList<GpuFacts> gpus) =>
        display.Gpu.IsKnown && gpus.FirstOrDefault(gpu => gpu.Id == display.Gpu.Value) is { IsIntegrated.IsKnown: true } gpu
            ? gpu.IsIntegrated.Value ? TraitState.Yes : TraitState.No
            : TraitState.Unknown;

    private static TraitState State(Fact<bool> fact) => fact.IsKnown ? (fact.Value ? TraitState.Yes : TraitState.No) : TraitState.Unknown;

    private static string SignatureOf(NormalizedSnapshot facts)
    {
        static string Show<T>(Fact<T> fact) => fact.IsKnown ? Convert.ToString(fact.Value, CultureInfo.InvariantCulture) ?? "" : "?";

        var lines = new List<string>
        {
            $"system|{Show(facts.Machine.SystemManufacturer)}|{Show(facts.Machine.SystemProduct)}",
            $"board|{Show(facts.Machine.BoardManufacturer)}|{Show(facts.Machine.BoardProduct)}",
        };
        lines.AddRange((facts.Processors.Value ?? []).Where(cpu => cpu.Populated.Value).Select(cpu => $"cpu|{Show(cpu.Version)}").Order(StringComparer.Ordinal));
        lines.AddRange((facts.Gpus.Value ?? []).Select(gpu => gpu.PciIdentity.Value is { } id
            ? string.Create(CultureInfo.InvariantCulture, $"gpu|{id.VendorId:x4}:{id.DeviceId:x4}:{id.RevisionId:x2}")
            : "gpu|?").Order(StringComparer.Ordinal));

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines))));
    }
}

/// <summary>
/// One real-hardware path through a check: its verdict and certainty, the condition key, and the applicability,
/// downgrade, gate and missing codes that led there. Descriptive: which paths real PCs have exercised, never a score.
/// </summary>
public sealed record CheckPath(
    string Check,
    Verdict Verdict,
    IReadOnlyList<string> Codes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Certainty? Certainty = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Condition = null)
{
    private readonly bool _lists = Guard.NoNulls(Codes, nameof(Codes)) && Check is not null;

    /// <summary>The distinct paths of one results document, ordered.</summary>
    public static IReadOnlyList<CheckPath> Of(CheckResults results)
    {
        ArgumentNullException.ThrowIfNull(results);

        return Distinct(results.Results.Select(result => new CheckPath(
            result.Check,
            result.Verdict,
            [.. result.Trace.Where(step => step.Kind != TraceKind.Comparison).Select(step => step.Code)
                .Concat(result.Missing.Select(missing => missing.Code))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            result.Certainty,
            result.Condition)));
    }

    public static IReadOnlyList<CheckPath> Distinct(IEnumerable<CheckPath> paths) =>
        [.. paths.DistinctBy(path => path.Key).OrderBy(path => path.Key, StringComparer.Ordinal)];

    /// <summary>A readable, comparable form: <c>display.refresh-below-max ok likely [on-battery, …]</c>.</summary>
    [JsonIgnore]
    public string Key => string.Join(' ', new[]
    {
        Check,
        JsonNamingPolicyName(Verdict),
        Certainty is { } certainty ? JsonNamingPolicyName(certainty) : null,
        Condition,
        "[" + string.Join(", ", Codes) + "]",
    }.Where(part => part is not null));

    private static string JsonNamingPolicyName<TEnum>(TEnum value)
        where TEnum : struct, Enum => System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(value.ToString());
}
