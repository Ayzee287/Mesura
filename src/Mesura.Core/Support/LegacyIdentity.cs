namespace Mesura.Core.Support;

/// <summary>
/// The product's identity before it was renamed to Mesura (D022): "FPS Utility", code identifier <c>FpsUtility</c>.
/// Files written before the rename carry these names — config bundles, catalogs, profiles, history records, Track A
/// sessions — and the data folder was <c>%LOCALAPPDATA%\FpsUtility</c>. They stay readable; nothing new is written
/// under the old names. This is the one place the old identity is spelled.
/// </summary>
public static class LegacyIdentity
{
    /// <summary>The old code identifier, also the old data folder name under <c>%LOCALAPPDATA%</c>.</summary>
    public const string CodeName = "FpsUtility";

    /// <summary>The Track A capture format name before the rename.</summary>
    public const string TrackAFormatName = "fpsutility-track-a-capture";

    private const string CurrentPrefix = "Mesura.";

    /// <summary>
    /// Whether a file's <c>kind</c> is the current one or its pre-rename spelling (<c>FpsUtility.X</c> for
    /// <c>Mesura.X</c>).
    /// </summary>
    public static bool IsKind(string? actual, string current)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(current);
        if (actual is null)
        {
            return false;
        }

        return actual == current
            || (current.StartsWith(CurrentPrefix, StringComparison.Ordinal)
                && actual == CodeName + "." + current[CurrentPrefix.Length..]);
    }
}
