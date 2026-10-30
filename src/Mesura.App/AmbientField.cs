using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Mesura.App;

/// <summary>
/// The light behind the glass (docs/redesign-2026-11.md §4): three soft pools on the neutral field, none of them a colour you notice
/// by itself. A broad cool light sits top-left and a warm counter-light bottom-right; a third key light leans toward the pointer with
/// inertia (the eased position of <see cref="LightField"/>), and the other two shift a few pixels the other way, which is the depth.
/// Everything is a gradient on a translated ellipse, so moving the light costs a composition transform, not a repaint of the
/// page. Full: the key light follows the pointer and a second, violet light trails it more slowly, at full strength. Reduced: the lights rest, at
/// about a third of the strength, and nothing moves. Off: the field is not drawn at all, so the window is plain dark with no residual glow.
/// </summary>
public sealed class AmbientField : Canvas
{
    private readonly Ellipse _key = Orb("OrbKey", 920, 920);
    private readonly Ellipse _cool = Orb("OrbCool", 1100, 820);
    private readonly Ellipse _warm = Orb("OrbWarm", 940, 780);
    private readonly Ellipse _trail = Orb("OrbTrail", 760, 760);
    private readonly TranslateTransform _trailT = new();
    private Point _trailAt;
    private readonly TranslateTransform _keyT = new();
    private readonly TranslateTransform _coolT = new();
    private readonly TranslateTransform _warmT = new();

    public AmbientField()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
        _key.RenderTransform = _keyT;
        _cool.RenderTransform = _coolT;
        _warm.RenderTransform = _warmT;
        _trail.RenderTransform = _trailT;
        Children.Add(_cool);
        Children.Add(_warm);
        Children.Add(_trail);
        Children.Add(_key);
        SizeChanged += (_, _) => Rest();
        Loaded += (_, _) =>
        {
            LightField.Frame += OnFrame;
            LightField.ModeChanged += OnModeChanged;
            Rest();
            ApplyMode();
        };
        Unloaded += (_, _) =>
        {
            LightField.Frame -= OnFrame;
            LightField.ModeChanged -= OnModeChanged;
        };
    }

    private static Ellipse Orb(string brushKey, double w, double h) => new()
    {
        Width = w,
        Height = h,
        Fill = (Brush)Application.Current.Resources[brushKey],
        IsHitTestVisible = false,
    };

    /// <summary>The resting place of each light, for the current size.</summary>
    private void Rest()
    {
        if (ActualWidth < 1)
        {
            return;
        }

        Place(_coolT, -_cool.Width * 0.28, -_cool.Height * 0.40);
        Place(_warmT, ActualWidth - _warm.Width * 0.62, ActualHeight - _warm.Height * 0.55);
        Place(_keyT, ActualWidth * 0.28 - _key.Width / 2, ActualHeight * 0.16 - _key.Height / 2);
        _trailAt = new Point(ActualWidth * 0.34, ActualHeight * 0.30);
        Place(_trailT, _trailAt.X - _trail.Width / 2, _trailAt.Y - _trail.Height / 2);
        _key.Opacity = 0.55;
        _trail.Opacity = 0;
    }

    private void OnModeChanged()
    {
        Rest();
        ApplyMode();
    }

    /// <summary>Off draws nothing; Reduced draws the resting lights thin; Full draws them at full strength and lets them move.</summary>
    private void ApplyMode()
    {
        Visibility = LightField.Mode == LightingMode.Off ? Visibility.Collapsed : Visibility.Visible;
        Opacity = LightField.Mode == LightingMode.Reduced ? 0.34 : 1.0;
    }

    private void OnFrame(Point pointer, Point eased)
    {
        if (LightField.Mode != LightingMode.Full || !LightField.HasPointer || ActualWidth < 1)
        {
            if (LightField.Mode == LightingMode.Full && !LightField.HasPointer)
            {
                Rest();
            }

            return;
        }

        // Window coordinates equal canvas coordinates: the canvas fills the window's client area.
        Place(_keyT, eased.X - _key.Width / 2, eased.Y - _key.Height / 2);
        _key.Opacity = 1;

        // The trailing light closes on the eased position a little each frame, so it lags behind the key light and swings wide on turns.
        _trailAt = new Point(_trailAt.X + (eased.X - _trailAt.X) * 0.045, _trailAt.Y + (eased.Y - _trailAt.Y) * 0.045);
        Place(_trailT, _trailAt.X - _trail.Width / 2, _trailAt.Y - _trail.Height / 2);
        _trail.Opacity = 1;

        // The other two lean away from the pointer by a few pixels: parallax, enough to read as depth and no more.
        var nx = (eased.X / ActualWidth) - 0.5;
        var ny = (eased.Y / ActualHeight) - 0.5;
        Place(_coolT, -_cool.Width * 0.28 - nx * 38, -_cool.Height * 0.40 - ny * 28);
        Place(_warmT, ActualWidth - _warm.Width * 0.62 - nx * 52, ActualHeight - _warm.Height * 0.55 - ny * 36);
    }

    private static void Place(TranslateTransform t, double x, double y)
    {
        t.X = x;
        t.Y = y;
    }
}
