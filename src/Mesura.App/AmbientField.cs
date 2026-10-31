using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Mesura.App;

/// <summary>
/// The atmosphere behind the glass (0.4.4, docs/redesign-0.4.md): a dark room with a soft light in it, not an animation. After the look of a darkroom
/// seen from a distance: a very large, very faint key light that wanders across the window over about forty seconds, a violet "safelight" in the upper
/// corner that breathes, a faint mint haze low in the opposite corner, a pale diagonal beam that crosses the window in about a minute, and a few motes
/// of dust that rise through the dark, each at its own depth, speed and pace. The edges fall off into a vignette. Nothing in it has a hard edge and
/// nothing is brighter than a few percent over the ground, so text keeps its contrast (measured, docs/redesign-0.4.md).
///
/// The pointer is not a player here. It shifts the lights a few percent of the window with a long inertia (parallax: the room seems to have depth), lifts
/// a light by a few percent when it is near it, and nudges the motes a few pixels away. It never drags, bends or follows anything, and with the pointer
/// still the picture keeps living by itself.
///
/// Modes. Full: all of the above, drawn 12 times a second at rest (the movement is a few pixels a second) and 24 while the lights settle after the
/// pointer. Reduced is a different, smaller picture and not a slowed Full: the key light, the safelight and the haze, moving a little and breathing slowly,
/// no beam, no motes, no reaction to the pointer, 5 frames a second. Off: the same lights standing still, one frame, no loop.
/// A window that is not the active one is drawn twice a second. Every layer is a gradient on a shape moved by composition transforms, so a frame is a
/// handful of property writes, not a layout.
/// </summary>
public sealed class AmbientField : Canvas
{
    private const double Tau = Math.PI * 2;

    private sealed class Light
    {
        public required Shape Shape { get; init; }

        public required TranslateTransform Move { get; init; }

        public RotateTransform? Tilt { get; init; }

        // Where it sits (fractions of the window), how far its own path wanders (fractions), the path's period in seconds and phase, and its size.
        public double AnchorX, AnchorY, WanderX, WanderY, Period, Phase, Width, Height;

        // Its strength, how deep and how slowly it breathes, how far the pointer shifts it (fraction of the window) and how much a near pointer lifts it.
        public double Strength, BreathDepth, BreathPeriod, Parallax, Lift;

        // Only drawn in Full (the beam), or moving at all in Reduced.
        public bool FullOnly;
    }

    private sealed class Mote
    {
        public required Ellipse Shape { get; init; }

        public required TranslateTransform Move { get; init; }

        public double Depth, Size, Alpha, Duration, Offset, Rise, Sway, SwayPhase;

        // The life it is in now and where that life starts (fractions of the window); the pointer's small push on it, in pixels.
        public int Cycle = -1;
        public double StartX, StartY, PushX, PushY;
    }

    private static readonly Brush Grain = MakeGrain();

