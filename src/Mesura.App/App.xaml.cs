using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace Mesura.App;

/// <summary>
/// Mesura's desktop application. Two entry modes: the normal window, and <c>--tray</c>, which runs a tray icon and opens
/// the window on demand (architecture §4.11). One instance per Windows session: launching Mesura again asks the running
/// instance to open its window instead of starting a second one, and <c>Mesura --exit</c> asks it to quit. The services
/// are created once and shared, so the tray's status and the window agree and there is one user context.
/// <para>
/// The window asks for administrator rights once, when it opens (redesign §7): the first instance restarts itself
/// elevated and leaves. Declining keeps Mesura running without elevation. The tray instance started at sign-in stays
/// unelevated and asks when its window first opens. <c>--no-elevate</c>, or the preference in Settings, skips the prompt.
/// </para>
/// </summary>
public partial class App : Application
{
    private const string InstanceName = @"Local\Mesura.Instance";
    private const string OpenSignal = @"Local\Mesura.Open";
    private const string OpenNoPromptSignal = @"Local\Mesura.OpenNoPrompt";
    private const string ExitSignal = @"Local\Mesura.Exit";

    /// <summary>Passed by an elevation restart: wait for the instance that started us to close, then take over.</summary>
    public const string RelaunchedArgument = "--relaunched";

    /// <summary>Starts without asking for administrator rights (scripts, or a user who prefers it).</summary>
    public const string NoElevateArgument = "--no-elevate";

    private static string ElevatedElsewhere => Loc.Get(IsElevated() ? "Common.RunningOtherAccount" : "Common.ElevatedElsewhere");

    private Mutex? _instance;
    private EventWaitHandle? _open;
    private EventWaitHandle? _openNoPrompt;
    private EventWaitHandle? _exit;
    private TrayIcon? _tray;
    private MainWindow? _window;
    private Services? _services;
    private bool _elevationAsked;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        StartupLog.Note("stage: OnStartup");
        DispatcherUnhandledException += OnUnhandledException;
        bool Has(string argument) => e.Args.Any(a => a.Equals(argument, StringComparison.OrdinalIgnoreCase));
        var exitRequested = Has("--exit");
        var relaunched = Has(RelaunchedArgument);

        switch (BecomeTheInstance(relaunched))
        {
            case Instance.Elevated:
                // The running instance is elevated and this launch isn't, so Windows won't let it signal it.
                if (!exitRequested)
                {
                    MessageBox.Show(ElevatedElsewhere, Branding.Name, MessageBoxButton.OK, MessageBoxImage.Information);
                }

                Shutdown(1);
                return;
            case Instance.Running:
                // Mesura already runs in this session: hand the request to it and leave. --no-elevate travels with it,
                // so a running tray doesn't ask for administrator rights for a launch that opted out.
                if (!Signal(exitRequested ? [ExitSignal] : Has(NoElevateArgument) ? [OpenNoPromptSignal, OpenSignal] : [OpenSignal]) && !exitRequested)
                {
                    MessageBox.Show(ElevatedElsewhere, Branding.Name, MessageBoxButton.OK, MessageBoxImage.Information);
                }

                Shutdown();
                return;
        }

        if (exitRequested)
        {
            Shutdown(); // nothing to close
            return;
        }

        StartupLog.Note("stage: this is the one instance");
        // One administrator prompt when the window opens; the elevated instance takes over and this one leaves.
        var preferences = AppSettings.Load(new Mesura.Setup.Windows.SetupStore());
        StartupLog.Note("stage: preferences read");
        Loc.Instance.Use(PreviewLanguage() ?? preferences.Language ?? Loc.DefaultLanguage());
        if (ShellLauncher.ElevationBlockedReason is { } exposed)
        {
            StartupLog.Note($"administrator mode is not offered from this folder: {exposed}");
        }

        if (!Has("--tray") && !relaunched && !Has(NoElevateArgument) && !IsElevated() && StartupChoice.WantsAdministrator(preferences, new Mesura.Setup.Windows.SetupStore()))
        {
            _elevationAsked = true;
            if (ShellLauncher.RelaunchElevated(e.Args) is null)
            {
                Shutdown();
                return;
            }

            // The prompt was declined (or couldn't be shown): carry on without elevation; the window says so.
        }

