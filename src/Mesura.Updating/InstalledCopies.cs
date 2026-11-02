using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Mesura.Updating;

/// <summary>
/// One Windows Installer registration of Mesura: a per-user copy (HKCU) or an all-users copy (HKLM), by its product code. Windows Installer keeps the two
/// kinds apart as two products, so a PC can hold both after someone installs for the other scope.
/// </summary>
public sealed record InstalledCopy(InstallScope Scope, string ProductCode, string Folder, string Version);

/// <summary>What <see cref="CopyTransition.Plan"/> decided about the copies on this PC.</summary>
public enum TransitionKind
{
    /// <summary>Nothing to do: only this copy is installed (or this one isn't an installed copy at all).</summary>
    None,

    /// <summary>A copy of the other kind is installed beside this one, and it can be removed safely by its own product code.</summary>
    Offer,

    /// <summary>A copy of the other kind exists but removing it by product code would be ambiguous or could remove this copy: left to the user.</summary>
    Manual,
}

public sealed record TransitionPlan(TransitionKind Kind, InstalledCopy? Other, string Reason = "");

/// <summary>How an attempt to remove the other copy ended.</summary>
public enum RemovalStatus
{
    Removed,

    /// <summary>The user declined the administrator prompt or cancelled the uninstall: nothing changed.</summary>
    Cancelled,

    /// <summary>The uninstall failed, or finished and the copy is still registered: both copies remain.</summary>
    Failed,

    /// <summary>The uninstall finished but this copy's own registration is gone: Mesura must be installed again. The check exists so this is never silent.</summary>
    OwnCopyLost,
}

public sealed record RemovalResult(RemovalStatus Status, int ExitCode, string Detail);

/// <summary>Reads the Windows Installer registrations of Mesura (a stand-in in tests).</summary>
public interface IInstalledCopies
{
    IReadOnlyList<InstalledCopy> Read();
}

/// <summary>Runs <c>msiexec /x</c> for a product code and returns its exit code (a stand-in in tests).</summary>
public interface ICopyUninstaller
{
    /// <param name="productCode">The product to remove, in braces.</param>
    /// <param name="elevate">Ask Windows for administrator rights first (an all-users copy, from an unelevated Mesura).</param>
    int Uninstall(string productCode, bool elevate);
}

/// <summary>
/// Moving between "only for me" and "for all users" with the user's consent (0.5). Windows Installer cannot upgrade across the two kinds, so installing the
/// other kind leaves the first copy beside the new one. This finds that copy and, when the user agrees, removes it with its own product code; the copy being
/// removed is always the one this process is <em>not</em> running from, so a failure leaves both copies and never none.
/// </summary>
public static partial class CopyTransition
{
    /// <summary>
    /// Decides, from the registrations and the running executable, whether another copy exists and can be offered for removal. Only a copy of the <em>other</em>
    /// scope is considered, only when the running executable is itself an installed copy, and only when the product codes differ: one product code in both scopes
    /// would make <c>msiexec /x</c> ambiguous (it could remove the copy being used), so that case is left to the user.
    /// </summary>
    public static TransitionPlan Plan(string runningExecutable, IReadOnlyList<InstalledCopy> copies)
    {
        ArgumentNullException.ThrowIfNull(copies);
        var running = copies.FirstOrDefault(c => SameFile(Path.Combine(c.Folder, "Mesura.exe"), runningExecutable));
        if (running is null)
        {
            return new(TransitionKind.None, null, "this copy is not an installed copy");
        }

        var others = copies.Where(c => c.Scope != running.Scope).ToList();
        if (others.Count == 0)
        {
            return new(TransitionKind.None, null);
        }

        var other = others[0];
        if (others.Count > 1 || string.Equals(other.ProductCode, running.ProductCode, StringComparison.OrdinalIgnoreCase) || SameFile(other.Folder, running.Folder))
        {
            return new(TransitionKind.Manual, other, "the two copies share a product code or a folder, or there is more than one other copy");
        }

        return new(TransitionKind.Offer, other);
    }

    /// <summary>
    /// Removes the copy of a plan that is an <see cref="TransitionKind.Offer"/>, then reads the registrations again: the copy must be gone and this copy's own
    /// registration must still be there.
    /// </summary>
    public static RemovalResult Remove(TransitionPlan plan, bool elevated, string runningExecutable, IInstalledCopies registrations, ICopyUninstaller uninstaller)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(registrations);
        ArgumentNullException.ThrowIfNull(uninstaller);
        if (plan is not { Kind: TransitionKind.Offer, Other: { } other } || !ProductCode().IsMatch(other.ProductCode))
        {
            return new(RemovalStatus.Failed, 0, "There is no copy that can be removed safely.");
        }

        int code;
        try
        {
            code = uninstaller.Uninstall(other.ProductCode, elevate: other.Scope == InstallScope.Machine && !elevated);
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            return new(RemovalStatus.Cancelled, 1223, "Windows' administrator prompt was declined; nothing was removed.");
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            return new(RemovalStatus.Failed, 0, $"The uninstall didn't start: {e.Message}");
        }

