using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Mesura.App;

/// <summary>A page that has something to apply or start: its template is shown in the command dock while the page is open.</summary>
public interface IDockPage
{
    /// <summary>The template of the page's dock content, bound to the page's own view model; <c>null</c> when it has none.</summary>
    DataTemplate? DockTemplate { get; }
}

/// <summary>
/// The one window (0.4 "Graphite" redesign): the open page over the ambient light, and one command dock under it: the pages on the left,
/// what the open page can do on the right (its selection and its primary action), and who is running Mesura. Each page is kept alive
/// while another is shown.
/// </summary>
public partial class MainWindow : Window
{
    private readonly bool _hostedInTray;
    private readonly Services _services;
    private readonly WindowsStateModel _windows;
    private readonly ActivityViewModel _activity;
    private readonly AppsViewModel _apps;
    private readonly UpdateViewModel _update;

    /// <param name="services">The shared services (one per process, so the tray and the window agree).</param>
    /// <param name="hostedInTray">When true, closing the window hides it (the tray keeps Mesura running) instead of exiting.</param>
    public MainWindow(Services services, bool hostedInTray = false)
    {
        InitializeComponent();
        _hostedInTray = hostedInTray;
        _services = services;
        Icon = Branding.WindowIcon;
        Logo.Source = Branding.Mark;
        LightField.Mode = LightField.Resolve(services.Settings.Lighting);
        LightField.Attach(this);
        ApplyAccessState();
        Loc.Instance.LanguageChanged += ApplyAccessState;
        Loaded += (_, _) => OnWindowSizeChanged(this, null!);

        services.Log("Mesura ready. Everything previews before it changes anything; nothing is sent anywhere.");
        foreach (var stale in new[]
        {
            Mesura.Setup.Windows.CatalogFiles.StaleNote(Mesura.Core.Windows.WindowsCatalog.FileName, Mesura.Setup.Windows.CatalogFiles.NewSinceCopied(services.WindowsCatalog)),
            Mesura.Setup.Windows.CatalogFiles.StaleNote(Mesura.Core.Apps.AppCatalog.FileName, Mesura.Setup.Windows.CatalogFiles.NewSinceCopied(services.Apps)),
            Mesura.Setup.Windows.CatalogFiles.SetAsideNote(Mesura.Core.Windows.WindowsCatalog.FileName, services.WindowsCatalog),
            Mesura.Setup.Windows.CatalogFiles.SetAsideNote(Mesura.Core.Apps.AppCatalog.FileName, services.Apps),
        }.Where(s => s.Length > 0))
        {
            services.Log(stale);
        }

        _windows = new WindowsStateModel(services);
        _activity = new ActivityViewModel(services);
        AppsPage.DataContext = _apps = new AppsViewModel(services, OpenActivity, () => ShowPage(ConfigurationsPage));
        OptimizePage.DataContext = new OptimizeViewModel(services, _windows, _apps, () => ShowPage(AllSettingsPage), OpenActivity, () => ShowPage(AppsPage));
        AllSettingsPage.DataContext = new AllSettingsViewModel(services, _windows, () => ShowPage(OptimizePage), OpenActivity);
        DebloatPage.DataContext = new DebloatViewModel(services, _windows, OpenActivity);
        FirmwarePage.DataContext = new FirmwareViewModel(services);
        var configurations = new ConfigurationsViewModel(services, OpenActivity);
        configurations.SavedChanged += _apps.RefreshSaved;
        ConfigurationsPage.DataContext = configurations;
        ProfilesPage.DataContext = new ProfilesViewModel(services, OpenActivity);
        ActivityPage.DataContext = _activity;
        ActivationPage.DataContext = new ActivationViewModel(services);
        SystemInfoPage.DataContext = new SystemInfoViewModel(services);
        _update = new UpdateViewModel(services);
        UpdateNotice.DataContext = _update;
        SettingsPage.DataContext = new SettingsViewModel(services, _update);
        NavOptimize.IsChecked = true;
        Language = System.Windows.Markup.XmlLanguage.GetLanguage(Loc.Instance.Culture.IetfLanguageTag);
        Loc.Instance.LanguageChanged += () => Language = System.Windows.Markup.XmlLanguage.GetLanguage(Loc.Instance.Culture.IetfLanguageTag);
        _ = _windows.RefreshAsync();
        Loaded += (_, _) => _update.Begin();
    }

