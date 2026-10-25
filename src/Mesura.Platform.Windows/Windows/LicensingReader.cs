using System.Management;
using Mesura.Core.Licensing;
using Microsoft.Win32;

namespace Mesura.Platform.Windows.Windows;

/// <summary>
/// Reads the Windows and Office licences (read-only, no elevation, no method calls): the products with a key installed
/// from <c>SoftwareLicensingProduct</c>, the description of the licence the firmware carries from
/// <c>SoftwareLicensingService</c>, and Office's Click-to-Run configuration. Only the named properties are queried: never
/// a product key, its last five characters, or the firmware key (<c>OA3xOriginalProductKey</c>), so none of them is
/// ever in Mesura's memory.
/// </summary>
public static class LicensingReader
{
    private const string ProductQuery =
        "SELECT ID, ApplicationID, Name, Description, ProductKeyChannel, LicenseStatus, LicenseStatusReason, GracePeriodRemaining, "
        + "KeyManagementServiceMachine, DiscoveredKeyManagementServiceMachineName, LicenseIsAddon "
        + "FROM SoftwareLicensingProduct WHERE PartialProductKey IS NOT NULL";

    public static ActivationStatus Read()
    {
        var windows = new List<LicenseInfo>();
        var office = new List<LicenseInfo>();
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\CIMV2", ProductQuery);
            foreach (var item in searcher.Get().OfType<ManagementObject>())
            {
                using (item)
                {
                    if (item["LicenseIsAddon"] is true || LicenseInfo.ProductOf(item["ApplicationID"] as string) is not { } product)
                    {
                        continue;
                    }

                    var info = Map(item, product);
                    (product == LicensedProduct.Windows ? windows : office).Add(info);
                }
            }
        }
        catch (Exception e) when (e is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            return ActivationStatus.Failed(e.Message);
        }

        return new ActivationStatus(windows, office, FirmwareLicense(), Office(), null);
    }

    private static LicenseInfo Map(ManagementObject item, LicensedProduct product)
    {
        var channel = item["ProductKeyChannel"] as string ?? "";
        var description = item["Description"] as string ?? "";
        var info = new LicenseInfo(product, item["Name"] as string ?? "", description, channel,
            LicenseInfo.StateOf(item["LicenseStatus"] is uint status ? status : 99u),
            item["GracePeriodRemaining"] is uint grace ? grace : 0u,
            item["LicenseStatusReason"] is uint reason ? reason : 0u,
            null, item["ID"] as string ?? "");
        if (!info.IsKmsClient)
        {
            return info;
        }

        var host = item["KeyManagementServiceMachine"] as string;
        return info with { KmsHost = string.IsNullOrWhiteSpace(host) ? item["DiscoveredKeyManagementServiceMachineName"] as string : host };
    }

    /// <summary>The description of the firmware's licence (e.g. <c>[4.0] Core OEM:DM</c>), or <c>null</c>.</summary>
    private static string? FirmwareLicense()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\CIMV2", "SELECT OA3xOriginalProductKeyDescription FROM SoftwareLicensingService");
            foreach (var item in searcher.Get().OfType<ManagementObject>())
            {
                using (item)
                {
                    return item["OA3xOriginalProductKeyDescription"] is string { Length: > 0 } text ? text : null;
                }
            }
        }
        catch (Exception e) when (e is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            // Not read: the page says nothing about the firmware licence.
        }

        return null;
    }

    /// <summary>Office installed with Click-to-Run, or <c>null</c>.</summary>
    private static OfficeInstall? Office()
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(@"SOFTWARE\Microsoft\Office\ClickToRun\Configuration");
            return key?.GetValue("ProductReleaseIds") is string { Length: > 0 } ids
                ? new OfficeInstall(ids, key.GetValue("VersionToReport") as string ?? "")
                : null;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