        // --no-elevate holds for this instance's whole life: the tray's window won't ask either.
        _elevationAsked |= Has(NoElevateArgument);
        if (!CreateSignals(relaunched))
        {
            MessageBox.Show(Loc.Safe("Common.RunningOtherAccount", "Mesura is already running in this session under another account. Switch to it from the taskbar or the notification area, or exit it there and try again."), Branding.Name, MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(1);
            return;
        }

        new Thread(ListenForSignals) { IsBackground = true, Name = "Mesura signals" }.Start();

        try
        {
            _services = Services.Create();
            StartupLog.Note("stage: services created");
        }
        catch (Exception ex)
        {
            StartupLog.Fatal("Services.Create", ex);
            MessageBox.Show(Loc.Safe("Common.StartFailed", "Mesura couldn't start: {0}", ex.Message), Branding.Name, MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        if (Has("--tray"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown; // the window closing must not quit the tray
            _tray = new TrayIcon(_services, ShowWindow, () => RequestExit(asked: true));
        }

        if (_tray is null || relaunched)
        {
            StartupLog.Note("stage: opening the window");
            ShowWindow(); // after an elevation restart the user expects the window back, tray or not
        }
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private enum Instance
    {
        /// <summary>This process is the one Mesura instance.</summary>
        First,

        /// <summary>Another instance runs; it can be signalled.</summary>
        Running,

        /// <summary>Another instance runs elevated and this process can't reach it.</summary>
        Elevated,
    }

    /// <summary>
    /// Takes the session-wide instance mutex. Its security grants the signed-in user full access, so an instance
    /// restarted as administrator can still be reached from a normal launch of the same user.
    /// </summary>
    private Instance BecomeTheInstance(bool relaunched)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            try
            {
                _instance = MutexAcl.Create(initiallyOwned: true, InstanceName, out var created, UserMutex());
                if (created)
                {
                    return Instance.First;
                }

                if (relaunched)
                {
                    try
                    {
                        // The instance that restarted us elevated is closing; take over once it has.
                        if (_instance.WaitOne(TimeSpan.FromSeconds(20)))
                        {
                            return Instance.First;
                        }
                    }
                    catch (AbandonedMutexException)
                    {
                        return Instance.First; // it closed without releasing; the mutex is ours now
                    }
                }

                _instance.Dispose();
                _instance = null;
                return Instance.Running;
            }
            catch (UnauthorizedAccessException)
            {
                _instance = null;
                if (relaunched && DateTime.UtcNow < deadline)
                {
                    // The instance that restarted us is closing, and it ran as another account (its mutex isn't ours to
                    // open): wait for it to go rather than report it as running.
                    Thread.Sleep(250);
                    continue;
                }

                return Instance.Elevated;
            }
        }
    }

    /// <summary>
    /// Creates the events other launches signal. An instance restarted as another administrator can get here while its
    /// predecessor's events still exist (they stay the predecessor's until its process has gone): it waits for them as
    /// it waits for the mutex, and gives up after the same time.
    /// </summary>
    private bool CreateSignals(bool relaunched)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            try
            {
                _open = EventWaitHandleAcl.Create(false, EventResetMode.AutoReset, OpenSignal, out _, UserEvent());
                _openNoPrompt = EventWaitHandleAcl.Create(false, EventResetMode.AutoReset, OpenNoPromptSignal, out _, UserEvent());
                _exit = EventWaitHandleAcl.Create(false, EventResetMode.AutoReset, ExitSignal, out _, UserEvent());
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                _open?.Dispose();
                _openNoPrompt?.Dispose();
                _exit?.Dispose();
                _open = _openNoPrompt = _exit = null;
                if (!relaunched || DateTime.UtcNow >= deadline)
                {
                    return false;
                }

                Thread.Sleep(250);
            }
        }
    }

