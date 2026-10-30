namespace Mesura.Core.Support;

/// <summary>
/// Where Mesura keeps what changes: settings, history, profiles, logs, staged updates (<c>%LOCALAPPDATA%\Mesura</c>). Never the
/// program folder, which may be read-only (Program Files) and is replaced whole by an update. The environment variable
/// <c>MESURA_DATA_DIR</c> moves it for tests and for a portable copy that keeps its data beside it.
/// </summary>
public static class DataFolder
{
    public const string OverrideVariable = "MESURA_DATA_DIR";

    /// <summary>The data folder, given Windows' <c>%LOCALAPPDATA%</c>.</summary>
    public static string Local(string localAppData) =>
        Environment.GetEnvironmentVariable(OverrideVariable) is { Length: > 0 } custom
            ? Path.GetFullPath(custom)
            : Path.Combine(localAppData, "Mesura");
}
