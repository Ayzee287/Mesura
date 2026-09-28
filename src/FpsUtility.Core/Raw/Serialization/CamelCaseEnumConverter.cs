using System.Text.Json;
using System.Text.Json.Serialization;

namespace FpsUtility.Core.Raw.Serialization;

/// <summary>
/// Writes an enum as its camelCase member name (<c>notSupported</c>, <c>displayPort</c>). Integer
/// values are rejected in both directions, so an undefined member can never be written or read.
/// </summary>
public sealed class CamelCaseEnumConverter<TEnum> : JsonStringEnumConverter<TEnum>
    where TEnum : struct, Enum
{
    public CamelCaseEnumConverter()
        : base(JsonNamingPolicy.CamelCase, allowIntegerValues: false)
    {
    }
}
