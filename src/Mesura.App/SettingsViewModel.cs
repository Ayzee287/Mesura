using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Text;
using Mesura.Core.Apps;
using Mesura.Core.Windows;
using Mesura.Platform.Windows;
using Mesura.Setup.Windows;

namespace Mesura.App;

/// <summary>
/// Settings: the language, how Mesura starts (administrator prompt, tray at sign-in, Start menu), its folders and
/// edited catalogs, and what it runs as. The switches show what is actually set, re-read after each change.
/// </summary>
public sealed class SettingsViewModel : Observable
{
    private readonly Services _services;

    public SettingsViewModel(Services services, UpdateViewModel update)
    {
        _services = services;
        Update = update;
        Loc.Instance.LanguageChanged += () =>
        {
            Raise(nameof(ElevationFolderNote));
            Raise(nameof(ElevateHintText));
        };
        foreach (var (code, key) in new[] { ("stable", "Update.ChannelStable"), ("preview", "Update.ChannelPreview") })
        {
            Channels.Add(new FilterChoice(code, () => Loc.Get(key), chosen =>
            {
                if (_services.Settings.UpdateChannel != chosen)
                {
                    _services.Settings.UpdateChannel = chosen;
                    _services.Settings.SkippedUpdate = null;
                    _services.SaveSettings();
                    Update.CheckNow.Execute(null);
                }
            }));
        }

        Channels.First(c => c.Value == _services.Settings.UpdateChannel).IsSelected = true;
        foreach (var (code, name) in Loc.Languages)
        {
            Languages.Add(new FilterChoice(code, () => name, chosen =>
            {
                if (Loc.Instance.Language != chosen)
                {
                    _services.Settings.Language = chosen;
                    _services.SaveSettings();
                    Loc.Instance.Use(chosen);
                }
            }));
        }

        Languages.First(l => l.Value == Loc.Instance.Language).IsSelected = true;
        BuildStartupModes();
        foreach (var (code, key) in new[] { ("auto", "Settings.LightAuto"), ("full", "Settings.LightFull"), ("reduced", "Settings.LightReduced"), ("off", "Settings.LightOff") })
        {
            LightingChoices.Add(new FilterChoice(code, () => Loc.Get(key), chosen =>
            {
                var setting = chosen == "auto" ? null : chosen;
                if (_services.Settings.Lighting != setting)
                {
                    _services.Settings.Lighting = setting;
                    _services.SaveSettings();
                }

                LightField.Mode = LightField.Resolve(setting);
            }));
        }

        LightingChoices.First(c => c.Value == (_services.Settings.Lighting ?? "auto")).IsSelected = true;
        OpenHistory = new RelayCommand(() => Note = ShellLauncher.OpenFolder(_services.Store.HistoryFolder) ?? "");
        OpenCatalogs = new RelayCommand(() => Note = ShellLauncher.OpenFolder(CatalogFiles.SharedFolder) ?? "");
        CopyCatalogs = new RelayCommand(DoCopyCatalogs, () => MissingCatalogs.Count > 0);
        Loc.Instance.LanguageChanged += () => Raise(string.Empty);
    }

    public ObservableCollection<FilterChoice> Languages { get; } = [];

    /// <summary>Automatic, full, reduced or off: how much interactive light the window draws (docs/redesign-2026-11.md §4).</summary>
    public ObservableCollection<FilterChoice> LightingChoices { get; } = [];

    /// <summary>The update channels (stable, preview) and the update state the Updates section shows.</summary>
    public ObservableCollection<FilterChoice> Channels { get; } = [];

    public UpdateViewModel Update { get; }

    public bool CheckForUpdates
    {
        get => _services.Settings.CheckForUpdates;
        set
        {
            _services.Settings.CheckForUpdates = value;
            _services.SaveSettings();
            Raise();
        }
    }

    public RelayCommand OpenHistory { get; }

    public RelayCommand OpenCatalogs { get; }

    /// <summary>Writes the built-in catalogs into the shared folder (never over an edited copy) so they can be edited.</summary>
    public RelayCommand CopyCatalogs { get; }

    private string _note = "";

    /// <summary>The result of the last action on this page (an error, or what was done).</summary>
    public string Note
    {
        get => _note;
        private set => Set(ref _note, value);
    }

    /// <summary>What the start-up switch does here: in a per-user install it does nothing (Mesura never elevates from a user folder), and says so.</summary>
    public string ElevateHintText => Loc.Get(Loc.AdminKey("Settings.ElevateHint"));

    /// <summary>Ask each time, always as you are, or always restart as administrator (the last only where the program folder is protected).</summary>
    public ObservableCollection<FilterChoice> StartupModes { get; } = [];

    private void BuildStartupModes()
    {
        var blocked = ShellLauncher.ElevationBlockedReason is not null;
        foreach (var (code, key) in new[] { ("ask", "Settings.ModeAsk"), ("normal", "Settings.ModeNormal"), ("admin", "Settings.ModeAdmin") })
        {
            if (blocked && code == "admin")
            {
                continue; // never offered from a folder any program can change
            }

            StartupModes.Add(new FilterChoice(code, () => Loc.Get(key), chosen =>
            {
                if (_services.Settings.StartupMode != chosen)
                {
                    _services.Settings.StartupMode = chosen;
                    _services.Settings.ElevateAtStart = chosen != "normal";
                    _services.SaveSettings();
                }
            }));
        }

        var current = _services.Settings.EffectiveStartupMode;
        (StartupModes.FirstOrDefault(c => c.Value == (blocked && current == "admin" ? "ask" : current)) ?? StartupModes[0]).IsSelected = true;
    }

