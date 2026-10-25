using System.Text.Json.Serialization;
using Mesura.Core.Raw.Serialization;

namespace Mesura.Core.Raw;

/// <summary>
/// The operating system, as read before any collection (architecture §4.4 <c>os</c>, §17.1). The
/// platform check (D011) decides from these facts alone.
/// </summary>
/// <param name="Platform">The OS family whose collector wrote the snapshot (D009).</param>
/// <param name="Edition">The edition identifier, e.g. <c>Professional</c>.</param>
/// <param name="Version">The feature-update version, e.g. <c>22H2</c>. Never used to decide support.</param>
/// <param name="Build">The build number from the real OS version, e.g. <c>19045</c>. The support floor is this number (D008).</param>
/// <param name="UpdateRevision">The update revision, e.g. <c>7727</c> in build 19045.7727.</param>
/// <param name="Architecture">The OS's native architecture, not the process's (an x64 process can run emulated on Arm64). Native: the platform's machine code.</param>
/// <param name="ProductType">Whether this is a client (workstation) or server installation.</param>
/// <param name="Session">Whether the scan ran on the local console or in a remote session.</param>
public sealed record OsInfo(
    OsPlatform Platform,
    Reading<string> Edition,
    Reading<string> Version,
    int Build,
    Reading<int> UpdateRevision,
    Reading<MachineArchitecture> Architecture,
    Reading<OsProductType> ProductType,
    Reading<SessionType> Session);

[JsonConverter(typeof(CamelCaseEnumConverter<OsPlatform>))]
public enum OsPlatform
{
    Windows,
}

[JsonConverter(typeof(CamelCaseEnumConverter<MachineArchitecture>))]
public enum MachineArchitecture
{
    X64,
    Arm64,
    X86,

    /// <summary>A machine code the collector doesn't name; the native code is kept.</summary>
    Other,
}

[JsonConverter(typeof(CamelCaseEnumConverter<OsProductType>))]
public enum OsProductType
{
    Workstation,

    /// <summary>A server or domain-controller installation.</summary>
    Server,
}

[JsonConverter(typeof(CamelCaseEnumConverter<SessionType>))]
public enum SessionType
{
    Local,
    Remote,
}
