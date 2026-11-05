using System.Text;
using System.Text.RegularExpressions;

namespace Mesura.Setup.Windows;

// The GPU change records (0.6): one JSON file per change under <root>\gpu, written before the driver is touched so that a change can be undone even if the process dies. They are kept apart from the
// run history (history\<id>\run.json), which has its own typed model for installs, configs and Windows changes and lists every record it finds.
public sealed partial class SetupStore
{
    public string GpuFolder => Path.Combine(Root, "gpu");

    /// <summary>Writes (or replaces) a GPU change record, <c>gpu\&lt;id&gt;.json</c>, atomically (a temporary file, then a move).</summary>
    /// <returns>The full path written.</returns>
    public string WriteGpuChange(string id, string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var path = GpuPath(id);
        var temp = path + TempSuffix;
#pragma warning disable RS0030 // The setup component's one file writer: its own GPU change records (class remarks).
        Directory.CreateDirectory(GpuFolder);
        File.WriteAllBytes(temp, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(json));
        File.Move(temp, path, overwrite: true);
#pragma warning restore RS0030
        return path;
    }

    /// <summary>The text of a GPU change record, or <c>null</c> when there is none by that id.</summary>
    public string? ReadGpuChange(string id)
    {
        var path = GpuPath(id);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    /// <summary>The ids of the recorded GPU changes, newest first.</summary>
    public IReadOnlyList<string> ListGpuChanges() =>
        Directory.Exists(GpuFolder)
            ? [.. Directory.EnumerateFiles(GpuFolder, "*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>().OrderDescending(StringComparer.Ordinal)]
            : [];

    private string GpuPath(string id) => Path.Combine(GpuFolder, Regex.Replace(id, @"[^0-9A-Za-z._-]", "_") + ".json");
}
