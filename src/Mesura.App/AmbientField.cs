using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Mesura.App;

/// <summary>
/// The living background behind the glass (0.4.3, docs/redesign-0.4.md): a few soft shapes that move on their own, each on its own slow path and at its
/// own speed, the way bubbles drift in a lava lamp (the idea, not the look). They have mass, they keep their momentum, and when two of them meet they push
/// each other away and squash against the contact, so the field is one body of moving light and not four lamps. The pointer does not steer anything: it is
/// an obstacle. A shape near it is pushed away, flattened against the push, and swept a little along the pointer's own movement; when the pointer
/// leaves, the shape eases back into its own drift. With the pointer still, nothing stops.
///
/// Everything is a gradient on an ellipse that is moved by composition transforms (scale, rotation, translation), so a frame costs a handful of
/// property writes and no layout or repaint of the page. The loop runs only while a mode other than Off is on, the window is visible and not minimised,
/// at the display's rate in Full, at half of it in Reduced, and slowed right down while the window is not the active one.
/// Full: five shapes, the pointer pushes them. Reduced: the same field drifting more slowly and fainter, no reaction to the pointer. Off: not drawn.
/// </summary>
public sealed class AmbientField : Canvas
{
    private const double BaseSize = 800; // the ellipse's own size; a shape is this scaled to its radius

    private sealed class Blob
    {
        public required Ellipse Shape { get; init; }

        public required ScaleTransform Scale { get; init; }

        public required RotateTransform Rotate { get; init; }

        public required TranslateTransform Move { get; init; }

        // Where it lives (fractions of the window), how far its own path wanders, how fast, and its size as a fraction of the window's larger side.
        public double AnchorX, AnchorY, WanderX, WanderY, FreqX, FreqY, PhaseX, PhaseY, Size, Weight, Strength, Breath, BreathPhase;

        // State: position and velocity in pixels, and the elastic deformation (a push squashes it along the push's direction).
        public double X, Y, VX, VY, Squash, SquashAngle, Angle;
    }

    private readonly List<Blob> _blobs = [];
    private readonly Random _random = new(7);
    private bool _running;
    private TimeSpan _last = TimeSpan.MinValue;
    private double _time;
    private double _width, _height;
    private Point _pointerBefore;
    private bool _hadPointer;
    private Window? _window;