    /// <summary>
    /// Called when the tray shows the window again after it was hidden: the PC may have changed meanwhile (the CLI, a
    /// change in Windows Settings), so the Windows state and Activity are read again. Apps aren't: that takes winget a
    /// second per app, and the page has its own refresh.
    /// </summary>
    public void RefreshOnShow()
    {
        _ = _windows.RefreshAsync();
        _activity.Load();
    }

    private void OnNavigate(object sender, RoutedEventArgs e)
    {
        UserControl? page = sender switch
        {
            _ when sender == NavOptimize => OptimizePage,
            _ when sender == NavTweaks => AllSettingsPage,
            _ when sender == NavDebloat => DebloatPage,
            _ when sender == NavFirmware => FirmwarePage,
            _ when sender == NavApps => AppsPage,
            _ when sender == NavConfigurations => ConfigurationsPage,
            _ when sender == NavProfiles => ProfilesPage,
            _ when sender == NavActivation => ActivationPage,
            _ when sender == NavSystem => SystemInfoPage,
            _ when sender == NavActivity => ActivityPage,
            _ when sender == NavSettings => SettingsPage,
            _ => null,
        };
        if (page is not null)
        {
            ShowPage(page);
        }
    }

    /// <summary>Shows one page, checks its entry in the dock and hands the dock the page's actions.</summary>
    private void ShowPage(UserControl page) => ShowPage(page, record: true);

    // The pages visited, newest last, so Back returns to the one before without losing that page's state (every page is kept alive).
    private readonly List<UserControl> _history = [];
    private UserControl? _current;

    private void ShowPage(UserControl page, bool record)
    {
        if (record && _current is not null && _current != page)
        {
            _history.Add(_current);
            if (_history.Count > 24)
            {
                _history.RemoveAt(0);
            }
        }

        _current = page;
        BackButton.Visibility = _history.Count > 0 || page.DataContext is IPageBack ? Visibility.Visible : Visibility.Collapsed;
        var changed = page.Visibility != Visibility.Visible;
        foreach (var child in new UserControl[] { OptimizePage, AllSettingsPage, DebloatPage, FirmwarePage, AppsPage, ConfigurationsPage, ProfilesPage, ActivationPage, SystemInfoPage, ActivityPage, SettingsPage })
        {
            if (child != page && child.Visibility == Visibility.Visible)
            {
                (child.DataContext as IPageState)?.Leave(); // a result that went well has been seen: it doesn't wait on the page
            }
            else if (child == page && child.Visibility != Visibility.Visible)
            {
                (child.DataContext as IPageState)?.Enter();
            }

            child.Visibility = child == page ? Visibility.Visible : Visibility.Collapsed;
        }

        var nav = page == AllSettingsPage ? NavTweaks
            : page == DebloatPage ? NavDebloat
            : page == FirmwarePage ? NavFirmware
            : page == AppsPage ? NavApps
            : page == ConfigurationsPage ? NavConfigurations
            : page == ProfilesPage ? NavProfiles
            : page == ActivationPage ? NavActivation
            : page == SystemInfoPage ? NavSystem
            : page == ActivityPage ? NavActivity
            : page == SettingsPage ? NavSettings
            : NavOptimize;
        // The dock's entries are kept in step by hand: the ones in the "more" menu are not in the visual tree until it opens, so the
        // group's own mutual exclusion cannot reach them.
        foreach (var item in new[] { NavOptimize, NavTweaks, NavDebloat, NavFirmware, NavApps, NavActivation, NavSystem, NavActivity, NavConfigurations, NavProfiles, NavSettings })
        {
            var want = item == nav;
            if (item.IsChecked != want)
            {
                item.Checked -= OnNavigate;
                item.IsChecked = want;
                item.Checked += OnNavigate;
            }
        }

        NavMore.IsChecked = false;
        NavMore.Tag = page == ConfigurationsPage || page == ProfilesPage || page == SettingsPage ? "active" : null;

        var template = (page as IDockPage)?.DockTemplate;
        ActionZone.ContentTemplate = template;
        ActionZone.Content = template is null ? null : page.DataContext;
        ActionDivider.Visibility = template is null ? Visibility.Collapsed : Visibility.Visible;

        if (changed)
        {
            Reveal(page);
        }
    }

