using System.IO;
using System.Windows.Forms;
using Mesura.Core.Windows;

namespace Mesura.App;

/// <summary>
/// The tray presence (architecture §4.11): opens Mesura with a click or from the menu, and shows a short status — how
/// many changes are left for the outcome chosen on Optimize (Recommended unless another was applied), whose settings
/// Mesura targets, and whether it runs as administrator. It never pops up notifications: the status lives in the
/// tooltip and the menu, and is read again when the menu opens and after every run Mesura records. If the status can't
/// be read, it says so instead of showing a stale or made-up number.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const string DefaultOutcome = "recommended-gaming";

    private readonly Services _services;
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _open;
    private readonly ToolStripMenuItem _status;
    private readonly ToolStripMenuItem _scope;
    private readonly ToolStripMenuItem _startup;
    private readonly ToolStripMenuItem _exit;

    public TrayIcon(Services services, Action openWindow, Action exit)
    {
        _services = services;

        _status = new ToolStripMenuItem("…") { Enabled = false };
        _scope = new ToolStripMenuItem("") { Enabled = false };
        _startup = new ToolStripMenuItem("", null, (_, _) => ToggleStartup());
        _open = new ToolStripMenuItem("", null, (_, _) => openWindow());
        _open.Font = new System.Drawing.Font(_open.Font ?? System.Drawing.SystemFonts.MenuFont!, System.Drawing.FontStyle.Bold);
        _exit = new ToolStripMenuItem("", null, (_, _) => exit());

        var menu = new ContextMenuStrip();
        menu.Items.Add(_open);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_status);
        menu.Items.Add(_scope);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_startup);
        menu.Items.Add(_exit);
        menu.Opening += (_, _) => Refresh();

        _icon = new NotifyIcon
        {
            Icon = Branding.CreateTrayIcon(),
            Visible = true,
            Text = Branding.Name,
            ContextMenuStrip = menu,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                openWindow();
            }
        };

        // Both are raised on the UI thread: keep the tooltip current without polling.
        services.RunRecorded += _ => Refresh();
        services.ChangingChanged += Refresh;
        Loc.Instance.LanguageChanged += Refresh;
        Refresh();
    }

    private void ToggleStartup()
    {
        if (Services.RunsFromRemovableDrive)
        {
            MessageBox.Show(Loc.Format("Settings.Removable", Path.GetPathRoot(Services.Executable) ?? "", SettingsViewModel.DefaultInstallFolder),
                Branding.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            _services.Startup.Set(Services.Executable, !_services.Startup.IsOn(Services.Executable));
        }
        catch (Exception e) when (e is InvalidOperationException or UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            MessageBox.Show(e.Message, Branding.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        Refresh();
    }

    /// <summary>Re-reads the configuration status (read-only) for the tooltip and the menu.</summary>
    private void Refresh()
    {
        var reader = _services.Reader;
        _open.Text = Loc.Get("Tray.Open");
        _exit.Text = Loc.Get("Tray.Exit");
        _startup.Checked = _services.Startup.IsOn(Services.Executable);
        _startup.Text = Loc.Get("Settings.Tray");
        _startup.Enabled = reader.User.Resolved && _services.SameUser;
        _scope.Text = (reader.User.Resolved ? Loc.Format("Tray.SettingsFor", reader.User.Account) : Loc.Get("Tray.UserUnknown"))
            + " · " + (reader.IsElevated ? Loc.Get("Tray.Elevated") : Loc.Get("Tray.NotElevated"));

        if (_services.Changing)
        {
            Show(Loc.Get("Tray.Applying"), Loc.Get("Tray.Applying"));
            return;
        }

        try
        {
            if (_services.WindowsCatalog.Catalog is not { } catalog)
            {
                Show(Loc.Get("Tray.Unavailable"), Loc.Get("Tray.Unavailable"));
                return;
            }

            if ((catalog.PresetById(_services.Settings.Outcome ?? DefaultOutcome) ?? catalog.PresetById(DefaultOutcome)) is not { } preset)
            {
                Show(Loc.Get("Tray.Unavailable"), Loc.Get("Tray.Unavailable"));
                return;
            }

            var states = catalog.Resolve(preset).Select(op => WindowsEvaluator.Evaluate(op, reader).State).ToList();
            var pending = states.Count(s => s is OperationState.NotApplied or OperationState.Partial);
            var unreadable = states.Count(s => s is OperationState.Blocked or OperationState.Unknown);
            var summary = unreadable > 0 && pending == 0 ? Loc.Get("Tray.Unavailable")
                : pending == 0 ? Loc.Get("Optimize.OutcomeDone")
                : Loc.Plural("Optimize.OutcomePending", pending);
            Show($"{Loc.Name(preset)}: {summary}", summary);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
        {
            Show(Loc.Get("Tray.Unavailable"), Loc.Get("Tray.Unavailable"));
        }
    }

    /// <param name="status">The menu line.</param>
    /// <param name="tip">The short form for the tooltip, which Windows caps at 63 characters.</param>
    private void Show(string status, string tip)
    {
        _status.Text = status;
        var text = $"{Branding.Name} — {tip}";
        _icon.Text = text.Length <= 63 ? text : text[..62] + "…";
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Icon?.Dispose();
        _icon.Dispose();
    }
}
