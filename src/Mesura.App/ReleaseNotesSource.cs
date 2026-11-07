using System.IO;
using Mesura.Core.Update;

namespace Mesura.App;

/// <summary>The notes carried in this build (<c>Localization\whats-new.json</c>, an embedded resource), read once; a missing or damaged file gives no notes.</summary>
internal static class ReleaseNotesSource
{
    private static ReleaseNotes? _current;

    public static ReleaseNotes Current => _current ??= Load();

    private static ReleaseNotes Load()
    {
        try
        {
            using var stream = typeof(ReleaseNotesSource).Assembly.GetManifestResourceStream("Mesura.App.Localization.whats-new.json");
            if (stream is null)
            {
                return ReleaseNotes.Empty;
            }

            using var reader = new StreamReader(stream);
            return ReleaseNotes.Parse(reader.ReadToEnd());
        }
        catch (IOException)
        {
            return ReleaseNotes.Empty;
        }
    }
}
