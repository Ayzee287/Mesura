using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Mesura.App;

/// <summary>The tones a state can have: each has a text colour and a subtle background in the theme.</summary>
public static class Tone
{
    public const string Ok = "ok";
    public const string Warn = "warn";
    public const string Danger = "danger";
    public const string Muted = "muted";
    public const string Info = "info";
    public const string Accent = "accent";
    public const string Neutral = "neutral";

    /// <summary>
    /// The tone of a state: one of the tone names, or a status word from the setup layer (<c>StepWords</c>, which the
    /// history and the CLI share), so a state reads the same everywhere: done in green, waiting in amber, failed in red,
    /// not applicable dimmed, guided or manual in blue.
    /// </summary>
    public static string Of(string? state) => (state ?? "").ToLowerInvariant() switch
    {
        Ok or Warn or Danger or Muted or Info or Accent or Neutral => state!.ToLowerInvariant(),
        "installed" or "applied" or "already done" or "reverted" or "succeeded" or "already installed" or "done" => Ok,
        "partly applied" or "partial" or "needs admin" or "waiting" or "run again" or "incomplete" or "restart needed" => Warn,
        "blocked" or "failed" or "unknown" or "no winget" or "needs attention" => Danger,
        "not applicable" or "n/a" or "cancelled" or "can't detect" or "checking…" or "left as is" => Muted,
        "guided" or "manual" or "info" or "to finish" => Info,
        "would change" => Accent,
        _ => Neutral,
    };
}

/// <summary>
/// Colours a state the same way everywhere (<see cref="Tone.Of"/>): the text colour, or with
/// <see cref="Background"/> the subtle fill of a pill or banner.
/// </summary>
public sealed class StateBrushConverter : IValueConverter
{
    public bool Background { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var tone = Tone.Of(value as string);
        var key = (tone, Background) switch
        {
            (Tone.Ok, false) => "Success",
            (Tone.Ok, true) => "SuccessSubtle",
            (Tone.Warn, false) => "Warning",
            (Tone.Warn, true) => "WarningSubtle",
            (Tone.Danger, false) => "Danger",
            (Tone.Danger, true) => "DangerSubtle",
            (Tone.Muted, false) => "TextTertiary",
            (Tone.Info, false) => "Info",
            (Tone.Info, true) => "InfoSubtle",
            (Tone.Accent, false) => "Accent",
            (Tone.Accent, true) => "AccentSubtle",
            (_, true) => "NeutralSubtle",
            _ => "TextSecondary",
        };
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Visible when the value is a non-empty string, a non-empty collection, a positive number, or true.</summary>
public sealed class PresenceToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var present = value switch
        {
            null => false,
            bool b => b,
            string s => s.Length > 0,
            int i => i > 0,
            System.Collections.ICollection c => c.Count > 0,
            _ => true,
        };
        if (parameter is "invert")
        {
            present = !present;
        }

        return present ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>A boolean the other way round (for IsEnabled bound to a "this doesn't apply here" flag).</summary>
public sealed class InvertConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>The icon for a tone: done, warning, error, or information.</summary>
public sealed class ToneGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = Tone.Of(value as string) switch
        {
            Tone.Ok => "IconDone",
            Tone.Warn => "IconWarning",
            Tone.Danger => "IconError",
            _ => "IconInfo",
        };
        return Application.Current.TryFindResource(key) as string ?? "";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Half an element's width, for a two-column wrap panel whose rows are only as tall as their own items (a uniform grid
/// makes every row as tall as the tallest item in the list).
/// </summary>
public sealed class HalfWidthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double width && width > 2 ? Math.Floor(width / 2) : double.NaN;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// The width of one card in a grid of cards: one column in a narrow container, two up to 1180 px, three beyond, so a card keeps its
/// proportions at every window size instead of stretching with it.
/// </summary>
public sealed class CardColumnsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double width || width < 40)
        {
            return double.NaN;
        }

        var columns = width < 640 ? 1 : width < 1180 ? 2 : 3;
        return Math.Floor(width / columns);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
