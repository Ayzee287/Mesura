using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Mesura.Core.Support;

namespace Mesura.Setup.Windows.Nvidia;

/// <summary>
/// Folders under <c>%ProgramData%\MesuraTools</c> that only the system and administrators can write to: where an elevated Mesura keeps what it will run or read with
/// administrator rights (a verified copy of NVIDIA Profile Inspector, the profile file it imports, NVIDIA's driver package). A folder made under ProgramData the plain way is NOT
/// such a folder: Windows gives <c>BUILTIN\Users</c> the right to create files in the subfolders of ProgramData, so <see cref="ProgramFolderTrust"/> rightly refuses it (measured
/// on Windows 11, 2026-11-02). These are made with an explicit access list that does not inherit, and the result is checked after it is made, not trusted.
/// </summary>
#pragma warning disable RS0030 // The NVIDIA tool owner's protected folders: only inside %ProgramData%\MesuraTools (class remarks).
[SupportedOSPlatform("windows")]
public static class ProtectedToolFolder
{
    private const string RootName = "MesuraTools";

    /// <summary>The rights a protected folder grants: the system and administrators everything, users only to read.</summary>
    public static IReadOnlyList<(SecurityIdentifier Who, FileSystemRights Rights)> Grants() =>
    [
        (new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl),
        (new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl),
        (new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute),
    ];

    /// <summary>A named folder that stays between runs (the driver downloads), or <c>null</c> when no protected one could be made.</summary>
    public static string? Persistent(string name, string? programData = null)
    {
        var root = Root(programData ?? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), out _);
        return root is null ? null : Protected(Path.Combine(root, name), out _);
    }

    /// <summary>A new, empty folder for one run of <paramref name="purpose"/>; the ones left by earlier runs are removed.</summary>
    /// <exception cref="InvalidOperationException">No folder that only administrators can write to could be made.</exception>
    public static string Fresh(string purpose, string? programData = null)
    {
        var root = Root(programData ?? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), out var why)
            ?? throw new InvalidOperationException("no folder that only administrators can write to could be made under ProgramData: " + why);
        foreach (var old in Directory.EnumerateDirectories(root, purpose + "-*"))
        {
            try
            {
                Directory.Delete(old, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Still in use by a run that is going on.
            }
        }

        return Protected(Path.Combine(root, purpose + "-" + Guid.NewGuid().ToString("N")[..12]), out var made)
            ?? throw new InvalidOperationException("the folder could not be protected: " + made);
    }

    private static string? Root(string programData, out string? why) =>
        Protected(Path.Combine(programData, RootName), out why) ?? Protected(Path.Combine(programData, RootName + "-" + Guid.NewGuid().ToString("N")[..12]), out why);

    /// <returns>The folder once it exists with the protected access list, else <c>null</c> (and nothing of it is trusted); <paramref name="why"/> says what still lets a non-administrator change it.</returns>
    private static string? Protected(string path, out string? why)
    {
        if (!Directory.Exists(path))
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (var (who, rights) in Grants())
            {
                security.AddAccessRule(new FileSystemAccessRule(who, rights, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            }

            try
            {
                new DirectoryInfo(path).Create(security);
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException or ArgumentException)
            {
                why = "it could not be created with administrator rights (" + e.Message + ")";
                return null;
            }
        }

        why = ProgramFolderTrust.Why(path);
        return why is null ? path : null;
    }
}
