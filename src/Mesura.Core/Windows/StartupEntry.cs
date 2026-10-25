namespace Mesura.Core.Windows;

/// <summary>
/// Whether Mesura starts in the tray when the signed-in user signs in: one value under that user's
/// <c>HKCU\...\CurrentVersion\Run</c> key, <c>"&lt;Mesura.exe&gt;" --tray</c>. Read and written through the same reader and
/// writer as the catalog operations, so it goes to the signed-in user (not the elevated account) and is refused when that
/// user is unknown. It is an app preference the tray menu shows as a tick, so it isn't a history run.
/// </summary>
public sealed class StartupEntry(ISystemReader reader, IWindowsWriter writer)
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "Mesura";

    /// <summary>The command a sign-in runs for this executable.</summary>
    public static string Command(string executable) => $"\"{executable}\" --tray";

    /// <summary>Whether a sign-in starts this executable in the tray (an entry pointing elsewhere doesn't count).</summary>
    public bool IsOn(string executable)
    {
        var value = reader.ReadRegistry("HKCU", RunKey, ValueName);
        return value.ValueExists && value.Kind == RegistryKind.String
            && string.Equals(value.Value, Command(executable), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether a Mesura entry exists that starts some other copy (Mesura was moved, or another build).</summary>
    public bool PointsElsewhere(string executable)
    {
        var value = reader.ReadRegistry("HKCU", RunKey, ValueName);
        return value.ValueExists && !IsOn(executable);
    }

    /// <summary>Adds (or corrects) the entry, or removes it; reads it back.</summary>
    /// <exception cref="InvalidOperationException">The signed-in user is unknown, or Windows didn't keep the change.</exception>
    public void Set(string executable, bool on)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        if (!reader.User.Resolved)
        {
            throw new InvalidOperationException($"Start at sign-in can't be changed: {reader.User.Reason}");
        }

        if (on)
        {
            writer.SetRegistry("HKCU", RunKey, ValueName, RegistryKind.String, Command(executable));
        }
        else
        {
            writer.DeleteRegistry("HKCU", RunKey, ValueName);
        }

        if (IsOn(executable) != on)
        {
            throw new InvalidOperationException("Windows didn't keep the change to the sign-in entry.");
        }
    }
}