    private static Brush MakeGrain()
    {
        const int size = 128;
        var pixels = new byte[size * size * 4];
        var random = new Random(3);
        for (var i = 0; i < size * size; i++)
        {
            // White at 1 to 3 parts in 255 over the ground (premultiplied): it lifts a pixel by up to about two levels, one in three pixels stays clear.
            var a = (byte)random.Next(0, 4);
            pixels[i * 4] = a;
            pixels[i * 4 + 1] = a;
            pixels[i * 4 + 2] = a;
            pixels[i * 4 + 3] = a;
        }

        var tile = System.Windows.Media.Imaging.BitmapSource.Create(size, size, 96, 96, PixelFormats.Pbgra32, null, pixels, size * 4);
        tile.Freeze();
        var brush = new ImageBrush(tile)
        {
            TileMode = TileMode.Tile,
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, size, size),
            Stretch = Stretch.None,
        };
        brush.Freeze();
        return brush;
    }

    private readonly List<Light> _lights = [];
    private readonly List<Mote> _motes = [];
    private readonly Random _random = new(11);
    private Rectangle? _vignette;
    private Rectangle? _grain;
    private Light? _beam;
    private bool _running;
    private TimeSpan _last = TimeSpan.MinValue;
    private double _time = 9;
    private double _sinceDraw;
    private double _width, _height;
    private double _pointerX = 0.5, _pointerY = 0.5; // eased, as fractions of the window
    private bool _settling;
    private Window? _window;

    public AmbientField()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;

        // Taste, tuned by eye against the reference's calm: slow, large, faint. (anchor, wander, period, size, strength, breath, parallax, lift)
        AddLight("AmbientKey", 0.5, 0.42, 0.10, 0.08, 34, 1.16, 1.16, 1.00, 0.16, 14, 0.030, 0.12, fullOnly: false);
        AddLight("AmbientSafelight", 0.88, 0.02, 0.04, 0.045, 29, 0.92, 0.84, 1.0, 0.5, 17, 0.012, 0.14, fullOnly: false);
        AddLight("AmbientHaze", 0.08, 0.98, 0.06, 0.045, 41, 0.78, 0.66, 1.0, 0.4, 21, 0.018, 0.10, fullOnly: false);
        _beam = AddLight("AmbientBeam", 0.5, 0.5, 0.10, 0.0, 50, 1.2, 1.6, 1.15, 0.0, 1, 0.010, 0.0, fullOnly: true, tilt: -16);

        // Three depths: near motes are larger, brighter, quicker and react most to the pointer; far ones are specks that hardly move.
        for (var i = 0; i < 26; i++)
        {
            AddMote(i % 3 == 0 ? 1.0 : i % 3 == 1 ? 0.62 : 0.34);
        }

        _vignette = new Rectangle { Fill = (Brush)Application.Current.Resources["AmbientVignette"], IsHitTestVisible = false };
        Children.Add(_vignette);

        // A very fine, fixed grain over the lights: a dark gradient in 8 bits shows steps, and two levels of noise hide them (and give the dark its film-like tooth).
        if (!HighContrastTheme.IsOn)
        {
            _grain = new Rectangle { Fill = Grain, IsHitTestVisible = false };
            Children.Add(_grain);
        }

        SizeChanged += (_, _) => Resized();
        Loaded += (_, _) =>
        {
            _window = Window.GetWindow(this);
            LightField.ModeChanged += OnModeChanged;
            if (_window is not null)
            {
                _window.StateChanged += (_, _) => Pump();
                _window.IsVisibleChanged += (_, _) => Pump();
                _window.Activated += (_, _) => Pump();
            }

            Resized();
            ApplyMode();
            Pump();
        };
        Unloaded += (_, _) =>
        {
            LightField.ModeChanged -= OnModeChanged;
            Stop();
        };
    }

    private Light AddLight(string brushKey, double ax, double ay, double wx, double wy, double period, double width, double height, double strength, double breathDepth,
        double breathPeriod, double parallax, double lift, bool fullOnly, double tilt = 0)
    {
        var move = new TranslateTransform();
        var rotate = tilt == 0 ? null : new RotateTransform(tilt);
        var shape = new Rectangle
        {
            Fill = (Brush)Application.Current.Resources[brushKey],
            IsHitTestVisible = false,
            RenderTransform = rotate is null ? move : new TransformGroup { Children = [rotate, move] },
            RenderTransformOrigin = new Point(0.5, 0.5),
            Opacity = strength,
        };
        Children.Add(shape);
        var light = new Light
        {
            Shape = shape,
            Move = move,
            Tilt = rotate,
            AnchorX = ax,
            AnchorY = ay,
            WanderX = wx,
            WanderY = wy,
            Period = period,
            Phase = _random.NextDouble() * Tau,
            Width = width,
            Height = height,
            Strength = strength,
            BreathDepth = breathDepth,
            BreathPeriod = breathPeriod,
            Parallax = parallax,
            Lift = lift,
            FullOnly = fullOnly,
        };
        _lights.Add(light);
        return light;
    }

    private void AddMote(double depth)
    {
        var size = 2.0 + 3.6 * depth;
        var shape = new Ellipse
        {
            Width = size,
            Height = size,
            Fill = (Brush)Application.Current.Resources["AmbientMote"],
            IsHitTestVisible = false,
            Opacity = 0,
        };
        var move = new TranslateTransform();
        shape.RenderTransform = move;
        Children.Add(shape);
        _motes.Add(new Mote
        {
            Shape = shape,
            Move = move,
            Depth = depth,
            Size = size,
            Alpha = 0.22 + 0.36 * depth,
            Duration = 24 + _random.NextDouble() * 18,
            Offset = _random.NextDouble() * 60,
            Rise = (46 + _random.NextDouble() * 70) * depth,
            Sway = (6 + _random.NextDouble() * 12) * depth,
            SwayPhase = _random.NextDouble() * Tau,
        });
    }

    private bool Alive => LightField.Mode != LightingMode.Off && IsVisible && _window is { WindowState: not WindowState.Minimized, IsVisible: true } && ActualWidth > 8;

    private void OnModeChanged()
    {
        ApplyMode();
        Pump();
    }

    /// <summary>Full: everything. Reduced: the three lights, moving a little. Off: the three lights standing still.</summary>
    private void ApplyMode()
    {
        var full = LightField.Mode == LightingMode.Full;
        foreach (var light in _lights.Where(l => l.FullOnly))
        {
            light.Shape.Visibility = full ? Visibility.Visible : Visibility.Collapsed;
        }

        foreach (var mote in _motes)
        {
            mote.Shape.Visibility = full ? Visibility.Visible : Visibility.Collapsed;
        }

        Opacity = full ? 1.0 : 0.8;
        Render(0);
    }

    private void Resized()
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 8 || h < 8)
        {
            return;
        }

        _width = w;
        _height = h;
        foreach (var light in _lights)
        {
            var lw = light.Width * w;
            var lh = light.Height * h;
            light.Shape.Width = lw;
            light.Shape.Height = lh;
            SetLeft(light.Shape, light.AnchorX * w - lw / 2);
            SetTop(light.Shape, light.AnchorY * h - lh / 2);
        }

        foreach (var cover in new[] { _vignette, _grain })
        {
            if (cover is not null)
            {
                cover.Width = w;
                cover.Height = h;
            }
        }

        foreach (var mote in _motes)
        {
            mote.Cycle = -1; // a new life lays itself out for the new size
        }

        Render(0);
        Pump();
    }

    private void Pump()
    {
        if (Alive)
        {
            Start();
        }
        else
        {
            Stop();
        }
    }

    private void Start()
    {
        if (_running)
        {
            return;
        }

        _running = true;
        _last = TimeSpan.MinValue;
        CompositionTarget.Rendering += OnRendering;
    }

    private void Stop()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!Alive)
        {
            Stop();
            return;
        }

        var now = ((RenderingEventArgs)e).RenderingTime;
        if (_last == TimeSpan.MinValue || now == _last)
        {
            _last = now;
            return;
        }

        var dt = Math.Min((now - _last).TotalSeconds, 0.25);
        _last = now;
        _sinceDraw += dt;

        // The cost of a frame is the page under the light being composed again, so frames are spent where the eye can use them: the movement is a few pixels
        // a second and reads the same at 15 frames; the lights settling after the pointer get 24; Reduced is 5; a window that is not the active one, 2.
        var full = LightField.Mode == LightingMode.Full;
        var interval = !_window!.IsActive ? 1.0 / 2 : !full ? 1.0 / 5 : _settling ? 1.0 / 24 : 1.0 / 15;
        if (_sinceDraw < interval)
        {
            return;
        }

        var step = _sinceDraw;
        _sinceDraw = 0;
        _time += step;
        Render(step);
    }

    private double Smooth(double x) => x <= 0 ? 0 : x >= 1 ? 1 : x * x * (3 - 2 * x);

    /// <summary>One picture at the current time; <paramref name="dt"/> is the time since the last one (0: just lay it out again).</summary>
    private void Render(double dt)
    {
        var w = _width;
        var h = _height;
        if (w < 8 || h < 8)
        {
            return;
        }

        var mode = LightField.Mode;
        var full = mode == LightingMode.Full;
        var moving = mode != LightingMode.Off;
        var reach = Math.Max(w, h);

        // The pointer, as a fraction of the window, eased with a long inertia (about 1.4 s); out of the window it eases back to the middle, slower.
        var target = new Point(0.5, 0.5);
        var have = full && LightField.HasPointer && _window is { IsActive: true } && ActualWidth > 8;
        if (have)
        {
            var p = _window!.TranslatePoint(LightField.Pointer, this);
            target = new Point(Math.Clamp(p.X / w, 0, 1), Math.Clamp(p.Y / h, 0, 1));
        }

        if (dt > 0)
        {
            var k = 1 - Math.Exp(-dt / (have ? 1.4 : 2.6));
            _pointerX += (target.X - _pointerX) * k;
            _pointerY += (target.Y - _pointerY) * k;
        }

        _settling = full && (Math.Abs(target.X - _pointerX) > 0.004 || Math.Abs(target.Y - _pointerY) > 0.004);
        var shiftX = full ? _pointerX - 0.5 : 0;
        var shiftY = full ? _pointerY - 0.5 : 0;
        var amplitude = full ? 1.0 : mode == LightingMode.Reduced ? 0.45 : 0.0;
        var t = moving ? _time : 9;

        foreach (var light in _lights)
        {
            if (light.FullOnly && !full)
            {
                continue;
            }

            var wx = Math.Sin(Tau * t / light.Period + light.Phase);
            var wy = Math.Sin(Tau * t / (light.Period * 1.37) + light.Phase * 1.7);
            var x = (light.WanderX * wx * amplitude + shiftX * light.Parallax * 2) * w;
            var y = (light.WanderY * wy * amplitude + shiftY * light.Parallax * 2) * h;
            light.Move.X = x;
            light.Move.Y = y;
            if (light.Tilt is not null)
            {
                light.Tilt.Angle = -16 + 0.8 * Math.Sin(Tau * t / light.Period + light.Phase);
            }

            // It breathes (the safelight most) and a near pointer lifts it a little.
            var breath = moving && light.BreathDepth > 0 ? 0.5 + 0.5 * Math.Sin(Tau * t / light.BreathPeriod + light.Phase * 0.6) : 0.7;
            var strength = light.Strength * (1 - light.BreathDepth + light.BreathDepth * breath);
            if (have && light.Lift > 0)
            {
                var cx = light.AnchorX * w + x;
                var cy = light.AnchorY * h + y;
                var d = Math.Sqrt(Math.Pow(_pointerX * w - cx, 2) + Math.Pow(_pointerY * h - cy, 2)) / (0.6 * reach);
                strength *= 1 + light.Lift * Smooth(1 - d);
            }

            light.Shape.Opacity = strength;
        }

        if (!full)
        {
            return;
        }

        var pointer = have ? new Point(_pointerX * w, _pointerY * h) : (Point?)null;
        foreach (var mote in _motes)
        {
            var u = (t + mote.Offset) / mote.Duration;
            var cycle = (int)Math.Floor(u);
            var phase = u - cycle;
            if (cycle != mote.Cycle)
            {
                mote.Cycle = cycle;
                mote.StartX = 0.02 + _random.NextDouble() * 0.96;
                mote.StartY = 0.18 + _random.NextDouble() * 0.9;
            }

            var px = mote.StartX * w + mote.Sway * Math.Sin(Tau * phase * 1.3 + mote.SwayPhase) + shiftX * 26 * mote.Depth;
            var py = mote.StartY * h - mote.Rise * phase * (mote.Duration / 30) + shiftY * 26 * mote.Depth;

            // The pointer nudges a near mote a few pixels away; the mote settles back when it leaves.
            var pushX = 0.0;
            var pushY = 0.0;
            if (pointer is { } at)
            {
                var dx = px - at.X;
                var dy = py - at.Y;
                var dist = Math.Sqrt(dx * dx + dy * dy) + 1e-6;
                const double radius = 150;
                if (dist < radius)
                {
                    var f = Smooth(1 - dist / radius) * 9 * mote.Depth;
                    pushX = dx / dist * f;
                    pushY = dy / dist * f;
                }
            }

            var ease = dt > 0 ? 1 - Math.Exp(-dt / 0.6) : 1;
            mote.PushX += (pushX - mote.PushX) * ease;
            mote.PushY += (pushY - mote.PushY) * ease;
            mote.Move.X = px + mote.PushX - mote.Size / 2;
            mote.Move.Y = py + mote.PushY - mote.Size / 2;
            mote.Shape.Opacity = mote.Alpha * Smooth(phase / 0.14) * Smooth((1 - phase) / 0.14);
        }
    }
}
