using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Mesura.Core.Config;

/// <summary>
/// Keeps secrets out of carried settings. A setting carried as one block (<see cref="ConfigSetting.Subtree"/>, such as
/// Vencord's plugins) can hold an API key, a session token or a password that a plugin asked for (Translate's Kagi
/// session and DeepL key, a Last.fm key). Those are never exported, never written from a bundle, and a destination
/// keeps its own. A property counts as secret when a word of its name says so: token, session, secret, password,
/// cookie, credential, webhook, auth, bearer, or a key of a kind that is one (apiKey, accessKey, privateKey; not a
/// hotKey or a toggleKey). The block's own top-level names are never tested: in a map such as Vencord's plugins they
/// are the entries' names (BetterSessions, SecretRingToneEnabler), not settings.
/// </summary>
public static partial class SensitiveValues
{
    private static readonly HashSet<string> SecretWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "token", "tokens", "session", "sessions", "secret", "secrets", "password", "passwords", "passwd", "pwd",
        "cookie", "cookies", "credential", "credentials", "webhook", "webhooks", "auth", "oauth", "bearer", "apikey",
    };

    // The words before "key" that make it a secret one.
    private static readonly HashSet<string> SecretKeyKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "api", "access", "private", "secret", "auth", "client", "license", "licence", "subscription", "app", "encryption", "signing",
    };

    private static readonly JsonDocumentOptions ReadOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>Whether a property name names a secret (see the class summary).</summary>
    public static bool IsSecret(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var words = Word().Matches(name).Select(m => m.Value).ToList();
        return words.Any(SecretWords.Contains)
            || (words.Count > 1 && words[^1].Equals("key", StringComparison.OrdinalIgnoreCase) && SecretKeyKinds.Contains(words[^2]));
    }

    /// <summary>The block without its secret properties, at any depth; text that isn't a JSON object or array is returned as it is.</summary>
    public static string Strip(string serialized)
    {
        ArgumentNullException.ThrowIfNull(serialized);
        if (ParseContainer(serialized) is not { } node)
        {
            return serialized;
        }

        Remove(node, testNames: false);
        return node.ToJsonString();
    }

    /// <summary>
    /// The block to write: <paramref name="incoming"/> without secrets, with the secrets already in the destination's
    /// block (<paramref name="current"/>) kept where they were.
    /// </summary>
    public static string Merge(string incoming, string? current)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        if (ParseContainer(incoming) is not JsonObject result)
        {
            return Strip(incoming);
        }

        Remove(result, testNames: false);
        if (current is not null && ParseContainer(current) is JsonObject existing)
        {
            Keep(existing, result, testNames: false);
        }

        return result.ToJsonString();
    }

    private static JsonNode? ParseContainer(string text)
    {
        try
        {
            return JsonNode.Parse(text, documentOptions: ReadOptions) is { } node && node is JsonObject or JsonArray ? node : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void Remove(JsonNode node, bool testNames = true)
    {
        if (node is JsonObject obj)
        {
            if (testNames)
            {
                foreach (var name in obj.Select(p => p.Key).Where(IsSecret).ToList())
                {
                    obj.Remove(name);
                }
            }

            foreach (var (_, child) in obj)
            {
                if (child is not null)
                {
                    Remove(child);
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array.OfType<JsonNode>())
            {
                Remove(child);
            }
        }
    }

    /// <summary>Copies each secret of <paramref name="from"/> into <paramref name="into"/> at the same place, creating objects on the way.</summary>
    private static void Keep(JsonObject from, JsonObject into, bool testNames = true)
    {
        foreach (var (name, value) in from)
        {
            if (testNames && IsSecret(name))
            {
                into[name] = value?.DeepClone();
            }
            else if (value is JsonObject child && Secrets(child))
            {
                if (into[name] is not JsonObject target)
                {
                    if (into.ContainsKey(name))
                    {
                        continue; // the incoming block has something else there: leave it
                    }

                    target = [];
                    into[name] = target;
                }

                Keep(child, target);
            }
        }
    }

    private static bool Secrets(JsonObject obj) => obj.Any(p => IsSecret(p.Key) || (p.Value is JsonObject child && Secrets(child)));

    // Words of a name: "kagiSession" -> kagi, Session; "deeplApiKey" -> deepl, Api, Key; "API_KEY" -> API, KEY.
    [GeneratedRegex("[A-Z]+(?![a-z])|[A-Z]?[a-z]+|[0-9]+")]
    private static partial Regex Word();
}
