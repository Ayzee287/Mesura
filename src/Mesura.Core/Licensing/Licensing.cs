using System.Globalization;
using System.Text;

namespace Mesura.Core.Licensing;

/// <summary>Which product a licence belongs to.</summary>
public enum LicensedProduct
{
    Windows,
    Office,
}

/// <summary>A licence's state, as <c>SoftwareLicensingProduct.LicenseStatus</c> numbers it (0 to 6).</summary>
public enum LicenseState
{
    Unlicensed = 0,
    Licensed = 1,

    /// <summary>Out-of-box grace: installed, not activated yet.</summary>
    OobGrace = 2,

    /// <summary>Out-of-tolerance grace: the hardware changed too much.</summary>
    OotGrace = 3,

    NonGenuineGrace = 4,
    Notification = 5,
    ExtendedGrace = 6,
    Unknown = 99,
}

/// <summary>
/// One installed licence, read from <c>SoftwareLicensingProduct</c> (a product with a key installed that isn't an
/// add-on). No product key is held: not the key, and not its last five characters (vault privacy rule: no activation
/// details are kept). The KMS host is read only for a KMS client key, where it says where activation comes from.
/// </summary>
/// <param name="Channel">The key's channel as Windows reports it (<c>Retail</c>, <c>OEM:DM</c>, <c>Volume:GVLK</c>, <c>Volume:MAK</c>, …), shown as is.</param>
/// <param name="GraceMinutes">Minutes before the product goes into notification mode (for a KMS client: before reactivation is due).</param>
/// <param name="SkuId">The licence's id (<c>SoftwareLicensingProduct.ID</c>), which activation is asked for.</param>
public sealed record LicenseInfo(
    LicensedProduct Product, string Name, string Description, string Channel, LicenseState State,
    uint GraceMinutes, uint StatusReason, string? KmsHost, string SkuId)
{
    /// <summary>The application id Windows licences carry.</summary>
    public const string WindowsApplicationId = "55c92734-d682-4d71-983e-d6ec3f16059f";

    /// <summary>The application id Office volume licences carry.</summary>
    public const string OfficeApplicationId = "0ff1ce15-a989-479d-af46-f275c6370663";

    /// <summary>Days left, rounded up as <c>slmgr /xpr</c> does; <c>null</c> when there is no deadline.</summary>
    public int? GraceDays => GraceMinutes == 0 ? null : (int)((GraceMinutes + 1439L) / 1440);

    /// <summary>A KMS client key (GVLK): activation comes from a KMS host and must be renewed.</summary>
    public bool IsKmsClient =>
        Channel.Contains("GVLK", StringComparison.OrdinalIgnoreCase) || Description.Contains("KMSCLIENT", StringComparison.OrdinalIgnoreCase);

    public bool IsMak => Channel.Contains("MAK", StringComparison.OrdinalIgnoreCase) || Description.Contains("MAK", StringComparison.Ordinal);

    public bool IsRetail => Channel.StartsWith("Retail", StringComparison.OrdinalIgnoreCase) || Description.Contains("RETAIL channel", StringComparison.OrdinalIgnoreCase);

    public bool IsOem => Channel.StartsWith("OEM", StringComparison.OrdinalIgnoreCase) || Description.Contains("OEM_", StringComparison.OrdinalIgnoreCase);

    /// <summary>The licence state from the WMI number; an unexpected number is <see cref="LicenseState.Unknown"/>.</summary>
    public static LicenseState StateOf(uint status) => status <= 6 ? (LicenseState)status : LicenseState.Unknown;

    /// <summary>Which product an application id belongs to, or <c>null</c> for another product.</summary>
    public static LicensedProduct? ProductOf(string? applicationId) =>
        string.Equals(applicationId, WindowsApplicationId, StringComparison.OrdinalIgnoreCase) ? LicensedProduct.Windows
        : string.Equals(applicationId, OfficeApplicationId, StringComparison.OrdinalIgnoreCase) ? LicensedProduct.Office
        : null;
}

/// <summary>Office installed with Click-to-Run, as its configuration names it (e.g. <c>ProPlus2024Volume</c>, <c>O365ProPlusRetail</c>).</summary>
public sealed record OfficeInstall(string ProductReleaseIds, string Version)
{
    /// <summary>A volume edition: its licence is in <c>SoftwareLicensingProduct</c> and takes a volume key.</summary>
    public bool IsVolume => ProductReleaseIds.Contains("Volume", StringComparison.OrdinalIgnoreCase);

    /// <summary>Microsoft 365 or a retail edition: it activates with the Microsoft account it was bought with.</summary>
    public bool ActivatesWithAccount => !IsVolume;
}

