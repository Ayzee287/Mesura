using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Mesura.Core.Support;

/// <summary>
/// Whether a folder can be changed by something that is not an administrator (0.3.0-rc2, docs/security.md). It decides whether Mesura may
/// run elevated from that folder: a program started with administrator rights is only as trustworthy as the files it is made of, and a
/// folder that any program running as the user can write to lets such a program replace <c>Mesura.exe</c> (or a DLL beside it) and be
/// elevated the next time the user approves the prompt. Read from the folder's access control list, not by trying to write (an elevated
/// process can write anywhere, which proves nothing).
/// </summary>
[SupportedOSPlatform("windows")]
public static class ProgramFolderTrust
{
    private static readonly SecurityIdentifier[] Trusted =
    [
        new(WellKnownSidType.LocalSystemSid, null),
        new(WellKnownSidType.BuiltinAdministratorsSid, null),
        new(WellKnownSidType.CreatorOwnerSid, null),
        new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"), // NT SERVICE\TrustedInstaller
    ];

    private const FileSystemRights Changes = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories
        | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    private const FileSystemRights Replaces = FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    /// <summary>
    /// Whether a principal that isn't an administrator, the system or the installer can change <paramref name="folder"/> (add, replace or
    /// delete files in it), or replace the folder itself through a parent. When the access list can't be read the answer is "yes": an
    /// unreadable list is not a reason to trust the folder.
    /// </summary>
    public static bool IsUserWritable(string folder) => Why(folder) is not null;

    /// <summary>The first thing that makes the folder changeable by a non-administrator, in words; <c>null</c> when it is protected.</summary>
    public static string? Why(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        try
        {
            var directory = new DirectoryInfo(Path.GetFullPath(folder));
            if (!directory.Exists)
            {
                return "the folder doesn't exist";
            }

            var self = directory.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
            if (self.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner && !IsTrusted(owner))
            {
                return $"it is owned by {Describe(owner)}, who can change who may write to it";
            }

            if (Offending(self, Changes, thisFolderOnly: false) is { } own)
            {
                return own;
            }

            for (var parent = directory.Parent; parent is not null; parent = parent.Parent)
            {
                var rules = parent.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
                if (rules.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier parentOwner && !IsTrusted(parentOwner) && parent.Parent is not null)
                {
                    return $"its parent folder {parent.FullName} is owned by {Describe(parentOwner)}";
                }

                if (Offending(rules, Replaces, thisFolderOnly: true) is { } up)
                {
                    return $"its parent folder {parent.FullName}: {up}";
                }
            }

            return null;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or InvalidOperationException or PlatformNotSupportedException or ArgumentException)
        {
            return "its permissions couldn't be read (" + e.Message + ")";
        }
    }

    private static string? Offending(FileSystemSecurity security, FileSystemRights rights, bool thisFolderOnly)
    {
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow || (rule.FileSystemRights & rights) == 0)
            {
                continue;
            }

            if (rule.IdentityReference is not SecurityIdentifier sid || IsTrusted(sid))
            {
                continue;
            }

            // An entry that only reaches files and folders inside (not the folder itself) doesn't let anyone replace this folder.
            if (thisFolderOnly && rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly))
            {
                continue;
            }

            return $"{Describe(sid)} may {Name(rule.FileSystemRights & rights)} here";
        }

        return null;
    }

    private static bool IsTrusted(SecurityIdentifier sid) => Trusted.Any(t => t.Equals(sid));

    private static string Describe(SecurityIdentifier sid)
    {
        try
        {
            return sid.Translate(typeof(NTAccount)).Value;
        }
        catch (Exception e) when (e is IdentityNotMappedException or SystemException)
        {
            return sid.Value;
        }
    }

    private static string Name(FileSystemRights rights) =>
        (rights & (FileSystemRights.WriteData | FileSystemRights.CreateFiles | FileSystemRights.AppendData)) != 0 ? "write files"
        : (rights & (FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles)) != 0 ? "delete or replace files"
        : "change permissions";
}
