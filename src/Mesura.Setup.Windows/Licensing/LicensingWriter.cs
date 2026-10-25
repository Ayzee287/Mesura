using System.Management;
using System.Runtime.InteropServices;

namespace Mesura.Setup.Windows.Licensing;

/// <summary>What a licensing call ended with: done, or the error code Windows returned.</summary>
public sealed record LicensingResult(bool Ok, uint Code, string Message)
{
    public static readonly LicensingResult Done = new(true, 0, "");
}

/// <summary>
/// The setup layer's one owner of licensing changes (architecture §2.3, D031), each on the user's confirmed request:
/// install a product key the user owns, activate an installed licence, and clear a retail key from the registry
/// afterwards (as <c>slmgr /cpky</c> does). The key goes to <c>SoftwareLicensingService.InstallProductKey</c> in this
/// process, never on a command line (which process auditing can log), and is never stored or logged. Nothing here sets a
/// KMS host, installs a KMS client key on its own, rearms or calls any other licensing method.
/// </summary>
public static class LicensingWriter
{
    private const string Scope = @"root\CIMV2";

    /// <summary>Installs a product key (normalised by <c>ProductKey.TryNormalize</c>) and refreshes the licence state.</summary>
    public static LicensingResult InstallProductKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return Call(() =>
        {
            using var service = Service();
            Invoke(service, "InstallProductKey", [key]);
            Invoke(service, "RefreshLicenseStatus", null);
        });
    }

    /// <summary>Activates the licence with this id (<c>SoftwareLicensingProduct.ID</c>), online, then refreshes the state.</summary>
    public static LicensingResult Activate(string skuId)
    {
        if (!Guid.TryParse(skuId, out var id))
        {
            return new LicensingResult(false, 0, "Not a licence id.");
        }

        return Call(() =>
        {
            using var searcher = new ManagementObjectSearcher(Scope, $"SELECT ID FROM SoftwareLicensingProduct WHERE ID = '{id:D}'");
            var product = searcher.Get().OfType<ManagementObject>().FirstOrDefault()
                ?? throw new InvalidOperationException("That licence isn't installed any more.");
            using (product)
            {
                Invoke(product, "Activate", null);
            }

            using var service = Service();
            Invoke(service, "RefreshLicenseStatus", null);
        });
    }

    /// <summary>Removes the installed key from the registry (it stays installed), as Microsoft advises after a retail key.</summary>
    public static LicensingResult ClearKeyFromRegistry() => Call(() =>
    {
        using var service = Service();
        Invoke(service, "ClearProductKeyFromRegistry", null);
    });

    private static ManagementObject Service()
    {
        using var searcher = new ManagementObjectSearcher(Scope, "SELECT Version FROM SoftwareLicensingService");
        return searcher.Get().OfType<ManagementObject>().FirstOrDefault()
            ?? throw new InvalidOperationException("Windows' licensing service didn't answer.");
    }

    private static void Invoke(ManagementObject target, string method, object[]? arguments)
    {
#pragma warning disable RS0030 // The setup layer's licensing owner: a licensing method the user confirmed (class remarks).
        target.InvokeMethod(method, arguments ?? []);
#pragma warning restore RS0030
    }

    private static LicensingResult Call(Action action)
    {
        try
        {
            action();
            return LicensingResult.Done;
        }
        catch (COMException e)
        {
            return new LicensingResult(false, unchecked((uint)e.HResult), e.Message);
        }
        catch (ManagementException e)
        {
            return new LicensingResult(false, unchecked((uint)e.ErrorCode), e.Message);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or InvalidOperationException)
        {
            return new LicensingResult(false, e is UnauthorizedAccessException ? 0x80070005u : 0u, e.Message);
        }
        catch (SystemException e) when (e.HResult < 0)
        {
            // The runtime turns some Windows error codes into other exceptions (0x8007007B, a KMS host name that doesn't
            // resolve, arrives as FileNotFoundException); the code is kept so the page can word it.
            return new LicensingResult(false, unchecked((uint)e.HResult), e.Message);
        }
    }
}
