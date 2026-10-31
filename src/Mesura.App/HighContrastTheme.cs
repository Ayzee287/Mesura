using System.Windows;
using System.Windows.Media;

namespace Mesura.App;

/// <summary>
/// Windows' high-contrast themes (0.4.1). Mesura draws its own surfaces, which Windows can't recolour, so when a high-contrast theme is on the design tokens
/// are pointed at the system colours instead: window and window text, the highlight for the accent, borders as full-strength lines, status told by the
/// words and marks the pages already carry rather than by hue. The interactive light is switched off. Applied once at start (and after a theme change,
/// which Windows reports as a settings change); it only recolours the tokens, so every page follows without its own code.
/// </summary>
public static class HighContrastTheme
{
    /// <summary>Whether a high-contrast theme is on (or a Debug build was asked to show one with <c>MESURA_PREVIEW_HC=1</c>).</summary>
    public static bool IsOn
    {
        get
        {
#if DEBUG
            if (Environment.GetEnvironmentVariable("MESURA_PREVIEW_HC") == "1")
            {
                return true;
            }
#endif
            return SystemParameters.HighContrast;
        }
    }

    public static void ApplyIfOn(ResourceDictionary resources)
    {
        if (!IsOn)
        {
            return;
        }

        var window = SystemColors.WindowColor;
        var text = SystemColors.WindowTextColor;
        var gray = SystemColors.GrayTextColor;
        var highlight = SystemColors.HighlightColor;
        var onHighlight = SystemColors.HighlightTextColor;
#if DEBUG
        if (!SystemParameters.HighContrast)
        {
            // A preview on a PC with an ordinary theme: the palette of Windows' "High contrast black" theme.
            window = Colors.Black;
            text = Colors.White;
            gray = Color.FromRgb(0x3F, 0xF2, 0x3F);
            highlight = Color.FromRgb(0x1A, 0xEB, 0xFF);
            onHighlight = Colors.Black;
        }
#endif

        Set(resources, "Bg", window);
        Set(resources, "Surface", window);
        Set(resources, "Raised", window);
        Set(resources, "Hover", highlight);
        Set(resources, "CardHover", window);
        Set(resources, "Popover", window);
        Set(resources, "Stroke", text);
        Set(resources, "StrokeStrong", text);
        Set(resources, "Text", text);
        Set(resources, "TextSecondary", text);
        Set(resources, "TextTertiary", gray);
        Set(resources, "Accent", highlight);
        Set(resources, "AccentHover", highlight);
        Set(resources, "AccentPressed", highlight);
        Set(resources, "AccentSubtle", window);
        Set(resources, "AccentRim", highlight);
        Set(resources, "OnAccent", onHighlight);
        foreach (var status in new[] { "Success", "Warning", "Danger", "Info" })
        {
            Set(resources, status, text);
            Set(resources, status + "Subtle", window);
        }

        Set(resources, "NeutralSubtle", window);
        Set(resources, "SidebarSurface", window);
        // The glass is flat: the accent fills are the highlight, the panels' sheen and the dock the window colour, the lit rim a full-strength line.
        SetGradient(resources, highlight, "AccentFill", "AccentFillHover", "AccentFillPressed");
        SetGradient(resources, window, "GlassSheen", "DockSurface");
        SetGradient(resources, text, "GlassEdge");
        SetGradient(resources, Colors.Transparent, "OrbKey", "OrbTrail", "OrbCool", "OrbWarm");
        LightField.Mode = LightingMode.Off;
    }

    private static void SetGradient(ResourceDictionary resources, Color color, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (resources[key] is GradientBrush { IsFrozen: false } g)
            {
                foreach (var stop in g.GradientStops)
                {
                    stop.Color = color;
                }
            }
        }
    }

    private static void Set(ResourceDictionary resources, string key, Color color)
    {
        if (resources[key] is SolidColorBrush { IsFrozen: false } brush)
        {
            brush.Color = color;
        }
        else
        {
            resources[key] = new SolidColorBrush(color);
        }
    }
}