    public AmbientField()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
        // Colour, anchor (fraction of the window), wander, speed (radians per second), size, weight and strength; the numbers are taste, tuned by eye:
        // the large cool light is the slowest and heaviest, the small violet and blue ones are quick and light, so the field has a pace and not a rhythm.
        Add("OrbCool", 0.18, 0.12, 0.16, 0.10, 0.052, 0.041, 0.52, 3.2, 0.95);
        Add("OrbWarm", 0.84, 0.88, 0.14, 0.12, 0.046, 0.058, 0.44, 2.6, 0.85);
        Add("OrbKey", 0.62, 0.30, 0.20, 0.16, 0.083, 0.067, 0.34, 1.6, 0.90);
        Add("OrbTrail", 0.30, 0.72, 0.18, 0.14, 0.071, 0.093, 0.30, 1.2, 0.80);
        Add("OrbKey", 0.90, 0.20, 0.10, 0.18, 0.099, 0.077, 0.22, 0.8, 0.70);
        SizeChanged += (_, _) => Resized();
        Loaded += (_, _) =>
        {
            _window = Window.GetWindow(this);
            LightField.ModeChanged += OnModeChanged;
            if (_window is not null)
            {
                _window.StateChanged += (_, _) => Pump();
                _window.IsVisibleChanged += (_, _) => Pump();
            }

            Resized();
            ApplyMode();
        };
        Unloaded += (_, _) =>
        {
            LightField.ModeChanged -= OnModeChanged;
            Stop();
        };
    }

    private void Add(string brushKey, double ax, double ay, double wx, double wy, double fx, double fy, double size, double weight, double strength)
    {
        var scale = new ScaleTransform(1, 1, BaseSize / 2, BaseSize / 2);
        var rotate = new RotateTransform(0, BaseSize / 2, BaseSize / 2);
        var move = new TranslateTransform();
        var shape = new Ellipse
        {
            Width = BaseSize,
            Height = BaseSize,
            Fill = (Brush)Application.Current.Resources[brushKey],
            IsHitTestVisible = false,
            RenderTransform = new TransformGroup { Children = [scale, rotate, move] },
            Opacity = strength,
        };
        Children.Add(shape);
        _blobs.Add(new Blob
        {
            Shape = shape,
            Scale = scale,
            Rotate = rotate,
            Move = move,
            AnchorX = ax,
            AnchorY = ay,
            WanderX = wx,
            WanderY = wy,
            FreqX = fx,
            FreqY = fy,
            PhaseX = _random.NextDouble() * Math.Tau,
            PhaseY = _random.NextDouble() * Math.Tau,
            Size = size,
            Weight = weight,
            Strength = strength,
            Breath = 0.05 + _random.NextDouble() * 0.04,
            BreathPhase = _random.NextDouble() * Math.Tau,
        });
    }

    private bool Alive => LightField.Mode != LightingMode.Off && IsVisible && _window is { WindowState: not WindowState.Minimized, IsVisible: true } && ActualWidth > 8;

    private void OnModeChanged()
    {
        ApplyMode();
        Pump();
    }

    /// <summary>Off draws nothing at all (a plain dark window); Reduced draws the field fainter; Full at its own strength.</summary>
    private void ApplyMode()
    {
        Visibility = LightField.Mode == LightingMode.Off ? Visibility.Collapsed : Visibility.Visible;
        Opacity = LightField.Mode == LightingMode.Reduced ? 0.55 : 1.0;
    }

    private void Resized()
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 8 || h < 8)
        {
            return;
        }

        if (_width < 8)
        {
            // First size: every shape starts on its own path at time zero.
            foreach (var b in _blobs)
            {
                var (tx, ty) = Target(b, 0, w, h);
                b.X = tx;
                b.Y = ty;
            }
        }
        else
        {
            foreach (var b in _blobs)
            {
                b.X *= w / _width;
                b.Y *= h / _height;
            }
        }

        _width = w;
        _height = h;
        Draw();
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

    private double _sinceDraw;
    private double _sinceTouch = 10;
    private bool _busy; // something is being pushed or is still moving fast: draw at the display's rate

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

        var dt = Math.Min((now - _last).TotalSeconds, 0.05);
        _last = now;
        _sinceDraw += dt;

        // The cost of a frame is the page under it being composed again, so frames are spent where the eye can use them: the drift is slow (a few pixels a
        // second) and reads the same at 12 frames a second (6 in Reduced); the display's rate is for the moments the pointer is moving things. While another window is the
        // active one the field only keeps company, at 3.
        var interval = !_window!.IsActive ? 1.0 / 3 : LightField.Mode == LightingMode.Reduced ? 1.0 / 6 : _busy ? 1.0 / 62 : 1.0 / 12;
        if (_sinceDraw < interval)
        {
            return;
        }

        var step = _sinceDraw;
        _sinceDraw = 0;
        Step(Math.Min(step, 0.066));
        Draw();
    }

    private (double X, double Y) Target(Blob b, double t, double w, double h) =>
        ((b.AnchorX + b.WanderX * Math.Sin(b.FreqX * t + b.PhaseX) + 0.35 * b.WanderX * Math.Sin(b.FreqY * 2.3 * t + b.PhaseY)) * w,
         (b.AnchorY + b.WanderY * Math.Sin(b.FreqY * t + b.PhaseY) + 0.35 * b.WanderY * Math.Sin(b.FreqX * 1.9 * t + b.PhaseX)) * h);

    private void Step(double dt)
    {
        var full = LightField.Mode == LightingMode.Full;
        var pace = LightField.Mode == LightingMode.Reduced ? 0.55 : 1.0;
        _time += dt * pace;
        var w = _width;
        var h = _height;
        var reference = Math.Max(w, h);

        var pointer = LightField.Pointer;
        var hasPointer = full && LightField.HasPointer && _window!.IsActive;
        var pointerVx = 0.0;
        var pointerVy = 0.0;
        if (hasPointer && _hadPointer)
        {
            pointerVx = (pointer.X - _pointerBefore.X) / Math.Max(dt, 1e-3);
            pointerVy = (pointer.Y - _pointerBefore.Y) / Math.Max(dt, 1e-3);
            var speed = Math.Sqrt(pointerVx * pointerVx + pointerVy * pointerVy);
            if (speed > 2400)
            {
                pointerVx *= 2400 / speed;
                pointerVy *= 2400 / speed;
            }
        }

        _pointerBefore = pointer;
        _hadPointer = hasPointer;

        // Forces first (every shape sees the others as they were), then the integration.
        var ax = new double[_blobs.Count];
        var ay = new double[_blobs.Count];
        var squashPush = new double[_blobs.Count];
        var touched = false;
        var squashAngle = new double[_blobs.Count];
        for (var i = 0; i < _blobs.Count; i++)
        {
            var b = _blobs[i];
            var ri = b.Size * reference / 2;

            // Its own path pulls it softly: the shape follows the path, it is not nailed to it.
            var (tx, ty) = Target(b, _time, w, h);
            ax[i] += (tx - b.X) * 0.55 * pace;
            ay[i] += (ty - b.Y) * 0.55 * pace;

            // Two shapes meeting push each other apart, the lighter one more, and both flatten against the contact.
            for (var j = 0; j < _blobs.Count; j++)
            {
                if (j == i)
                {
                    continue;
                }

                var o = _blobs[j];
                var dx = b.X - o.X;
                var dy = b.Y - o.Y;
                var dist = Math.Sqrt(dx * dx + dy * dy) + 1e-6;
                var reach = (ri + o.Size * reference / 2) * 0.60;
                if (dist >= reach)
                {
                    continue;
                }

                var overlap = 1 - dist / reach;
                var push = overlap * overlap * 2600 * (o.Weight / (b.Weight + o.Weight)) * 2 / Math.Max(b.Weight, 0.5);
                ax[i] += dx / dist * push;
                ay[i] += dy / dist * push;
                if (overlap > squashPush[i])
                {
                    squashPush[i] = overlap * 0.55;
                    squashAngle[i] = Math.Atan2(dy, dx);
                }
            }

            // The window's edge is soft: a shape that wanders past it is eased back.
            ax[i] += Edge(b.X, -0.12 * w, 1.12 * w) * 3.5;
            ay[i] += Edge(b.Y, -0.12 * h, 1.12 * h) * 3.5;

            if (hasPointer)
            {
                var dx = b.X - pointer.X;
                var dy = b.Y - pointer.Y;
                var dist = Math.Sqrt(dx * dx + dy * dy) + 1e-6;
                var influence = ri * 0.8 + 150;
                if (dist < influence)
                {
                    touched = true;
                    var t = 1 - dist / influence;
                    var push = t * t * 5200 / Math.Max(b.Weight, 0.6);
                    ax[i] += dx / dist * push + pointerVx * t * 0.9;
                    ay[i] += dy / dist * push + pointerVy * t * 0.9;
                    if (t * 0.62 > squashPush[i])
                    {
                        squashPush[i] = t * 0.62;
                        squashAngle[i] = Math.Atan2(dy, dx);
                    }
                }
            }
        }

        // Shapes resting against each other flatten a little all the time and drift slowly; that is not a reason to draw at the display's rate. The pointer is,
        // and so is the half second after it lets go, while the shapes spring back.
        _sinceTouch = touched ? 0 : _sinceTouch + dt;
        var busy = _sinceTouch < 0.6;
        for (var i = 0; i < _blobs.Count; i++)
        {
            var b = _blobs[i];
            busy |= b.VX * b.VX + b.VY * b.VY > 90 * 90;

            // Velocity with drag proportional to size (a big, heavy shape keeps its momentum longer), then position.
            var drag = 1.1 + 0.9 / Math.Max(b.Weight, 0.5);
            b.VX += ax[i] * dt;
            b.VY += ay[i] * dt;
            var damp = Math.Exp(-drag * dt);
            b.VX *= damp;
            b.VY *= damp;
            b.X += b.VX * dt;
            b.Y += b.VY * dt;

            // The elastic deformation: a spring that is pushed toward the contact's squash and lets go slowly (about a third of a second).
            var target = squashPush[i];
            if (target > 0.01)
            {
                b.SquashAngle = BlendAngle(b.SquashAngle, squashAngle[i], Math.Min(1, dt * 14));
            }

            b.Squash += (target - b.Squash) * Math.Min(1, dt * (target > b.Squash ? 16 : 3.2));
        }

        _busy = busy;
    }

    /// <summary>How far outside [low, high] a coordinate is, with the sign that points back in.</summary>
    private static double Edge(double v, double low, double high) => v < low ? low - v : v > high ? high - v : 0;

    private static double BlendAngle(double from, double to, double amount)
    {
        var d = Math.IEEERemainder(to - from, Math.Tau);
        return from + d * amount;
    }

    private void Draw()
    {
        var reference = Math.Max(_width, _height);
        foreach (var b in _blobs)
        {
            var speed = Math.Sqrt(b.VX * b.VX + b.VY * b.VY);
            var stretch = Math.Min(speed / 900, 0.16);
            var velocityAngle = Math.Atan2(b.VY, b.VX);

            // One axis for the whole shape: the push's direction while it is squashed, the direction of travel otherwise.
            var weightOfPush = b.Squash / (b.Squash + stretch + 0.02);
            b.Angle = BlendAngle(velocityAngle, b.SquashAngle, weightOfPush);
            var along = 1 + stretch - b.Squash;
            var across = 1 - 0.45 * stretch + 0.55 * b.Squash;

            var radius = b.Size * reference / 2;
            var s = 2 * radius / BaseSize;
            b.Scale.ScaleX = s * along;
            b.Scale.ScaleY = s * across;
            b.Rotate.Angle = b.Angle * 180 / Math.PI;
            b.Move.X = b.X - BaseSize / 2;
            b.Move.Y = b.Y - BaseSize / 2;
            b.Shape.Opacity = b.Strength * (1 - b.Breath + b.Breath * Math.Sin(_time * 0.21 + b.BreathPhase));
        }
    }
}
