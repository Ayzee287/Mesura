using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mesura.Core.Config;

/// <summary>
/// An editable configuration file that keeps every key it was not asked about. Export reads one allowlisted path;
/// import writes one allowlisted path. Values are carried as their serialized text, so a JSON boolean or number keeps
/// its type across the round trip.
/// </summary>
public interface IConfigDocument
{
    /// <summary>
    /// The serialized value at the path, or <c>null</c> if absent. A container (JSON object/array) is returned only
    /// when <paramref name="allowContainer"/> is set; otherwise a container yields <c>null</c> so nothing large is
    /// captured by accident.
    /// </summary>
    string? TryGet(string path, bool allowContainer = false);

    /// <summary>Writes the serialized value at the path, creating containers as needed. Replaces in place (idempotent).</summary>
    /// <returns><c>true</c> if written; <c>false</c> if the target could not be reached without discarding other keys.</returns>
    bool Set(string path, string serialized);

    /// <summary>The whole file, with unknown keys preserved, ready to write back.</summary>
    string ToText();
}

/// <summary>Steam/Valve KeyValues, over <see cref="VdfDocument"/>. Paths are <c>Root/section/key</c>.</summary>
public sealed class VdfConfigDocument : IConfigDocument
{
    private readonly VdfDocument _doc;

    public VdfConfigDocument(string text) => _doc = VdfDocument.Parse(text);

    public string? TryGet(string path, bool allowContainer = false) => _doc.GetValue(path);

    public bool Set(string path, string serialized) => _doc.SetValue(path, serialized);

    public string ToText() => _doc.ToText();
}

/// <summary>
/// A JSON object document (Brave <c>Preferences</c>, Vencord/Discord <c>settings.json</c>), over a mutable
/// <see cref="JsonNode"/> DOM. Paths are <c>/a/b/c</c> property chains. Captured values keep their JSON type.
/// </summary>
public sealed class JsonConfigDocument : IConfigDocument
{
    private readonly JsonObject _root;
    private readonly JsonSerializerOptions _style;

    public JsonConfigDocument(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var node = JsonNode.Parse(text, nodeOptions: null, documentOptions: new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });
        _root = node as JsonObject ?? throw new FormatException("The JSON config root is not an object.");
        _style = StyleOf(text);
    }

    /// <summary>
    /// Writes back in the file's own layout — compact (Chromium's <c>Preferences</c>) or indented with the file's indent
    /// (Vencord, Discord) — and without escaping non-ASCII text, so an import changes the values it sets, not the
    /// whole file's formatting.
    /// </summary>
    private static JsonSerializerOptions StyleOf(string text)
    {
        var options = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var lines = text.Trim().Split('\n', 3);
        if (lines.Length < 2)
        {
            return options;
        }

        var indent = lines[1].Length - lines[1].TrimStart(' ', '\t').Length;
        options.WriteIndented = true;
        options.IndentCharacter = lines[1].StartsWith('\t') ? '\t' : ' ';
        options.IndentSize = Math.Clamp(indent, 1, 8);
        options.NewLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        return options;
    }

    public string? TryGet(string path, bool allowContainer = false)
    {
        var segments = Split(path);
        JsonNode? current = _root;
        foreach (var segment in segments)
        {
            if (current is JsonObject obj && obj.TryGetPropertyValue(segment, out var next))
            {
                current = next;
            }
            else
            {
                return null;
            }
        }

        if (current is null)
        {
            return null;
        }

        // A container (object or array) is captured only when the allowlist opts in via Subtree; otherwise only scalars.
        if (current is JsonObject && !allowContainer)
        {
            return null;
        }

        return current.ToJsonString();
    }

    public bool Set(string path, string serialized)
    {
        var segments = Split(path);
        var parent = _root;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (!parent.TryGetPropertyValue(segments[i], out var next) || next is null)
            {
                var created = new JsonObject();
                parent[segments[i]] = created;
                parent = created;
            }
            else if (next is JsonObject obj)
            {
                parent = obj;
            }
            else
            {
                return false; // an existing non-object sits where a container is needed
            }
        }

        parent[segments[^1]] = JsonNode.Parse(serialized);
        return true;
    }

    public string ToText() => _root.ToJsonString(_style);

    private static string[] Split(string path) => path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>
/// Spotify's <c>prefs</c>: one <c>key=value</c> per line, the value an opaque JSON-ish token. Paths are the bare key.
/// Unknown lines, blank lines and ordering are preserved; a replaced key keeps its position; a new key is appended.
/// </summary>
public sealed class PrefsConfigDocument : IConfigDocument
{
    private readonly List<string> _lines;

    public PrefsConfigDocument(string text) =>
        _lines = [.. text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')];

    public string? TryGet(string path, bool allowContainer = false)
    {
        var index = IndexOf(path);
        if (index < 0)
        {
            return null;
        }

        return _lines[index][(path.Length + 1)..];
    }

    public bool Set(string path, string serialized)
    {
        var line = $"{path}={serialized}";
        var index = IndexOf(path);
        if (index >= 0)
        {
            _lines[index] = line;
        }
        else
        {
            // Append before any trailing blank lines, so the file keeps its single trailing newline.
            var insert = _lines.Count;
            while (insert > 0 && _lines[insert - 1].Length == 0)
            {
                insert--;
            }

            _lines.Insert(insert, line);
        }

        return true;
    }

    public string ToText() => string.Join('\n', _lines);

    private int IndexOf(string key)
    {
        var prefix = key + "=";
        return _lines.FindIndex(l => l.StartsWith(prefix, StringComparison.Ordinal));
    }
}
