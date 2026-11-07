using System.Text.Json;
using Mesura.Core.Windows;

namespace Mesura.Core.Bonus;

/// <summary>One cursor role of Windows (<c>Arrow</c>, <c>Hand</c>, ...) and the file of the pack that fills it.</summary>
public sealed record CursorRole(string ValueName, string File);

/// <summary>
/// The bonus cursor pack "W11 Cursors Light Free v2.2" by Jepri Creations: which file fills which of Windows' cursor roles. It mirrors the
/// pack's own <c>Install.inf</c> (the roles and the scheme name), minus what an installer must not do here: no machine-wide
/// <c>Runonce</c> entry, no copy into <c>%WINDIR%</c>, no odd <c>precisionhair</c> value that Windows has no role for.
/// </summary>
public static class CursorPack
{
    /// <summary>The pack's folder name under Mesura's data folder.</summary>
    public const string Id = "w11-light-2.2";

    public const string Author = "Jepri Creations";

    /// <summary>The scheme's name as the pack names it; it is what Mouse properties shows.</summary>
    public const string SchemeName = "W11 Cursor Light Free v2.2 by Jepri Creations";

    public const string CursorsKey = @"Control Panel\Cursors";

    public const string SchemesKey = @"Control Panel\Cursors\Schemes";

    /// <summary>The roles in the order Windows lists them in a scheme (the order of the comma-separated value under <c>Schemes</c>).</summary>
    public static IReadOnlyList<CursorRole> Roles { get; } =
    [
        new("Arrow", "pointer.cur"),
        new("Help", "help.cur"),
        new("AppStarting", "working.ani"),
        new("Wait", "busy.ani"),
        new("Crosshair", "precision.cur"),
        new("IBeam", "beam.cur"),
        new("NWPen", "handwriting.cur"),
        new("No", "unavailable.cur"),
        new("SizeNS", "vert.cur"),
        new("SizeWE", "horz.cur"),
        new("SizeNWSE", "dgn1.cur"),
        new("SizeNESW", "dgn2.cur"),
        new("SizeAll", "move.cur"),
        new("UpArrow", "alternate.cur"),
        new("Hand", "link.cur"),
        new("Person", "person.cur"),
        new("Pin", "pin.cur"),
    ];

