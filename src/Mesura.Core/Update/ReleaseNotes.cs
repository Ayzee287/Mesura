using System.Text.Json;

namespace Mesura.Core.Update;

/// <summary>
/// "What's new" for the version that is running (0.7): short notes written by hand per release, in both languages, carried inside the build
/// (<c>whats-new.json</c>), so they are there offline and need no request. They are for the installed version; the notes of an update that is on offer
/// still come from its signed manifest. The file is <c>{ "0.7": { "en": ["..."], "ru": ["..."] } }</c>, keyed by major.minor so a 0.7.1 build shows the 0.7 notes.
/// </summary>
public sealed class ReleaseNotes
{
    private readonly Dictionary<string, Dictionary<string, IReadOnlyList<string>>> _byVersion;

    private ReleaseNotes(Dictionary<string, Dictionary<string, IReadOnlyList<string>>> byVersion) => _byVersion = byVersion;

    public static ReleaseNotes Empty { get; } = new([]);

    /// <summary>Reads the notes file. Damaged text or a shape that isn't this one gives no notes at all, never an exception: a missing note must not stop a page from showing.</summary>
    public static ReleaseNotes Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(json.TrimStart('﻿'), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var byVersion = new Dictionary<string, Dictionary<string, IReadOnlyList<string>>>(StringComparer.Ordinal);
            foreach (var version in document.RootElement.EnumerateObject())
            {
                var byLanguage = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
                if (version.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var language in version.Value.EnumerateObject().Where(l => l.Value.ValueKind == JsonValueKind.Array))
                {
                    byLanguage[language.Name] = [.. language.Value.EnumerateArray().Where(n => n.ValueKind == JsonValueKind.String).Select(n => n.GetString()!).Where(n => n.Length > 0)];
                }

                byVersion[version.Name] = byLanguage;
            }

            return new ReleaseNotes(byVersion);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            return Empty;
        }
    }

    /// <summary>The notes for a version ("0.7.1" finds "0.7.1", else "0.7") in a language, or in English when that language has none; empty when there are none.</summary>
    public IReadOnlyList<string> For(string version, string language)
    {
        var parts = version.Split('-', '+')[0].Split('.');
        var candidates = parts.Length >= 2 ? new[] { string.Join('.', parts), parts[0] + "." + parts[1] } : [version];
        foreach (var key in candidates)
        {
            if (!_byVersion.TryGetValue(key, out var byLanguage))
            {
                continue;
            }

            if (byLanguage.TryGetValue(language, out var own) && own.Count > 0)
            {
                return own;
            }

            if (byLanguage.TryGetValue("en", out var english) && english.Count > 0)
            {
                return english;
            }
        }

        return [];
    }
}
