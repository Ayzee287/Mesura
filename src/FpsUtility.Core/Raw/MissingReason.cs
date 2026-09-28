using System.Text.Json.Serialization;
using FpsUtility.Core.Raw.Serialization;

namespace FpsUtility.Core.Raw;

/// <summary>
/// Why a value or a whole source is unavailable (architecture §4.2, §10). A missing value is data,
/// never a hardware finding (D008).
/// </summary>
[JsonConverter(typeof(CamelCaseEnumConverter<MissingReason>))]
public enum MissingReason
{
    /// <summary>The source answered, and it has no such value or object (no Steam install, no EDID, no link property).</summary>
    NotPresent,

    /// <summary>The platform, its version or the driver doesn't offer this value at all.</summary>
    NotSupported,

    /// <summary>The value needs rights the collecting process doesn't have.</summary>
    AccessDenied,

    /// <summary>The call failed; <c>detail</c> carries the error.</summary>
    SourceFailed,

    /// <summary>The source didn't answer within its time limit.</summary>
    TimedOut,

    /// <summary>The source answered with a value outside what the platform defines (reserved for the normaliser's sanity bounds).</summary>
    Implausible,

    /// <summary>The source answered with a value or structure the collector could not decode.</summary>
    ParseFailed,

    /// <summary>
    /// The source answered, and the collector deliberately didn't keep the value, because in this case it
    /// holds something private (for example a virtual disk's location, which is its image file's path; §13).
    /// Added in schema 2.
    /// </summary>
    Withheld,
}