    /// <summary>A page settles in: it fades up and rises a few pixels, 220 ms. Not at all when Windows' animation effects are off.</summary>
    private static void Reveal(UserControl page)
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            return;
        }

        var move = page.RenderTransform as TranslateTransform;
        if (move is null)
        {
            page.RenderTransform = move = new TranslateTransform();
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
        move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
    }

    /// <summary>
    /// A narrow window keeps the open page's name out of the dock (every item keeps its tooltip and automation name) and shortens the
    /// access label to its glyph, so the pages and their actions keep the room they need at the minimum size.
    /// </summary>
    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var compact = ActualWidth < CompactBelow;
        if (Chrome.GetCompact(Dock) == compact)
        {
            return;
        }

        Chrome.SetCompact(Dock, compact);
        AccessText.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
    }

    private const double CompactBelow = 1180;

    private void OpenActivity(string? runId)
    {
        _activity.Show(runId);
        ShowPage(ActivityPage);
    }

    /// <summary>Asks Windows for a dark title bar so the frame matches the window (Windows 10 20H1+; ignored elsewhere).</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        var dark = 1;
        _ = DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref dark, sizeof(int));
        var caption = 0x000B0908; // COLORREF (0x00BBGGRR) of the background #08090B, for Windows 11's caption colour
        _ = DwmSetWindowAttribute(handle, DwmCaptionColor, ref caption, sizeof(int));
    }

    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmCaptionColor = 35;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// In tray mode, closing hides the window (the tray stays, and a run in progress carries on); Exit from the tray menu
    /// quits for real. Otherwise closing quits, except while a run is changing the PC: it finishes first, so it is recorded.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (_hostedInTray)
        {
            e.Cancel = true;
            Hide();
        }
        else if (_services.Changing)
        {
            e.Cancel = true;
            Dialogs.Busy();
        }

        base.OnClosing(e);
    }

    /// <summary>
    /// The dock's access button says what is true for this run: "Administrator" (the shield rests), or "No admin rights" with the way to
    /// full access: a restart where Mesura can elevate, the all-users installation in a per-user install where it never does.
    /// </summary>
    private void ApplyAccessState()
    {
        var elevated = _services.Reader.IsElevated;
        var text = elevated ? Loc.Get("Nav.Admin") : Loc.Get("Nav.Standard");
        AccessText.Text = text;
        AccessGlyph.Foreground = elevated ? (Brush)FindResource("Success") : (Brush)FindResource("Warning");
        AccessButton.ToolTip = elevated ? Loc.Get("Nav.AdminHint") : Loc.Get(Loc.AdminKey("Nav.NotElevated")) + "\n" + Loc.Get(Loc.AdminKey("Nav.RestartElevated"));
        System.Windows.Automation.AutomationProperties.SetName(AccessButton, elevated ? text : text + ". " + Loc.Get(Loc.AdminKey("Nav.RestartElevated")));
    }

    private void OnAccessClick(object sender, RoutedEventArgs e)
    {
        if (_services.Reader.IsElevated)
        {
            return;
        }

        OnRestartElevated(sender, e);
    }

    private void OnRestartElevated(object sender, RoutedEventArgs e)
    {
        if (ShellLauncher.ElevationBlockedReason is { } why)
        {
            // A per-user install never elevates: say why, and how to get full access, instead of a prompt that cannot come.
            ConfirmDialog.Tell(Loc.Get("Nav.Standard"), Loc.Format("Admin.PerUserHow", AppContext.BaseDirectory, why));
            return;
        }

        if (_services.Changing)
        {
            Dialogs.Busy();
            return;
        }

        if (ShellLauncher.RelaunchElevated(Environment.GetCommandLineArgs().Skip(1)) is { } error)
        {
            ConfirmDialog.Tell(Loc.Get("Nav.NotElevated"), error);
            return;
        }

        Application.Current.Shutdown(); // the elevated instance takes over
    }

    /// <summary>Back: close what is open on this page first, then return to the page before.</summary>
    private void OnBackClick(object sender, RoutedEventArgs e) => GoBack();

    private void GoBack()
    {
        if (_current?.DataContext is IPageBack open && open.CloseOpenDetail())
        {
            return;
        }

        while (_history.Count > 0)
        {
            var previous = _history[^1];
            _history.RemoveAt(_history.Count - 1);
            if (previous != _current)
            {
                ShowPage(previous, record: false);
                return;
            }
        }
    }

    /// <summary>The mouse's Back button goes back too.</summary>
    private void OnWindowMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.XButton1)
        {
            GoBack();
            e.Handled = true;
        }
    }

    /// <summary>The logo: back to the Optimization page from anywhere.</summary>
    private void OnHomeClick(object sender, RoutedEventArgs e) => NavOptimize.IsChecked = true;

    /// <summary>Ctrl+F opens All settings with its search box focused.</summary>
    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Left && Keyboard.Modifiers == ModifierKeys.Alt)
        {
            GoBack();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            ShowPage(AllSettingsPage);
            AllSettingsPage.FocusSearch();
            e.Handled = true;
        }
    }
}
