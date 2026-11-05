using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mesura.Platform.Windows.Gpu;

/// <summary>
/// What the window needs to know to leave AMD's <c>amdadlx64.dll</c> to Mesura's own command line. A crash inside that DLL (an access violation) cannot be caught by managed code and ends the process, so
/// the window never calls it in its own process: the setup layer (<c>AmdCommandLineRunner</c>, the one owner of that process launch) runs <c>cli\mesura.exe</c>, the same reader and writer, and the window
/// reads what it prints; a crash then costs a child process, not the window and the run it was recording. The command line itself calls ADLX directly (<see cref="IsCommandLine"/>). This class starts no
/// process: it only says where the command line is and reads its report.
/// </summary>
internal static class AmdCommandLine
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };

    /// <summary>Whether this process is the command line (assembly <c>mesura</c>), which calls ADLX in process; the window (<c>Mesura</c>) and everything else go through the command line.</summary>
    public static bool IsCommandLine => string.Equals(Assembly.GetEntryAssembly()?.GetName().Name, "mesura", StringComparison.Ordinal);

    /// <summary>Tests point the launch at a script that hangs or dies, to prove the window survives both.</summary>
    internal static string? PathOverride { get; set; }

    /// <summary>The command line shipped beside the window: <c>cli\mesura.exe</c> in the window's folder; <c>null</c> where it is not there.</summary>
    public static string? Path
    {
        get
        {
            if (PathOverride is { } fixedPath)
            {
                return File.Exists(fixedPath) ? fixedPath : null;
            }

            var path = System.IO.Path.Combine(AppContext.BaseDirectory, "cli", "mesura.exe");
            return File.Exists(path) ? path : null;
        }
    }

    /// <summary>The report <c>mesura gpu amd --json</c> printed, or <c>null</c> when the text is not that report (the catalog line goes to stderr and is skipped).</summary>
    internal static AmdAdlxReport? ParseReport(string text)
    {
        var start = text.IndexOf('{', StringComparison.Ordinal);
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<AmdAdlxReport>(text[start..(end + 1)], Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The report written the way the command line prints it (tests write a report and read it back through <see cref="ParseReport"/>).</summary>
    internal static string ToJson(AmdAdlxReport report) => JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } });
}
