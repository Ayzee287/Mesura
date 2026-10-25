using System.Text.Json;
using Mesura.Core.Facts;

namespace Mesura.Core.Checks;

/// <summary>
/// A typed value in a check result's parameters or trace (architecture §4.6): a closed set, never an <c>object</c> bag.
/// Each kind is written as JSON exactly as the facts write the same value, so a parameter can be compared with the
/// evidence it came from. There is no text kind: a symbol is a token (an enum member's name, a snapshot-scoped id).
/// </summary>
public abstract record CheckValue
{
    private protected CheckValue()
    {
    }

    public static CheckValue Of(long value) => new IntegerValue(value);

    public static CheckValue Of(RefreshRate rate) => new RateValue(rate);

    public static CheckValue Of(PixelSize size) => new SizeValue(size);

    /// <summary>An enum member, as the facts name it (camelCase: <c>displayPort</c>).</summary>
    public static CheckValue Of<TEnum>(TEnum value)
        where TEnum : struct, Enum =>
        Enum.IsDefined(value)
            ? new SymbolValue(JsonNamingPolicy.CamelCase.ConvertName(value.ToString()))
            : throw new ArgumentOutOfRangeException(nameof(value), value, "Not a defined member.");

    /// <summary>A token: a snapshot-scoped id such as <c>gpu-2</c>, or a code.</summary>
    public static CheckValue Symbol(string symbol) => new SymbolValue(symbol);

    /// <summary>An ordered list of tokens, such as the ids of the discrete GPUs.</summary>
    public static CheckValue Symbols(IEnumerable<string> symbols) => new SymbolsValue([.. symbols]);
}

public sealed record IntegerValue : CheckValue
{
    internal IntegerValue(long value) => Value = value;

    public long Value { get; }
}

/// <summary>A refresh rate as the exact rational the facts hold.</summary>
public sealed record RateValue : CheckValue
{
    internal RateValue(RefreshRate rate) => Rate = rate ?? throw new ArgumentNullException(nameof(rate));

    public RefreshRate Rate { get; }
}

public sealed record SizeValue : CheckValue
{
    internal SizeValue(PixelSize size) => Size = size ?? throw new ArgumentNullException(nameof(size));

    public PixelSize Size { get; }
}

public sealed record SymbolValue : CheckValue
{
    internal SymbolValue(string value) => Value = Codes.Symbol(value, nameof(value));

    public string Value { get; }
}

public sealed record SymbolsValue : CheckValue
{
    internal SymbolsValue(IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (var value in values)
        {
            Codes.Symbol(value, nameof(values));
        }

        Values = values;
    }

    public IReadOnlyList<string> Values { get; }

    public bool Equals(SymbolsValue? other) => other is not null && Values.SequenceEqual(other.Values, StringComparer.Ordinal);

    public override int GetHashCode() => Values.Aggregate(Values.Count, (hash, value) => HashCode.Combine(hash, StringComparer.Ordinal.GetHashCode(value)));
}

/// <summary>A named value: a parameter, or one value a trace step records. The name is camelCase.</summary>
public sealed record NamedValue
{
    public NamedValue(string name, CheckValue value)
    {
        Name = Codes.Name(name, nameof(name));
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public string Name { get; }

    public CheckValue Value { get; }
}
