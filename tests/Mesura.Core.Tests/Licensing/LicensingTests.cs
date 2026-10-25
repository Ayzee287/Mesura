using Mesura.Core.Licensing;

namespace Mesura.Core.Tests.Licensing;

/// <summary>
/// Activation's rules that need no Windows (D031): a typed key made checkable, licence states and channels read the way
/// Windows reports them, grace days rounded as slmgr does, and error codes worded by channel. The keys below are made up
/// for the tests; none is a real product key.
/// </summary>
public sealed class LicensingTests
{
    [Theory]
    [InlineData("abcde-fghij-klmno-pqrst-uvw12", "ABCDE-FGHIJ-KLMNO-PQRST-UVW12")]
    [InlineData("  ABCDE FGHIJ-KLMNO-PQRST-UVW12 ", "ABCDE-FGHIJ-KLMNO-PQRST-UVW12")]
    [InlineData("ABCDE—FGHIJ–KLMNO−PQRST‐UVW12", "ABCDE-FGHIJ-KLMNO-PQRST-UVW12")] // dashes a clipboard brought
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVW12", "ABCDE-FGHIJ-KLMNO-PQRST-UVW12")]
    public void A_typed_key_is_shaped_into_five_groups(string typed, string key)
    {
        Assert.True(ProductKey.TryNormalize(typed, out var shaped));
        Assert.Equal(key, shaped);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("ABCDE-FGHIJ-KLMNO-PQRST")]
    [InlineData("ABCDE-FGHIJ-KLMNO-PQRST-UVW1")]
    [InlineData("ABCDE-FGHIJ-KLMNO-PQRST-UVW1!")]
    [InlineData("ABCDE-FGHIJ-KLMNO-PQRST-UVW12-XYZ")]
    public void Anything_else_isnt_a_key(string? typed) => Assert.False(ProductKey.TryNormalize(typed, out _));

    private static LicenseInfo License(string channel, string description, uint status, uint grace, string? kmsHost = null) =>
        new(LicensedProduct.Windows, "Windows(R), Professional edition", description, channel, LicenseInfo.StateOf(status), grace, 0, kmsHost,
            "00000000-0000-0000-0000-000000000001");

    [Fact]
    public void Channels_read_as_windows_reports_them()
    {
        var kms = License("Volume:GVLK", "Windows(R) Operating System, VOLUME_KMSCLIENT channel", 1, 217942, "kms.example.org");
        Assert.True(kms.IsKmsClient);
        Assert.False(kms.IsRetail);
        Assert.Equal(152, kms.GraceDays); // 217942 minutes rounded up to whole days, as slmgr /xpr does
        Assert.Equal(LicenseState.Licensed, kms.State);

        var mak = License("Volume:MAK", "Office 24, RETAIL(MAK) channel", 2, 2014);
        Assert.True(mak.IsMak);
        Assert.False(mak.IsKmsClient);
        Assert.Equal(2, mak.GraceDays);
        Assert.Equal(LicenseState.OobGrace, mak.State);

        var retail = License("Retail", "Windows(R) Operating System, RETAIL channel", 1, 0);
        Assert.True(retail.IsRetail);
        Assert.Null(retail.GraceDays); // no deadline
        Assert.True(License("OEM:DM", "Windows(R) Operating System, OEM_DM channel", 1, 0).IsOem);
        Assert.Equal(LicenseState.Unknown, LicenseInfo.StateOf(42));
    }

    [Fact]
    public void Products_are_told_apart_by_application_id()
    {
        Assert.Equal(LicensedProduct.Windows, LicenseInfo.ProductOf("55C92734-D682-4D71-983E-D6EC3F16059F"));
        Assert.Equal(LicensedProduct.Office, LicenseInfo.ProductOf("0ff1ce15-a989-479d-af46-f275c6370663"));
        Assert.Null(LicenseInfo.ProductOf("00000000-0000-0000-0000-000000000000"));
    }

    [Fact]
    public void The_firmware_licence_names_its_edition()
    {
        var status = new ActivationStatus([], [], "[4.0] Core OEM:DM", null, null);
        Assert.Equal("Core", status.FirmwareEdition);
        Assert.Equal("", new ActivationStatus([], [], null, null, null).FirmwareEdition);
    }

    [Fact]
    public void Office_installs_tell_volume_from_account_activation()
    {
        Assert.True(new OfficeInstall("ProPlus2024Volume", "16.0.17932.21056").IsVolume);
        Assert.True(new OfficeInstall("O365ProPlusRetail", "16.0.1").ActivatesWithAccount);
    }

    [Fact]
    public void Error_codes_are_worded_by_channel()
    {
        Assert.Equal("Activation.Error.NoKmsHost", ActivationErrors.Key(0xC004F074, kmsClient: true));
        Assert.Equal("Activation.Error.WrongEdition", ActivationErrors.Key(0xC004F074, kmsClient: false));
        Assert.Equal("Activation.Error.KmsLimit", ActivationErrors.Key(0xC004C008, kmsClient: true));
        Assert.Equal("Activation.Error.UsedElsewhere", ActivationErrors.Key(0xC004C008, kmsClient: false));
        Assert.Equal("Activation.Error.Invalid", ActivationErrors.Key(0xC004F050, kmsClient: false));
        Assert.Equal("Activation.Error.Other", ActivationErrors.Key(0x12345678, kmsClient: false));
        Assert.Equal("0xC004F074", ActivationErrors.Hex(0xC004F074));
    }
}
