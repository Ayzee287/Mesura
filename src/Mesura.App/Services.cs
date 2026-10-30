using System.Text;
using System.Threading.Tasks;
using Mesura.Core.Apps;
using Mesura.Core.Config;
using Mesura.Core.Setup;
using Mesura.Core.Windows;
using Mesura.Platform.Windows.Windows;
using Mesura.Setup.Windows;
using Mesura.Setup.Windows.Apps;
using Mesura.Setup.Windows.Nvidia;
using Mesura.Setup.Windows.Windows;
using Mesura.Setup.Windows.Workflow;

namespace Mesura.App;

/// <summary>
/// The live operations the app composes: the winget runner, the Windows state reader and writer, the setup store and
/// the editable catalogs, plus Mesura's own preferences and the session log. Made once at startup and shared by the
/// pages, so there is one reader (one user context) and one catalog-load result for the whole window.
/// </summary>
public sealed class Services
{
    private readonly StringBuilder _log = new();
    private Task<SystemSummary>? _summary;

    private Services(
        AppInstallService installer, LoadedCatalog<AppCatalog> apps,
        SystemStateReader reader, WindowsConfigurator windows, LoadedCatalog<WindowsCatalog> windowsCatalog,
        SetupStore store, StartupEntry startup, bool sameUser)
    {
        Installer = installer;
        Apps = apps;
        Reader = reader;
        Windows = windows;
        WindowsCatalog = windowsCatalog;
        Store = store;
        Startup = startup;
        SameUser = sameUser;
        Settings = AppSettings.Load(store);
        installer.InstallFolderFor = package => Settings.InstallFolders is { } folders && folders.TryGetValue(package.Id, out var folder) ? folder : null;
    }

    public AppInstallService Installer { get; }

    public LoadedCatalog<AppCatalog> Apps { get; }

    public SystemStateReader Reader { get; }

    public WindowsConfigurator Windows { get; }

    /// <summary>Windows restore points (made before a run that changes the whole PC, listed on Activity).</summary>
    public IRestorePoints RestorePoints { get; } = new Mesura.Setup.Windows.Windows.RestorePoints();

    /// <summary>What the last restore point attempt said (empty when it worked or none was asked for), for the result of that run.</summary>
    public string RestoreNote { get; set; } = "";

    private IStartupSource? _startupApps;

    /// <summary>The programs registered to start with Windows (the Debloat page's third tab).</summary>
    public IStartupSource StartupApps => _startupApps ??= new Mesura.Platform.Windows.Windows.StartupScanner(Reader.User);

    public LoadedCatalog<WindowsCatalog> WindowsCatalog { get; }

    public SetupStore Store { get; }

    /// <summary>NVCleanstall and NVIDIA Profile Inspector, for the NVIDIA steps of a setup.</summary>
    public INvidiaTools Nvidia { get; } = new NvidiaTools();

    /// <summary>Whether Mesura starts in the tray at sign-in (shared by the tray menu and the window).</summary>
    public StartupEntry Startup { get; }

    /// <summary>Mesura's own preferences.</summary>
    public AppSettings Settings { get; }

    /// <summary>
    /// Whether Mesura runs as the user signed in to this session (elevated or not). When it doesn't (a standard user
    /// elevated with another administrator's password), the app settings, Start menu entry and history here belong to
    /// that other account, so Configurations and the Start menu entry are blocked rather than changed for the wrong user.
    /// </summary>
    public bool SameUser { get; }


    /// <summary>Raised when the NVIDIA driver configuration was replaced (a profile run brought its own), so Setup shows it.</summary>
    public event Action? NvidiaConfigChanged;

    public void NvidiaConfigUpdated() => NvidiaConfigChanged?.Invoke();

    /// <summary>Saves the preferences; a failure goes to the session log.</summary>
    public void SaveSettings()
    {
        if (Settings.Save(Store) is { } error)
        {
            Log($"Preferences weren't saved: {error}");
        }
    }

    /// <summary>What Optimize shows about this PC, read once in the background (adapters take a moment).</summary>
    public Task<SystemSummary> Summary() => _summary ??= Task.Run(() => Preview(SystemSummaryReader.Read(Reader)));

#if DEBUG
    /// <summary>
    /// A design preview, in Debug builds only: with <c>MESURA_PREVIEW_NVIDIA=1</c> a PC without an NVIDIA GPU shows a
    /// stand-in one, named as a preview, so the NVIDIA card can be looked at on the development laptop. Its NVIDIA steps
    /// are never run. Release builds have no such switch.
    /// </summary>
    public static bool PreviewHardware { get; } = Environment.GetEnvironmentVariable("MESURA_PREVIEW_NVIDIA") == "1";
#else
    public static bool PreviewHardware => false;
#endif