        var now = registrations.Read();
        var ownStillThere = now.Any(c => SameFile(Path.Combine(c.Folder, "Mesura.exe"), runningExecutable));
        if (code is 1602 or 1223)
        {
            return new(RemovalStatus.Cancelled, code, "The uninstall was cancelled; nothing was removed.");
        }

        if (!ownStillThere)
        {
            return new(RemovalStatus.OwnCopyLost, code, "This copy of Mesura is no longer registered. Run the Mesura installer again.");
        }

        if (code is not (0 or 3010 or 1641))
        {
            return new(RemovalStatus.Failed, code, $"Windows Installer couldn't remove the other copy (exit code {code}); both copies remain.");
        }

        return now.Any(c => string.Equals(c.ProductCode, other.ProductCode, StringComparison.OrdinalIgnoreCase))
            ? new(RemovalStatus.Failed, code, "The uninstall finished but the other copy is still registered; both copies remain.")
            : new(RemovalStatus.Removed, code, code == 3010 ? "Removed; Windows asks for a restart to finish." : "Removed.");
    }

    private static bool SameFile(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^\{[0-9A-Fa-f]{8}-([0-9A-Fa-f]{4}-){3}[0-9A-Fa-f]{12}\}$", RegexOptions.CultureInvariant)]
    private static partial Regex ProductCode();
}

/// <summary>
/// Reads Mesura's registrations from Windows Installer itself (<c>MsiEnumProductsEx</c>, which names the install context of each product: for the current user or for the
/// machine), then the install folder the installer recorded in that context's own <c>Software\Mesura</c> key. The context cannot be read from where the Uninstall entry sits:
/// measured in Windows Sandbox (2026-11-02), a per-user package installed from an elevated process has its Uninstall entry under HKLM although Windows Installer records it
/// as a per-user product, so the hive of an Uninstall entry says nothing about the copy's scope.
/// </summary>
public sealed class WindowsInstallerCopies : IInstalledCopies
{
    private const uint ContextUserUnmanaged = 2;
    private const uint ContextMachine = 4;
    private const uint Ok = 0;
    private const uint NoMoreItems = 259;

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint MsiEnumProductsExW(string? productCode, string? userSid, uint context, uint index, StringBuilder installedProductCode, out uint installedContext, StringBuilder? sid, ref uint sidLength);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint MsiGetProductInfoExW(string productCode, string? userSid, uint context, string property, StringBuilder? value, ref uint valueLength);

    public IReadOnlyList<InstalledCopy> Read()
    {
        var copies = new List<InstalledCopy>();
        try
        {
            for (uint index = 0; ; index++)
            {
                var code = new StringBuilder(39);
                uint sidLength = 0;
                var result = MsiEnumProductsExW(null, null, ContextUserUnmanaged | ContextMachine, index, code, out var context, null, ref sidLength);
                if (result == NoMoreItems || result != Ok)
                {
                    break;
                }

                var productCode = code.ToString();
                if (Info(productCode, context, "ProductName") != "Mesura")
                {
                    continue;
                }

                var scope = context == ContextMachine ? InstallScope.Machine : InstallScope.User;
                var folder = InstallFolder(scope);
                if (folder is null)
                {
                    continue; // registered with Windows Installer but without the folder the installer records: not a copy this code can reason about
                }

                copies.Add(new InstalledCopy(scope, productCode.ToUpperInvariant(), folder, Info(productCode, context, "VersionString") ?? ""));
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or System.Security.SecurityException)
        {
            // No Windows Installer API: nothing is known.
        }

        return copies;
    }

    private static string? Info(string productCode, uint context, string property)
    {
        uint length = 0;
        var probe = MsiGetProductInfoExW(productCode, null, context, property, null, ref length);
        if (probe is not (Ok or 234)) // 234: more data (the length it needs)
        {
            return null;
        }

        var value = new StringBuilder((int)length + 1);
        length++;
        return MsiGetProductInfoExW(productCode, null, context, property, value, ref length) == Ok ? value.ToString() : null;
    }

    private static string? InstallFolder(InstallScope scope)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(scope == InstallScope.Machine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Registry64);
            using var own = root.OpenSubKey(InstallRegistration.KeyPath);
            return own?.GetValue("InstallDir") is string { Length: > 0 } folder ? InstallRegistration.NormalizeFolder(folder) : null;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}

/// <summary>The one place the copy transition starts <c>msiexec</c> (architecture §2.3), only on the user's confirmed choice to remove the other copy.</summary>
#pragma warning disable RS0030 // The process launcher of the copy transition (class remarks).
public sealed class MsiexecUninstaller : ICopyUninstaller
{
    public int Uninstall(string productCode, bool elevate)
    {
        // /qb: Windows Installer's own basic progress box, so the user sees it happen; /norestart: never restart without asking.
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "msiexec.exe"), $"/x {productCode} /norestart /qb")
        {
            UseShellExecute = elevate,
            Verb = elevate ? "runas" : "",
        };

        using var process = Process.Start(start) ?? throw new InvalidOperationException("msiexec didn't start.");
        process.WaitForExit();
        return process.ExitCode;
    }
}