    /// <summary>The distinct files of the pack.</summary>
    public static IReadOnlyList<string> Files { get; } = [.. Roles.Select(r => r.File).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>The value names the bonus may touch under <see cref="CursorsKey"/>: the default value (the scheme's name) and the roles.</summary>
    public static IReadOnlySet<string> ValueNames { get; } = new HashSet<string>(["", .. Roles.Select(r => r.ValueName)], StringComparer.OrdinalIgnoreCase);

    /// <summary>The path a role's value holds once the pack lives in <paramref name="folder"/>.</summary>
    public static string PathFor(string folder, CursorRole role) => Path.Combine(folder, role.File);

    /// <summary>The scheme entry's value: every role's path, in the order Windows lists them.</summary>
    public static string SchemeValue(string folder) => string.Join(',', Roles.Select(r => PathFor(folder, r)));
}

/// <summary>Where the bonus stands for this user.</summary>
public enum CursorBonusState
{
    /// <summary>Windows uses other cursors (or none were chosen); the pack isn't applied.</summary>
    NotApplied,

    /// <summary>Every role points at the pack's files, and the files are there.</summary>
    Applied,

    /// <summary>The pack was applied and something changed it since: some roles still point at it, others don't, or a file is gone.</summary>
    Changed,
}

/// <summary>The pack's files for one user, kept in a folder of Mesura's own (implemented by the setup component's single file writer).</summary>
public interface ICursorFiles
{
    /// <summary>Where the pack's files live.</summary>
    string Folder { get; }

    /// <summary>Whether every file of the pack is in <see cref="Folder"/> with its exact length.</summary>
    bool AllPresent();

    /// <summary>Puts every file of the pack in <see cref="Folder"/> (from the copy inside Mesura); throws when one can't be written.</summary>
    void Install();

    /// <summary>Takes the pack's files away again; never throws for a file Windows still holds.</summary>
    void Remove();

    /// <summary>What the bonus saved about the cursors from before, or <c>null</c>.</summary>
    string? ReadSaved();

    void WriteSaved(string json);

    void DeleteSaved();
}

/// <summary>Asks the signed-in session to load the cursors from the registry again, so the change shows without signing out.</summary>
public interface ICursorSession
{
    /// <summary><c>true</c> when Windows was asked; <c>false</c> when the call failed (the cursors then change at the next sign-in).</summary>
    bool Reload();
}

/// <summary>What <see cref="CursorBonus.Apply"/> or <see cref="CursorBonus.Restore"/> did.</summary>
public sealed record CursorBonusResult(bool Changed, bool Reloaded, string Detail);

/// <summary>
/// The cursor bonus (0.7): a pack of cursors Mesura offers on its own, outside the profiles. It is not a Windows setting of the catalog and not a history run
/// (like <see cref="StartupEntry"/> it is a preference the user turns on and off): it copies the pack's files into Mesura's folder, points the signed-in
/// user's cursor roles at them, reads every value back, and keeps what was there before so one click puts it back. A role the user changed after the pack
/// was applied is left alone by <see cref="Restore"/>. Per-user only: it writes through the signed-in user's hive and never anything machine-wide.
/// </summary>
public sealed class CursorBonus(ISystemReader reader, IWindowsWriter writer, ICursorFiles files, ICursorSession session)
{
    /// <param name="Expandable">The value was REG_EXPAND_SZ (the reader reports such a value without a kind); it comes back as the kind it was.</param>
    private sealed record SavedValue(string Name, bool Existed, string? Value, bool Expandable = false);

    private sealed record Saved(string Pack, string SavedAtUtc, List<SavedValue> Values, SavedValue Scheme);

    /// <summary>Why the bonus can't be changed right now, or <c>null</c> when it can.</summary>
    public string? BlockedReason =>
        reader.User.Resolved ? null : $"The cursors can't be changed: {reader.User.Reason}";

    public CursorBonusState State()
    {
        var ours = Roles().Count(r => Current(r.ValueName) == Expected(r));
        if (ours == CursorPack.Roles.Count && files.AllPresent())
        {
            return CursorBonusState.Applied;
        }

        return ours > 0 && files.ReadSaved() is not null ? CursorBonusState.Changed : CursorBonusState.NotApplied;
    }

    /// <summary>Whether there is something to put back (the bonus saved the cursors from before).</summary>
    public bool CanRestore => files.ReadSaved() is not null;

    /// <summary>Applies the pack. Nothing changes if any step fails: the roles go back to what they were and the exception says why.</summary>
    /// <exception cref="InvalidOperationException">The user is unknown, a file couldn't be written, or Windows didn't keep a value.</exception>
    public CursorBonusResult Apply()
    {
        if (BlockedReason is { } blocked)
        {
            throw new InvalidOperationException(blocked);
        }

        if (State() == CursorBonusState.Applied)
        {
            return new CursorBonusResult(false, false, "The cursors are already applied.");
        }

        // What is there now is what a restore must bring back, unless an earlier, interrupted apply already saved it (then some roles are ours).
        var before = Capture();
        var keepEarlier = files.ReadSaved() is not null && Roles().Any(r => Current(r.ValueName) == Expected(r));
        if (!keepEarlier)
        {
            files.WriteSaved(JsonSerializer.Serialize(before));
        }

        try
        {
            files.Install();
            foreach (var role in CursorPack.Roles)
            {
                writer.SetRegistryExpandString("HKCU", CursorPack.CursorsKey, role.ValueName, Expected(role));
            }

            writer.SetRegistryExpandString("HKCU", CursorPack.CursorsKey, "", CursorPack.SchemeName);
            writer.SetRegistry("HKCU", CursorPack.SchemesKey, CursorPack.SchemeName, RegistryKind.String, CursorPack.SchemeValue(files.Folder));
            if (State() != CursorBonusState.Applied || Current("") != CursorPack.SchemeName)
            {
                throw new InvalidOperationException("Windows didn't keep the cursor values.");
            }
        }
        catch
        {
            // Back to what was there. If even that fails (a locked key), the saved cursors and the files stay, so Restore can finish it later.
            if (TryPutBack(before))
            {
                if (!keepEarlier)
                {
                    files.DeleteSaved();
                }

                files.Remove();
            }

            throw;
        }

        return new CursorBonusResult(true, session.Reload(), "The cursors are applied.");
    }

    /// <summary>Puts back the cursors from before and removes the pack's files. A role changed since the pack was applied is left as it is.</summary>
    /// <exception cref="InvalidOperationException">The user is unknown, or there is nothing saved to put back.</exception>
    public CursorBonusResult Restore()
    {
        if (BlockedReason is { } blocked)
        {
            throw new InvalidOperationException(blocked);
        }

        var saved = ReadSaved() ?? throw new InvalidOperationException("There is nothing to put back: the cursors haven't been applied by Mesura.");
        PutBack(saved, onlyWhereOurs: true);
        var reloaded = session.Reload();
        files.Remove();
        files.DeleteSaved();
        return new CursorBonusResult(true, reloaded, "The cursors from before are back.");
    }

    // ---- the values -----------------------------------------------------------------------------------------------------

    private static IReadOnlyList<CursorRole> Roles() => CursorPack.Roles;

    private string Expected(CursorRole role) => CursorPack.PathFor(files.Folder, role);

    private string? Current(string name)
    {
        var reading = reader.ReadRegistry("HKCU", CursorPack.CursorsKey, name);
        return reading.ValueExists ? reading.Value : null;
    }

    private Saved Capture()
    {
        var values = new List<SavedValue>();
        foreach (var name in CursorPack.ValueNames)
        {
            var reading = reader.ReadRegistry("HKCU", CursorPack.CursorsKey, name);
            values.Add(new SavedValue(name, reading.ValueExists, reading.ValueExists ? reading.Value : null, reading.ValueExists && reading.Kind is null));
        }

        var scheme = reader.ReadRegistry("HKCU", CursorPack.SchemesKey, CursorPack.SchemeName);
        return new Saved(CursorPack.Id, DateTimeOffset.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture), values,
            new SavedValue(CursorPack.SchemeName, scheme.ValueExists, scheme.ValueExists ? scheme.Value : null, scheme.ValueExists && scheme.Kind is null));
    }

    private Saved? ReadSaved()
    {
        if (files.ReadSaved() is not { } json)
        {
            return null;
        }

        try
        {
            var saved = JsonSerializer.Deserialize<Saved>(json);
            // The file is the user's to edit: only the pack's own value names, only plain text, only this pack.
            if (saved is null || saved.Pack != CursorPack.Id || saved.Values.Any(v => !CursorPack.ValueNames.Contains(v.Name) || !Plain(v.Value))
                || !Plain(saved.Scheme.Value) || saved.Scheme.Name != CursorPack.SchemeName)
            {
                return null;
            }

            return saved;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool Plain(string? value) => value is null || (value.Length <= 520 && !value.Any(char.IsControl));

    private bool TryPutBack(Saved saved)
    {
        try
        {
            PutBack(saved, onlyWhereOurs: false);
            return true;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or InvalidOperationException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>
    /// Writes the saved values back, only where the registry differs from them. With <paramref name="onlyWhereOurs"/>, a role that no longer holds the pack's path stays as the user left it.
    /// A saved value that was never there is deleted again; a text value comes back as the kind it was (plain or expandable), so the registry ends up as it began.
    /// </summary>
    private void PutBack(Saved saved, bool onlyWhereOurs)
    {
        foreach (var value in saved.Values)
        {
            var role = CursorPack.Roles.FirstOrDefault(r => string.Equals(r.ValueName, value.Name, StringComparison.OrdinalIgnoreCase));
            var ours = role is not null ? Expected(role) : CursorPack.SchemeName;
            var current = Current(value.Name);
            var target = value.Existed ? value.Value : null;
            if (current == target || (onlyWhereOurs && current != ours))
            {
                continue;
            }

            Write(CursorPack.CursorsKey, value.Name, target, value.Expandable);
        }

        var schemeNow = reader.ReadRegistry("HKCU", CursorPack.SchemesKey, CursorPack.SchemeName);
        var schemeTarget = saved.Scheme.Existed ? saved.Scheme.Value : null;
        var schemeCurrent = schemeNow.ValueExists ? schemeNow.Value : null;
        if (schemeCurrent != schemeTarget && (!onlyWhereOurs || schemeCurrent == CursorPack.SchemeValue(files.Folder)))
        {
            Write(CursorPack.SchemesKey, saved.Scheme.Name, schemeTarget, saved.Scheme.Expandable);
        }
    }

    private void Write(string key, string name, string? value, bool expandable)
    {
        if (value is null)
        {
            writer.DeleteRegistry("HKCU", key, name);
        }
        else if (value.Length == 0 || !expandable)
        {
            writer.SetRegistry("HKCU", key, name, RegistryKind.String, value);
        }
        else
        {
            writer.SetRegistryExpandString("HKCU", key, name, value);
        }
    }
}