    private static SystemSummary Preview(SystemSummary summary)
    {
        if (!PreviewHardware || summary.HasNvidia)
        {
            return summary;
        }

        const string name = "NVIDIA GeForce RTX 4070 (design preview)";
        return summary with
        {
            NvidiaGpus = [.. summary.NvidiaGpus, name],
            NvidiaAdapters = [new Mesura.Core.Nvidia.NvidiaGpu(name, 0x2786, "32.0.15.6094")],
        };
    }

    /// <summary>
    /// The configuration newly installed apps are set up from, by the source chosen on Apps: Mesura's defaults, the
    /// user's saved configuration, or none (also none when Mesura runs as another account than the signed-in user).
    /// </summary>
    public (ConfigBundleFile? Bundle, string? Error) AfterInstallBundle() => BundleFor(Settings.ConfigureAfterInstall);

    /// <summary>
    /// The configuration for a set-up source (<c>defaults</c>, <c>saved</c>, <c>none</c>): a setup that continues uses the
    /// source it was confirmed with, not whatever Apps is set to now.
    /// </summary>
    public (ConfigBundleFile? Bundle, string? Error) BundleFor(string source)
    {
        if (source == "none" || !SameUser)
        {
            return (null, null);
        }

        if (source != "saved")
        {
            return (ConfigDefaults.Bundle(SetupRun.Stamp(DateTimeOffset.UtcNow)), null);
        }

        if (Settings.SavedConfiguration is not { } path || !System.IO.File.Exists(path))
        {
            return (null, null);
        }

        try
        {
            return (ConfigBundleFile.Load(System.IO.File.ReadAllText(path)), null);
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException or FormatException)
        {
            return (null, e.Message);
        }
    }

    /// <summary>What a setup runs with: the shared installer, configurator, store and NVIDIA tools.</summary>
    public SetupContext SetupContext(ConfigBundleFile? bundle) => new(
        Installer, Apps.Catalog ?? AppCatalog.Default, WindowsCatalog.Catalog ?? Mesura.Core.Windows.WindowsCatalog.Default, Windows, Store, Nvidia,
        () => DateTimeOffset.UtcNow, bundle, Reader.Refresh, ReadBattery: () => Reader.HasBattery);

    /// <summary>This executable, for the sign-in entry.</summary>
    public static string Executable => Environment.ProcessPath ?? System.IO.Path.Combine(AppContext.BaseDirectory, "Mesura.exe");

    /// <summary>
    /// Whether Mesura runs from a removable drive (a USB stick on a fresh install): a Start menu or sign-in entry would
    /// then point to the drive, so both are refused until Mesura runs from a folder on the PC.
    /// </summary>
    public static bool RunsFromRemovableDrive
    {
        get
        {
            try
            {
                return System.IO.Path.GetPathRoot(Executable) is { Length: > 0 } root
                    && new System.IO.DriveInfo(root).DriveType == System.IO.DriveType.Removable;
            }
            catch (ArgumentException)
            {
                return false; // a network path: not removable
            }
        }
    }

    /// <summary>
    /// Raised on the UI thread after a run was written to the history (with the run's operation, e.g.
    /// <c>windows-revert</c>), so the other pages can show its effect without a manual refresh.
    /// </summary>
    public event Action<string>? RunRecorded;

    /// <summary>Tells the pages about a run that was just written to the history.</summary>
    public void Recorded(SetupRun run)
    {
        NoteSession(run);
        RunRecorded?.Invoke(run.Operation);
    }

    private readonly Dictionary<string, WindowsOpStatus> _sessionWindows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ConfigImportStatus> _sessionConfig = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Remembers how each Windows setting and app configuration fared in this session's runs (recorded or not), so a page
    /// can tell "applied just now" from "already set", and show a failure or a wait for administrator rights until the
    /// state changes. Nothing of it is saved.
    /// </summary>
    public void NoteSession(SetupRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        foreach (var step in run.WindowsSteps)
        {
            _sessionWindows[step.OperationId] = step.Status;
        }

        foreach (var config in run.Config)
        {
            _sessionConfig[config.PortId] = config.Status;
        }
    }

    /// <summary>How a Windows setting fared in this session's latest run that touched it, or <c>null</c>.</summary>
    public WindowsOpStatus? SessionWindows(string operationId) =>
        _sessionWindows.TryGetValue(operationId, out var status) ? status : null;