/// <summary>What the Activation page shows: the Windows and Office licences, the firmware licence's description, and Office's install.</summary>
/// <param name="FirmwareLicense">
/// The description of the licence the PC's firmware carries (<c>OA3xOriginalProductKeyDescription</c>, e.g.
/// <c>[4.0] Core OEM:DM</c>); the firmware key itself is never read.
/// </param>
/// <param name="Error">Why licensing couldn't be read, if it couldn't.</param>
public sealed record ActivationStatus(
    IReadOnlyList<LicenseInfo> Windows, IReadOnlyList<LicenseInfo> Office, string? FirmwareLicense, OfficeInstall? OfficeInstall, string? Error)
{
    public static ActivationStatus Failed(string error) => new([], [], null, null, error);

    /// <summary>The Windows licence that counts: a licensed one first, otherwise the first.</summary>
    public LicenseInfo? WindowsLicense => Windows.FirstOrDefault(w => w.State == LicenseState.Licensed) ?? Windows.FirstOrDefault();

    /// <summary>The firmware licence's edition word (<c>Core</c> is Windows Home, <c>Professional</c> is Pro), or empty.</summary>
    public string FirmwareEdition =>
        FirmwareLicense is { Length: > 0 } text && text.Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: >= 2 } words ? words[1] : "";
}

/// <summary>
/// A product key as the user typed or pasted it, made checkable: spaces and dashes of any kind dropped (a clipboard may
/// turn hyphens into en or em dashes, which Microsoft names as a cause of 0xC004F050), letters upper-case, and exactly
/// twenty-five letters or digits required, then written as five groups of five. The key is only shaped here; nothing
/// keeps it, and Windows decides whether it is valid.
/// </summary>
public static class ProductKey
{
    public static bool TryNormalize(string? input, out string key)
    {
        key = "";
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var builder = new StringBuilder(25);
        foreach (var c in input)
        {
            if (char.IsWhiteSpace(c) || char.GetUnicodeCategory(c) == UnicodeCategory.DashPunctuation || c == (char)0x2212)
            {
                continue; // a separator: a space, a hyphen, an en or em dash, a minus sign
            }

            if (!char.IsAsciiLetterOrDigit(c))
            {
                return false;
            }

            builder.Append(char.ToUpperInvariant(c));
        }

        if (builder.Length != 25)
        {
            return false;
        }

        var text = builder.ToString();
        key = string.Join('-', Enumerable.Range(0, 5).Select(i => text.Substring(i * 5, 5)));
        return true;
    }
}

/// <summary>
/// Microsoft's activation error codes, as a key into the app's texts. Some codes mean different things for a KMS client
/// key and for a retail or MAK key (0xC004F074, 0xC004C008), so the channel picks the meaning (Microsoft's IT-pro page
/// for KMS and MAK, its consumer page for retail and OEM; both read 2026-10-23).
/// </summary>
public static class ActivationErrors
{
    /// <summary>The text key for an error (<c>Activation.Error.*</c>), or <c>Activation.Error.Other</c>.</summary>
    public static string Key(uint code, bool kmsClient) => code switch
    {
        0xC004F074 => kmsClient ? "Activation.Error.NoKmsHost" : "Activation.Error.WrongEdition",
        0xC004C008 => kmsClient ? "Activation.Error.KmsLimit" : "Activation.Error.UsedElsewhere",
        0xC004C003 => "Activation.Error.Blocked",
        0xC004F050 or 0xC004F034 => "Activation.Error.Invalid",
        0xC004C020 => "Activation.Error.MakLimit",
        0xC004F014 or 0xC004F213 => "Activation.Error.NoKey",
        0xC004F00F => "Activation.Error.Hardware",
        0x8007232B or 0x8007007B => "Activation.Error.KmsDns",
        0x80070005 => "Activation.Error.AccessDenied",
        0xC004F009 => "Activation.Error.GraceExpired",
        0x803F7001 => "Activation.Error.NoLicence",
        0xC004C060 or 0x803FA067 => "Activation.Error.NotValid",
        0xC004E016 => "Activation.Error.OtherEdition",
        0x80072F8F or 0x80072EE7 or 0x80072EFD => "Activation.Error.Connection",
        _ => "Activation.Error.Other",
    };

    /// <summary>An error code as Windows writes it, e.g. <c>0xC004F074</c>.</summary>
    public static string Hex(uint code) => "0x" + code.ToString("X8", CultureInfo.InvariantCulture);

    /// <summary>
    /// Whether an activation failed because of the key itself (blocked, invalid, its limit reached, used elsewhere, for
    /// another edition), so trying again with the same key can't help and another key is the way on.
    /// </summary>
    public static bool IsKeyProblem(uint code) => code is 0xC004C003 or 0xC004F050 or 0xC004F034 or 0xC004C020 or 0xC004C060
        or 0x803FA067 or 0xC004E016 or 0xC004C008;
}
