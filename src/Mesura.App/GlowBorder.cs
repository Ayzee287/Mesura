using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Mesura.App;

/// <summary>
/// A glass surface that answers the shared light (<see cref="LightField"/>, docs/redesign-2026-11.md §4). It draws two things on top of
/// its own background and border: a soft radial wash and a brighter radial hairline on the rim, both centred on the light. The surface
/// under the pointer lights fully; its neighbours light by how near the pointer is (a smooth falloff over <see cref="Reach"/>), so a
/// row of cards brightens together and the rim nearest the pointer catches it, which is what makes separate panels read as one
/// piece of glass. Nothing is drawn while the light is away, so an idle window costs nothing; with <see cref="LightingMode.Reduced"/>
/// only the surface under the pointer lights, and with Off none does (a selected surface keeps its static wash).
/// </summary>
public class GlowBorder : Border
{
    public static readonly DependencyProperty GlowOpacityProperty = DependencyProperty.Register(
        nameof(GlowOpacity), typeof(double), typeof(GlowBorder), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GlowRadiusProperty = DependencyProperty.Register(
        nameof(GlowRadius), typeof(double), typeof(GlowBorder), new FrameworkPropertyMetadata(260.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GlowColorProperty = DependencyProperty.Register(
        nameof(GlowColor), typeof(Color), typeof(GlowBorder), new FrameworkPropertyMetadata(Color.FromRgb(0xDC, 0xE6, 0xFF), FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((GlowBorder)d).ResetBrushes()));

    /// <summary>How strong the wash is at the pointer (0..1 of its alpha); the hairline is stronger.</summary>
    public static readonly DependencyProperty GlowStrengthProperty = DependencyProperty.Register(
        nameof(GlowStrength), typeof(double), typeof(GlowBorder), new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Keeps a faint glow after the pointer leaves (a selected card).</summary>
    public static readonly DependencyProperty IsHeldProperty = DependencyProperty.Register(
        nameof(IsHeld), typeof(bool), typeof(GlowBorder), new PropertyMetadata(false, (d, _) => ((GlowBorder)d).Retarget()));

    /// <summary>How far from the surface the light still reaches it, in device-independent pixels.</summary>
    public static readonly DependencyProperty ReachProperty = DependencyProperty.Register(
        nameof(Reach), typeof(double), typeof(GlowBorder), new PropertyMetadata(300.0));

    private const double NeighbourShare = 0.7;

    private Point _pointer;
    private Point _light;
    private bool _over;
    private double _near;
    private RadialGradientBrush? _wash;
    private RadialGradientBrush? _edge;

    public GlowBorder()
    {
        Loaded += (_, _) =>
        {
            LightField.Register(this);
            LightField.ModeChanged += OnModeChanged;
        };
        Unloaded += (_, _) =>
        {
            LightField.Unregister(this);
            LightField.ModeChanged -= OnModeChanged;
        };
    }

    /// <summary>Current glow of the surface under the pointer, 0 (none) to 1 (full); animated.</summary>
    public double GlowOpacity { get => (double)GetValue(GlowOpacityProperty); set => SetValue(GlowOpacityProperty, value); }

    public double GlowRadius { get => (double)GetValue(GlowRadiusProperty); set => SetValue(GlowRadiusProperty, value); }

    public Color GlowColor { get => (Color)GetValue(GlowColorProperty); set => SetValue(GlowColorProperty, value); }

    public double GlowStrength { get => (double)GetValue(GlowStrengthProperty); set => SetValue(GlowStrengthProperty, value); }

    public bool IsHeld { get => (bool)GetValue(IsHeldProperty); set => SetValue(IsHeldProperty, value); }

    public double Reach { get => (double)GetValue(ReachProperty); set => SetValue(ReachProperty, value); }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        _over = true;
        _pointer = e.GetPosition(this);
        Retarget();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _pointer = e.GetPosition(this);
        if (GlowOpacity > 0.01 && LightField.Mode != LightingMode.Off)
        {
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _over = false;
        Retarget();
    }

    /// <summary>
    /// One frame of the shared light: how near is the pointer (null when it is away, or the light is not in full mode)? The surface
    /// under it is handled by its own enter/leave animation; this lights the ones around it.
    /// </summary>
    internal void OnLight(Point? pointer, Window? window)
    {
        double near = 0;
        if (pointer is { } p && window is not null && IsVisible && ActualWidth > 4 && ActualHeight > 4)
        {
            var origin = TransformToAncestor(window).Transform(new Point(0, 0));
            var dx = Math.Max(Math.Max(origin.X - p.X, p.X - (origin.X + ActualWidth)), 0);
            var dy = Math.Max(Math.Max(origin.Y - p.Y, p.Y - (origin.Y + ActualHeight)), 0);
            var d = Math.Sqrt(dx * dx + dy * dy);
            if (d < Reach)
            {
                var k = 1 - d / Reach;
                near = k * k * NeighbourShare;
                _light = new Point(p.X - origin.X, p.Y - origin.Y);
            }
        }

        if (Math.Abs(near - _near) > 0.004 || (near > 0 && !_over))
        {
            _near = near;
            InvalidateVisual();
        }
    }

    private void OnModeChanged()
    {
        _near = 0;
        Retarget();
        InvalidateVisual();
    }

    private void Retarget()
    {
        var held = IsHeld ? 0.35 : 0.0;
        var target = LightField.Mode == LightingMode.Off ? held : _over ? 1.0 : held;
        if (!SystemParameters.ClientAreaAnimation || LightField.Mode == LightingMode.Off)
        {
            BeginAnimation(GlowOpacityProperty, null);
            GlowOpacity = target;
            return;
        }

        BeginAnimation(GlowOpacityProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(_over ? 140 : 320))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    private void ResetBrushes()
    {
        _wash = null;
        _edge = null;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var hover = GlowOpacity;
        var intensity = Math.Max(hover, _near);
        // Full is the full effect; Reduced draws the same light at about half strength (Off is handled by the caller: nothing is lit).
        var opacity = intensity * GlowStrength * (LightField.Mode == LightingMode.Reduced ? 0.5 : 1.0);
        if (opacity < 0.01 || ActualWidth < 4 || ActualHeight < 4)
        {
            return;
        }

        var radius = CornerRadius.TopLeft;
        var rect = new Rect(0, 0, ActualWidth, ActualHeight);
        var c = GlowColor;
        var lit = LightField.Mode != LightingMode.Off && (_over || _near > 0.004);
        var center = !lit ? new Point(ActualWidth / 2, 0) : _over ? _pointer : _light;

        _wash ??= Ramp(c, 0x2C, GlowRadius); // the wash sits behind text: at its centre the secondary text stays above 4.5:1 (docs/redesign-0.4.md)
        _edge ??= Ramp(c, 0xD2, GlowRadius * 0.8);
        _wash.Center = _wash.GradientOrigin = center;
        _edge.Center = _edge.GradientOrigin = center;

        dc.PushOpacity(Math.Min(1.0, opacity));
        dc.DrawRoundedRectangle(_wash, null, rect, radius, radius);
        if (lit)
        {
            // The lit rim only where the light is: the edge nearest the pointer catches it, also on a neighbouring surface. A card that
            // merely stays selected keeps just the soft wash.
            dc.DrawRoundedRectangle(null, new Pen(_edge, 1), new Rect(0.5, 0.5, Math.Max(0, ActualWidth - 1), Math.Max(0, ActualHeight - 1)), radius, radius);
        }

        dc.Pop();
    }

    private static RadialGradientBrush Ramp(Color c, byte alpha, double radius) => new(
        [
            new GradientStop(Color.FromArgb(alpha, c.R, c.G, c.B), 0),
            new GradientStop(Color.FromArgb((byte)(alpha / 3), c.R, c.G, c.B), 0.5),
            new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1),
        ])
    {
        MappingMode = BrushMappingMode.Absolute,
        RadiusX = radius,
        RadiusY = radius,
    };
}
