using System.Text.Json;
using Mesura.Core.Config;
using Mesura.Core.Support;

namespace Mesura.Core.Setup;

/// <summary>What a file the user opened in Backups is.</summary>
public enum BackupKind
{
    /// <summary>Not a Mesura backup (or not JSON at all).</summary>
    Unknown,

    /// <summary>A PC setup (<see cref="SetupProfile"/>, <c>*.profile.json</c>): apps, their settings, Windows settings.</summary>
    PcSetup,

    /// <summary>The settings of apps only (<see cref="ConfigBundleFile"/>): never touches Windows.</summary>
    AppSettings,
}

/// <summary>
/// Tells the two kinds of backup apart by the <c>kind</c> the file carries, so one "Open a backup" door can send each file
/// to the screen that knows what to do with it. It only reads; the screens still parse and validate in full.
/// </summary>
public static class BackupFile
{
    /// <summary>The kind of a backup file's text; <see cref="BackupKind.Unknown"/> for anything else, including damaged JSON.</summary>
    public static BackupKind Detect(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return BackupKind.Unknown;
        }

        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return BackupKind.Unknown;
            }

            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (!property.NameEquals("kind") && !string.Equals(property.Name, "kind", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var kind = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
                return LegacyIdentity.IsKind(kind, SetupProfile.FileKind) ? BackupKind.PcSetup
                    : LegacyIdentity.IsKind(kind, ConfigBundleFile.FileKind) ? BackupKind.AppSettings
                    : BackupKind.Unknown;
            }
        }
        catch (JsonException)
        {
        }

        return BackupKind.Unknown;
    }
}
