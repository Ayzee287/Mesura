using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Mesura.App;

/// <summary>
/// A surface that lights up where the pointer is (the interactive glow of the 2026-10 design, docs/redesign-2026-11.md §2).
/// It draws two things on top of its own background and border: a soft radial wash and a brighter radial hairline, both
/// centred on the pointer and fading in and out with it. Nothing is drawn while the pointer is away, so a screenful of
/// cards costs one repaint per mouse move, on the card under it. It has no effect of its own on layout or content, and
/// with Windows' animation effects off the glow switches instantly instead of fading.
/// </summary>
public class GlowBorder : Border
{
    public static readonly DependencyProperty GlowOpacityProperty = DependencyProperty.Register(
        nameof(GlowOpacity), typeof(double), typeof(GlowBorder), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GlowRadiusProperty = DependencyProperty.Register(
        nameof(GlowRadius), typeof(double), typeof(GlowBorder), new FrameworkPropertyMetadata(260.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GlowColorProperty = DependencyProperty.Register(
        nameof(GlowColor), typeof(Color), typeof(GlowBorder), new FrameworkPropertyMetadata(Color.FromRgb(0x9B, 0x8C, 0xFF), FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>How strong the wash is at the pointer (0..1 of its alpha); the hairline is stronger.</summary>
    public static readonly DependencyProperty GlowStrengthProperty = DependencyProperty.Register(
        nameof(GlowStrength), typeof(double), typeof(GlowBorder), new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Keeps a faint glow after the pointer leaves (a selected card).</summary>
    public static readonly DependencyProperty IsHeldProperty = DependencyProperty.Register(
        nameof(IsHeld), typeof(bool), typeof(GlowBorder), new PropertyMetadata(false, (d, _) => ((GlowBorder)d).Retarget()));

    private Point _pointer;
    private bool _over;
    private RadialGradientBrush? _wash;
    private RadialGradientBrush? _edge;

    /// <summary>Current glow, 0 (none) to 1 (full); animated.</summary>
    public double GlowOpacity { get => (double)GetValue(GlowOpacityProperty); set => SetValue(GlowOpacityProperty, value); }

    public double GlowRadius { get => (double)GetValue(GlowRadiusProperty); set => SetValue(GlowRadiusProperty, value); }

    public Color GlowColor { get => (Color)GetValue(GlowColorProperty); set => SetValue(GlowColorProperty, value); }

    public double GlowStrength { get => (double)GetValue(GlowStrengthProperty); set => SetValue(GlowStrengthProperty, value); }

    public bool IsHeld { get => (bool)GetValue(IsHeldProperty); set => SetValue(IsHeldProperty, value); }

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
        if (GlowOpacity > 0.01)
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

    private void Retarget()
    {
        var target = _over ? 1.0 : IsHeld ? 0.35 : 0.0;
        if (!SystemParameters.ClientAreaAnimation)
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

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var opacity = GlowOpacity * GlowStrength;
        if (opacity < 0.01 || ActualWidth < 4 || ActualHeight < 4)
        {
            return;
        }

        var radius = CornerRadius.TopLeft;
        var rect = new Rect(0, 0, ActualWidth, ActualHeight);
        var c = GlowColor;
        var center = _over ? _pointer : new Point(ActualWidth / 2, 0);

        _wash ??= Ramp(c, 0x2E, GlowRadius);
        _edge ??= Ramp(c, 0xB4, GlowRadius * 0.8);
        _wash.Center = _wash.GradientOrigin = center;
        _edge.Center = _edge.GradientOrigin = center;

        dc.PushOpacity(Math.Min(1.0, opacity));
        dc.DrawRoundedRectangle(_wash, null, rect, radius, radius);
        if (_over)
        {
            // The lit rim only where the pointer is; a card that merely stays selected keeps just the soft wash.
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