    /// <summary>How an app's configuration fared in this session's latest run that touched it, or <c>null</c>.</summary>
    public ConfigImportStatus? SessionConfig(string portId) =>
        _sessionConfig.TryGetValue(portId, out var status) ? status : null;

    /// <summary>
    /// Whether a run that changes the PC (apply, install, import, revert, profile) is in progress. Only one runs at a
    /// time, because they share the reader and the writer; and Mesura doesn't exit or restart mid-run, so what a run
    /// did is always recorded.
    /// </summary>
    public bool Changing => _changes > 0;

    private int _changes;

    /// <summary>Raised on the UI thread when <see cref="Changing"/> flips, so every page can enable or disable its actions.</summary>
    public event Action? ChangingChanged;

    /// <summary>
    /// Marks a changing run for as long as the returned object isn't disposed (call on the UI thread). Scopes are counted:
    /// one that ends doesn't clear another that is still open, so Mesura stays "changing" until the last run ends.
    /// </summary>
    public IDisposable BeginChange()
    {
        if (++_changes == 1)
        {
            ChangingChanged?.Invoke();
        }

        return new ChangeScope(this);
    }

    private sealed class ChangeScope(Services services) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (!_done)
            {
                _done = true;
                if (--services._changes == 0)
                {
                    services.ChangingChanged?.Invoke();
                }
            }
        }
    }

    /// <summary>The session log: every page's progress and results, in English, for the Activity page's details.</summary>
    public string SessionLog
    {
        get
        {
            lock (_log)
            {
                return _log.ToString();
            }
        }
    }

    /// <summary>Raised (on any thread) after a line was added to the session log.</summary>
    public event Action<string>? Logged;

    /// <summary>Adds a line to the session log (any thread).</summary>
    public void Log(string line)
    {
        lock (_log)
        {
            _log.Append(DateTime.Now.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)).Append("  ").AppendLine(line);
        }

        Logged?.Invoke(line);
    }

    /// <summary>
    /// When an elevation used another administrator's account, history and app settings live in that account's
    /// profile, not the signed-in user's; this says so (empty when they're the same account).
    /// </summary>
    public string ProfileNote
    {
        get
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return Reader.User.Resolved && !SameUser ? Loc.Format("Common.OtherAccount", identity.Name, Reader.User.Account) : "";
        }
    }

    /// <summary>
    /// What Optimize and Settings say about the Windows catalog in use, with its tone: an old edited copy set aside, an
    /// edited copy that lacks entries this version has, or simply an edited copy. Empty for the built-in catalog.
    /// </summary>
    public (string Text, string Tone) WindowsCatalogNote =>
        CatalogNote(WindowsCatalog, Mesura.Core.Windows.WindowsCatalog.FileName, CatalogFiles.NewSinceCopied(WindowsCatalog));

    /// <summary>The same for the app catalog, which Apps shows.</summary>
    public (string Text, string Tone) AppsCatalogNote => CatalogNote(Apps, AppCatalog.FileName, CatalogFiles.NewSinceCopied(Apps));

    private static (string Text, string Tone) CatalogNote<T>(LoadedCatalog<T> loaded, string file, IReadOnlyList<string> missing) where T : class =>
        loaded.SetAside is { } aside ? (Loc.Format(loaded.IgnoredWhileElevated ? "Catalog.IgnoredElevated" : "Catalog.SetAside", file, aside), Tone.Warn)
        : loaded.Catalog is null || loaded.Source == "built-in" ? ("", Tone.Neutral)
        : missing.Count > 0 ? (Loc.Plural("Catalog.Stale", missing.Count, file, string.Join(", ", missing.Take(4)) + (missing.Count > 4 ? ", …" : "")), Tone.Warn)
        : (Loc.Format("Catalog.Edited", file, loaded.Source), Tone.Info);

    /// <summary>Where a catalog came from, for the log: the shared edited copy, or the built-in one.</summary>
    public static string Describe(string source) => source == "built-in"
        ? "built-in catalog"
        : $@"your edited {System.IO.Path.GetFileName(source)} in %LOCALAPPDATA%\Mesura";

    public static Services Create()
    {
        var tools = new SystemTools();
        var reader = new SystemStateReader(tools);
        var writer = new WindowsWriter(reader.User, tools);
        var sameUser = ProcessUser.Is(reader.User);
        var windows = CatalogFiles.Windows(reader.IsElevated);
        return new Services(
            AppDetectors.Installer(reader.User, reader.IsElevated),
            CatalogFiles.Apps(reader.IsElevated),
            reader,
            new WindowsConfigurator(reader, writer, windows.Catalog),
            windows,
            new SetupStore(),
            new StartupEntry(reader, writer),
            sameUser);
    }
}