    /// <summary>Why administrator mode is unavailable in this install and how to get it (who can change this program folder); empty when the folder is protected.</summary>
    public string ElevationFolderNote => ShellLauncher.ElevationBlockedReason is { } why ? Loc.Format("Settings.ElevateExposed", AppContext.BaseDirectory, why) : "";

    public bool RestorePointBeforeChanges
    {
        get => _services.Settings.RestorePointBeforeChanges;
        set
        {
            _services.Settings.RestorePointBeforeChanges = value;
            _services.SaveSettings();
            Raise();
        }
    }

    public bool CanChangeStartup => !Services.RunsFromRemovableDrive && _services.SameUser;

    /// <summary>
    /// The prompt at start is decided from the signed-in user's own preferences; as another account, this switch would
    /// save to that account's and never change the prompt, so it is off.
    /// </summary>
    public bool CanChangeElevation => _services.SameUser && ShellLauncher.ElevationBlockedReason is null;

    /// <summary>Whether the start-up mode can be chosen: a choice saved by another account would never be read at start.</summary>
    public bool CanChangeStartupMode => _services.SameUser;

    public string StartupNote => Services.RunsFromRemovableDrive
        ? Loc.Format("Settings.Removable", Path.GetPathRoot(Services.Executable) ?? "", DefaultInstallFolder)
        : !_services.SameUser ? Loc.Get("Settings.OtherAccount")
        : _services.Startup.PointsElsewhere(Services.Executable) || StartMenuEntry.PointsElsewhere(Services.Executable) ? Loc.Get("Settings.PointsElsewhere")
        : "";

    public static string DefaultInstallFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Mesura");

    /// <summary>Whether Mesura starts in the tray when the user signs in (pointing at this copy).</summary>
    public bool StartAtSignIn
    {
        get => _services.Startup.IsOn(Services.Executable);
        set
        {
            try
            {
                _services.Startup.Set(Services.Executable, value);
                Note = "";
            }
            catch (Exception e) when (e is InvalidOperationException or UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                _services.Log($"Settings: the sign-in entry wasn't changed: {e.Message}");
                Note = Loc.Get("Settings.NotChanged");
            }

            Raise();
            Raise(nameof(StartupNote));
        }
    }

    /// <summary>Whether Mesura is in the Start menu (pointing at this copy); ticking adds or corrects it.</summary>
    public bool InStartMenu
    {
        get => StartMenuEntry.IsOn(Services.Executable);
        set
        {
            var error = StartMenuEntry.Set(Services.Executable, value);
            if (error is not null)
            {
                _services.Log($"Settings: {error}");
            }

            Note = error is null ? "" : Loc.Get("Settings.NotChanged");
            Raise();
            Raise(nameof(StartupNote));
        }
    }

    public string Version
    {
        get
        {
            var info = typeof(SettingsViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
            // "<version>+<commit>", with ".dirty" when the build had uncommitted changes (src\Directory.Build.targets).
            var plus = info.IndexOf('+', StringComparison.Ordinal);
            var commit = plus < 0 ? "" : info[(plus + 1)..];
            var uncommitted = commit.EndsWith(".dirty", StringComparison.Ordinal) ? " · " + Loc.Get("Settings.Uncommitted") : "";
            var version = plus < 0 ? info : info[..plus] + Loc.Format("Settings.Build", commit[..Math.Min(commit.Length, 7)]) + uncommitted;
            return $"Mesura {version}";
        }
    }

    public string Runtime => $".NET {Environment.Version} · {(Environment.Is64BitProcess ? "x64" : "x86")}";

    /// <summary>Who Mesura changes settings for, and with what rights.</summary>
    public string RunningAs
    {
        get
        {
            var reader = _services.Reader;
            var user = reader.User.Resolved ? reader.User.Account : Loc.Get("Settings.UserUnknown");
            return Loc.Format(reader.IsElevated ? "Settings.RunningElevated" : "Settings.RunningNormal", user);
        }
    }

    public string HistoryFolder => _services.Store.HistoryFolder;

    public string CatalogFolder => CatalogFiles.SharedFolder;

    public string CatalogsInUse =>
        Loc.Format("Settings.CatalogsInUse", Source(_services.WindowsCatalog.Source), Source(_services.Apps.Source))
        + string.Concat(new[] { _services.WindowsCatalogNote, _services.AppsCatalogNote }.Where(n => n.Tone == Tone.Warn).Select(n => " " + n.Text));

    private static string Source(string source) =>
        source == "built-in" ? Loc.Get("Settings.BuiltInCatalog") : Loc.Format("Settings.EditedCatalog", Path.GetFileName(source));

    private List<(string Name, string Json)> MissingCatalogs =>
        [.. new[] { (WindowsCatalog.FileName, (Func<string>)WindowsCatalog.BuiltInJson), (AppCatalog.FileName, AppCatalog.BuiltInJson) }
            .Where(c => !File.Exists(Path.Combine(CatalogFolder, c.Item1)))
            .Select(c => (c.Item1, c.Item2()))];

    private void DoCopyCatalogs()
    {
        try
        {
            var folder = LocalStore.CreateExportFolder(Path.GetDirectoryName(CatalogFolder)!, Path.GetFileName(CatalogFolder));
            var written = new List<string>();
            foreach (var (name, json) in MissingCatalogs)
            {
                LocalStore.WriteUserExport(Path.Combine(folder, name), Encoding.UTF8.GetBytes(json));
                written.Add(name);
            }

            Note = Loc.Format("Settings.Copied", string.Join(", ", written), folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _services.Log($"Settings: the catalogs weren't copied: {e.Message}");
            Note = Loc.Get("Settings.NotCopied");
        }

        CopyCatalogs.RaiseCanExecuteChanged();
    }
}
