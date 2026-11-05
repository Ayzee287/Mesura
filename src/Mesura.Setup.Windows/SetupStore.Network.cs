using System.Text;
using System.Text.RegularExpressions;

namespace Mesura.Setup.Windows;

// The network adapter snapshots (0.6): one JSON file per snapshot under <root>\network, written whole and atomically. A restore takes a new snapshot of the present state first, so a restore can itself be
// taken back by restoring that one. They are kept apart from the run history, which has its own typed model.
public sealed partial class SetupStore
{
    public string NetworkFolder => Path.Combine(Root, "network");

    /// <summary>Writes (or replaces) a network snapshot, <c>network\&lt;id&gt;.json</c>, atomically (a temporary file, then a move).</summary>
    /// <returns>The full path written.</returns>
    public string WriteNetworkSnapshot(string id, string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var path = NetworkPath(id);
        var temp = path + TempSuffix;
#pragma warning disable RS0030 // The setup component's one file writer: its own network snapshots (class remarks).
        Directory.CreateDirectory(NetworkFolder);
        File.WriteAllBytes(temp, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(json));
        File.Move(temp, path, overwrite: true);
#pragma warning restore RS0030
        return path;
    }

    /// <summary>The text of a network snapshot, or <c>null</c> when there is none by that id.</summary>
    public string? ReadNetworkSnapshot(string id)
    {
        var path = NetworkPath(id);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    /// <summary>The ids of the saved network snapshots, newest first.</summary>
    public IReadOnlyList<string> ListNetworkSnapshots() =>
        Directory.Exists(NetworkFolder)
            ? [.. Directory.EnumerateFiles(NetworkFolder, "*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>().OrderDescending(StringComparer.Ordinal)]
            : [];

    private string NetworkPath(string id) => Path.Combine(NetworkFolder, Regex.Replace(id, @"[^0-9A-Za-z._-]", "_") + ".json");
}
