using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Mesura.App;

/// <summary>How much of the interactive light Mesura draws (Settings > Lighting).</summary>
public enum LightingMode
{
    /// <summary>The pointer lights every glass surface near it, and the ambient light behind them follows it with inertia.</summary>
    Full,

    /// <summary>Only the surface under the pointer lights up; the ambient light stays where it is.</summary>
    Reduced,

    /// <summary>No pointer light at all. A selected surface keeps its static wash.</summary>
    Off,
}

/// <summary>
/// One shared light for the whole window (0.4 "Graphite", docs/redesign-2026-11.md §4). The pointer is the light source: every
/// <see cref="GlowBorder"/> near it answers by its distance, so neighbouring surfaces brighten together instead of one card lighting
/// up alone, and the ambient field behind the glass (<see cref="AmbientField"/>) leans toward it with a little inertia.
/// Cost control: the pointer is read once per rendered frame, only while it moves or the eased position is still settling; the
/// frame loop is not subscribed otherwise, so an idle window draws nothing. Windows' own animation setting, a remote session
/// and a weak render tier all reduce or switch the effect off (see <see cref="Resolve"/>), and the user can choose too.
/// </summary>
public static class LightField
{
    private static readonly List<WeakReference<GlowBorder>> Surfaces = [];
    private static Window? _window;
    private static Point _pointer;
    private static Point _eased;
    private static bool _has;
    private static bool _moved;
    private static bool _subscribed;
    private static LightingMode _mode = LightingMode.Full;

    /// <summary>Raised once per frame while light is moving (pointer window position, eased position).</summary>
    public static event Action<Point, Point>? Frame;

    /// <summary>Raised when <see cref="Mode"/> changes.</summary>
    public static event Action? ModeChanged;

    public static LightingMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value)
            {
                return;
            }

            _mode = value;
            ModeChanged?.Invoke();
            Pump();
        }
    }

    /// <summary>The pointer's position in window coordinates, once it has entered the window.</summary>
    public static bool HasPointer => _has;

    /// <summary>
    /// What "Automatic" means on this PC: off when Windows' animation effects are off, reduced in a remote session or when the
    /// graphics tier is below 2 (no hardware-accelerated gradients), full otherwise.
    /// </summary>
    public static LightingMode Resolve(string? setting) => setting switch
    {
        _ when HighContrastTheme.IsOn => LightingMode.Off, // a high-contrast theme is a plain field, whatever was chosen
        "full" => LightingMode.Full,
        "reduced" => LightingMode.Reduced,
        "off" => LightingMode.Off,
        _ => !SystemParameters.ClientAreaAnimation ? LightingMode.Off
            : SystemParameters.IsRemoteSession || (System.Windows.Media.RenderCapability.Tier >> 16) < 2 ? LightingMode.Reduced
            : LightingMode.Full,
    };

    /// <summary>Starts following the pointer over <paramref name="window"/>.</summary>
    public static void Attach(Window window)
    {
        _window = window;
        window.PreviewMouseMove += (_, e) => Moved(e.GetPosition(window));
        window.MouseLeave += (_, _) =>
        {
            _has = false;
            _moved = true;
            Pump();
        };
    }

    internal static void Register(GlowBorder surface)
    {
        Surfaces.RemoveAll(w => !w.TryGetTarget(out _));
        Surfaces.Add(new WeakReference<GlowBorder>(surface));
    }

    internal static void Unregister(GlowBorder surface) => Surfaces.RemoveAll(w => !w.TryGetTarget(out var s) || s == surface);

    private static void Moved(Point p)
    {
        if (_mode == LightingMode.Off)
        {
            return;
        }

        if (!_has)
        {
            _eased = p;
        }

        _pointer = p;
        _has = true;
        _moved = true;
        Pump();
    }

    private static void Pump()
    {
        if (!_subscribed)
        {
            _subscribed = true;
            CompositionTarget.Rendering += OnRendering;
        }
    }

    private static void OnRendering(object? sender, EventArgs e)
    {
        var settling = false;
        if (_has)
        {
            var dx = _pointer.X - _eased.X;
            var dy = _pointer.Y - _eased.Y;
            if (Math.Abs(dx) > 0.4 || Math.Abs(dy) > 0.4)
            {
                _eased = new Point(_eased.X + dx * 0.14, _eased.Y + dy * 0.14);
                settling = true;
            }
            else
            {
                _eased = _pointer;
            }
        }

        if (_moved || settling)
        {
            var near = _mode == LightingMode.Full && _has && _window is not null;
            for (var i = Surfaces.Count - 1; i >= 0; i--)
            {
                if (!Surfaces[i].TryGetTarget(out var s))
                {
                    Surfaces.RemoveAt(i);
                    continue;
                }

                s.OnLight(near ? _pointer : null, _window);
            }

            Frame?.Invoke(_pointer, _eased);
        }

        _moved = false;
        if (!settling)
        {
            CompositionTarget.Rendering -= OnRendering;
            _subscribed = false;
        }
    }
}
