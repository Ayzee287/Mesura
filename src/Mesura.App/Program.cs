using System.Windows;

namespace Mesura.App;

/// <summary>
/// The entry point, written out (the one WPF generates is switched off with <c>StartupObject</c>) so a failure while the
/// application starts, before any window or the in-app session log exists, is written to the startup log and shown with
/// where that file is, instead of the process ending silently.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        StartupLog.Begin(args);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            StartupLog.Fatal("the process (unhandled, it is ending)", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => StartupLog.Fatal("a background task (unobserved)", e.Exception);
        if (MissingFiles() is { Count: > 0 } missing)
        {
            // Mesura.exe copied away from its folder: WPF's native libraries sit beside it (no self-extraction, so a locked-down
            // TEMP can't stop the start). Say so plainly, before WPF fails inside its own message pump.
            StartupLog.Note($"ERROR: files missing beside Mesura.exe: {string.Join(", ", missing)}");
            Show($"Mesura needs the other files from its folder ({string.Join(", ", missing)}). Unzip the whole archive and start Mesura.exe from there, not a copy of the exe alone.\n\n"
                + $"Mesura нужны остальные файлы из её папки ({string.Join(", ", missing)}). Распакуйте архив целиком и запускайте Mesura.exe оттуда, а не копию одного exe.\n\n{StartupLog.Path}");
            return 2;
        }

        try
        {
            StartupLog.Note("stage: creating the application");
            var app = new App();
            app.InitializeComponent();
            app.LoadResources();
            StartupLog.Note("stage: resources loaded, running");
            var code = app.Run();
            StartupLog.Note($"exit code {code}");
            return code;
        }
        catch (Exception ex)
        {
            StartupLog.Fatal("startup", ex);
            Show($"Mesura could not start / Mesura не удалось запустить:\n\n{ex.GetType().Name}: {ex.Message}\n\n"
                + $"Details / Подробности:\n{StartupLog.Path}");
            return 1;
        }
    }

    /// <summary>
    /// The native files WPF loads from beside the executable that aren't there. Only a published single file is checked: a build
    /// run from Visual Studio or <c>dotnet run</c> finds them in the shared runtime instead.
    /// </summary>
    private static List<string> MissingFiles() =>
        System.IO.File.Exists(System.IO.Path.Combine(AppContext.BaseDirectory, "Mesura.dll")) ? [] : // not a single file
        [.. new[] { "PresentationNative_cor3.dll", "wpfgfx_cor3.dll", "PenImc_cor3.dll", "vcruntime140_cor3.dll", "D3DCompiler_47_cor3.dll" }
            .Where(name => !System.IO.File.Exists(System.IO.Path.Combine(AppContext.BaseDirectory, name)))];

    private static void Show(string text)
    {
        try
        {
            MessageBox.Show(text, "Mesura", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception shown) when (shown is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            StartupLog.Note($"the error message could not be shown: {shown.Message}");
        }
    }
}
