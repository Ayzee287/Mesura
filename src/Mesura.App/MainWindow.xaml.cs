using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

namespace Mesura.App;

/// <summary>
/// The one window (redesign §2, D031): a navigation rail organised around goals — Setup (Optimize in code), Apps,
/// Configurations, Profiles, Activation, Activity — and Settings, with each page kept alive while another is shown. All
/// settings opens from Setup.
/// </summary>
public partial class MainWindow : Window
{
    private readonly bool _hostedInTray;
    private readonly Services _services;
    private readonly WindowsStateModel _windows;
    private readonly ActivityViewModel _activity;
    private readonly AppsViewModel _apps;

    /// <param name="services">The shared services (one per process, so the tray and the window agree).</param>
    /// <param name="hostedInTray">When true, closing the window hides it (the tray keeps Mesura running) instead of exiting.</param>
    public MainWindow(Services services, bool hostedInTray = false)
    {
        InitializeComponent();
        _hostedInTray = hostedInTray;
        _services = services;
        Icon = Branding.WindowIcon;
        Logo.Source = Branding.Mark;
        ElevationNote.Visibility = services.Reader.IsElevated ? Visibility.Collapsed : Visibility.Visible;

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
        var configurations = new ConfigurationsViewModel(services, OpenActivity);
        configurations.SavedChanged += _apps.RefreshSaved;
        ConfigurationsPage.DataContext = configurations;
        ProfilesPage.DataContext = new ProfilesViewModel(services, OpenActivity);
        ActivityPage.DataContext = _activity;
        ActivationPage.DataContext = new ActivationViewModel(services);
        SettingsPage.DataContext = new SettingsViewModel(services);
        NavOptimize.IsChecked = true;
        Language = System.Windows.Markup.XmlLanguage.GetLanguage(Loc.Instance.Culture.IetfLanguageTag);
        Loc.Instance.LanguageChanged += () => Language = System.Windows.Markup.XmlLanguage.GetLanguage(Loc.Instance.Culture.IetfLanguageTag);
        _ = _windows.RefreshAsync();
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
            _ when sender == NavApps => AppsPage,
            _ when sender == NavConfigurations => ConfigurationsPage,
            _ when sender == NavProfiles => ProfilesPage,
            _ when sender == NavActivation => ActivationPage,
            _ when sender == NavActivity => ActivityPage,
            _ when sender == NavSettings => SettingsPage,
            _ => null,
        };
        if (page is not null)
        {
            ShowPage(page);
        }
    }

    /// <summary>Shows one page; All settings belongs to Optimize in the rail.</summary>
    private void ShowPage(UserControl page)
    {
        foreach (var child in new UserControl[] { OptimizePage, AllSettingsPage, AppsPage, ConfigurationsPage, ProfilesPage, ActivationPage, ActivityPage, SettingsPage })
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

        var nav = page == AllSettingsPage ? NavOptimize
            : page == AppsPage ? NavApps
            : page == ConfigurationsPage ? NavConfigurations
            : page == ProfilesPage ? NavProfiles
            : page == ActivationPage ? NavActivation
            : page == ActivityPage ? NavActivity
            : page == SettingsPage ? NavSettings
            : NavOptimize;
        if (nav.IsChecked != true)
        {
            nav.Checked -= OnNavigate;
            nav.IsChecked = true;
            nav.Checked += OnNavigate;
        }
    }

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
        var caption = 0x000F0C0B; // COLORREF (0x00BBGGRR) of the background #0B0C0F, for Windows 11's caption colour
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

    private void OnRestartElevated(object sender, RoutedEventArgs e)
    {
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

    /// <summary>Ctrl+F opens All settings with its search box focused.</summary>
    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            ShowPage(AllSettingsPage);
            AllSettingsPage.FocusSearch();
            e.Handled = true;
        }
    }
}
