using System.Globalization;
using System.Text.Json.Nodes;
using Mesura.Core.Privacy;

namespace Mesura.Core.TrackA;

/// <summary>Where two JSON documents differ, as JSON Pointers to the smallest differing values. Values never leave it.</summary>
public static class JsonDiff
{
    public static IReadOnlyList<string> Pointers(JsonNode? a, JsonNode? b)
    {
        var found = new List<string>();
        Walk(a, b, "", found);
        return found;
    }

    private static void Walk(JsonNode? a, JsonNode? b, string pointer, List<string> found)
    {
        if (JsonNode.DeepEquals(a, b))
        {
            return;
        }

        switch (a, b)
        {
            case (JsonObject left, JsonObject right):
                foreach (var name in left.Select(pair => pair.Key).Union(right.Select(pair => pair.Key), StringComparer.Ordinal))
                {
                    var child = pointer + "/" + JsonStrings.Escape(name);
                    if (left.ContainsKey(name) && right.ContainsKey(name))
                    {
                        Walk(left[name], right[name], child, found);
                    }
                    else
                    {
                        found.Add(child);
                    }
                }

                break;
            case (JsonArray left, JsonArray right) when left.Count == right.Count:
                for (var i = 0; i < left.Count; i++)
                {
                    Walk(left[i], right[i], pointer + "/" + i.ToString(CultureInfo.InvariantCulture), found);
                }

                break;
            default:
                found.Add(pointer);
                break;
        }
    }
}