    /// <summary>
    /// Signals the running instance with the first of <paramref name="names"/> it has (an older instance may not know a
    /// newer signal); false when it exists but this process may not reach it.
    /// </summary>
    private static bool Signal(IReadOnlyList<string> names)
    {
        try
        {
            foreach (var name in names)
            {
                if (EventWaitHandle.TryOpenExisting(name, out var signal))
                {
                    using (signal)
                    {
                        _ = AllowSetForegroundWindow(AsfwAny); // let the running instance bring its window to the front
                        signal.Set();
                    }

                    return true;
                }
            }

            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    // The instance objects give the user who runs Mesura full access, elevated or not. Without that, objects made by an
    // elevated instance get the administrators-only default, and a normal launch of the same user can't reach them.
    private static SecurityIdentifier CurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User!;
    }

    private static MutexSecurity UserMutex()
    {
        var security = new MutexSecurity();
        security.AddAccessRule(new MutexAccessRule(CurrentUser(), MutexRights.FullControl, AccessControlType.Allow));
        return security;
    }

    private static EventWaitHandleSecurity UserEvent()
    {
        var security = new EventWaitHandleSecurity();
        security.AddAccessRule(new EventWaitHandleAccessRule(CurrentUser(), EventWaitHandleRights.FullControl, AccessControlType.Allow));
        return security;
    }

    private const int AsfwAny = -1;

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    /// <summary>Waits for another launch's request and carries it out on the UI thread.</summary>
    private void ListenForSignals()
    {
        WaitHandle[] signals = [_open!, _openNoPrompt!, _exit!];
        while (true)
        {
            var which = WaitHandle.WaitAny(signals);
            if (which == 0)
            {
                Dispatcher.BeginInvoke(() => ShowWindow());
            }
            else if (which == 1)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    _elevationAsked = true; // the launch said --no-elevate
                    ShowWindow();
                });
            }
            else
            {
                Dispatcher.BeginInvoke(() => RequestExit(asked: false));
                return;
            }
        }
    }

    /// <summary>
    /// Quits, unless a run is changing the PC: then the tray's Exit says so (the user can wait or cancel), and a
    /// scripted <c>--exit</c> quits as soon as the run has finished and been recorded.
    /// </summary>
    private void RequestExit(bool asked)
    {
        if (_services is not { Changing: true } services)
        {
            Shutdown();
            return;
        }

        if (asked)
        {
            Dialogs.Busy();
            return;
        }

        void ExitWhenDone()
        {
            if (!services.Changing)
            {
                services.ChangingChanged -= ExitWhenDone;
                Shutdown();
            }
        }

        services.ChangingChanged += ExitWhenDone;
    }

    /// <summary>Opens the window, creating it the first time and reusing it after (it hides, not closes, in tray mode).</summary>
    private void ShowWindow()
    {
        if (_services is null)
        {
            return;
        }

        if (_window is null)
        {
            // The tray instance started at sign-in runs unelevated: its window asks once, like a normal start.
            if (_tray is not null && !_elevationAsked && !_services.Changing && !IsElevated())
            {
                _elevationAsked = true;
                if (StartupChoice.WantsAdministrator(_services.Settings, _services.Store) && ShellLauncher.RelaunchElevated(["--tray"]) is null)
                {
                    Shutdown(); // the elevated instance shows the window and keeps the tray
                    return;
                }
            }

            HighContrastTheme.ApplyIfOn(Resources);
            _window = new MainWindow(_services, hostedInTray: _tray is not null);
            _window.Closed += (_, _) => _window = null;
        }
        else if (!_window.IsVisible)
        {
            _window.RefreshOnShow(); // hidden since the tray last showed it: the PC may have changed
        }

        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
    }

    /// <summary>
    /// A last line of defence: an unexpected error is shown (and the app keeps running) instead of closing Mesura
    /// without a word. Expected failures are reported in the tab where they happen.
    /// </summary>
    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        _services?.Log($"Unexpected: {e.Exception}");
        StartupLog.Fatal("the window (unexpected, the app keeps running)", e.Exception);
        MessageBox.Show(Loc.Safe("Common.UnexpectedDialog", "Something unexpected went wrong; nothing further ran.\n\n{0}", $"{e.Exception.GetType().Name}: {e.Exception.Message}"),
            Branding.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _open?.Dispose();
        _openNoPrompt?.Dispose();
        _exit?.Dispose();
        if (_instance is not null)
        {
            _instance.ReleaseMutex();
            _instance.Dispose();
        }

        base.OnExit(e);
    }

    /// <summary>
    /// Debug builds only: <c>MESURA_PREVIEW_LANGUAGE=en</c> or <c>ru</c> shows the window in that language for screenshots
    /// without touching the saved preference. Release builds have no such switch.
    /// </summary>
    private static string? PreviewLanguage()
    {
#if DEBUG
        var language = Environment.GetEnvironmentVariable("MESURA_PREVIEW_LANGUAGE");
        return language is "en" or "ru" ? language : null;
#else
        return null;
#endif
    }
}
